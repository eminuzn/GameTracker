using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameTracker.Core;
using GameTracker.Infrastructure;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace GameTracker.Desktop;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsGridViewProperty = DependencyProperty.Register(nameof(IsGridView), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty GridColumnsProperty = DependencyProperty.Register(nameof(GridColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(3));
    public bool IsGridView { get => (bool)GetValue(IsGridViewProperty); set => SetValue(IsGridViewProperty, value); }
    public int GridColumns { get => (int)GetValue(GridColumnsProperty); set => SetValue(GridColumnsProperty, value); }
    private readonly LocalStore store;
    private readonly ObservableCollection<Game> games;
    private readonly List<LibraryRoot> roots;
    private readonly SessionTracker tracker;
    private readonly CoverClient covers;
    private readonly InitialPlayHistory initialPlayHistory;
    private readonly HttpClient versionHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SteamVersionClient versions;
    private readonly LibraryViewSettings viewSettings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer rescanTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private CancellationTokenSource? scan;
    private CancellationTokenSource? versionScan;
    private bool busy, ticking, exiting, detailDirty, versionDirty, editingCover, initializingView = true;
    private long lastRootCheck;
    private readonly Dictionary<Guid, bool> rootAvailability = [];
    private Game? Selected => GamesList.SelectedItem as Game;

    public MainWindow() : this(new LocalStore()) { }
    public MainWindow(LocalStore store)
    {
        this.store = store;
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/GameTracker;component/Assets/GameTracker.ico"));
        SourceInitialized += (_, _) => WindowBounds.Attach(this);
        roots = store.Roots(); games = new(store.Games());
        viewSettings = store.LoadViewSettings();
        tracker = new(store); covers = new(store, DesktopSettings.Load()); initialPlayHistory = new(store);
        versionHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GameTracker/1.0");
        versions = new(versionHttp);
        GamesList.ItemsSource = games;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(games);
        view.Filter = x => x is Game g && GameListComparer.Matches(g, SearchBox.Text, viewSettings.FavoritesOnly, viewSettings.ExeSelectionOnly);
        if (view.CanChangeLiveSorting)
        {
            foreach (var property in new[] { nameof(Game.Name), nameof(Game.LastPlayedAt), nameof(Game.SizeBytes) }) view.LiveSortingProperties.Add(property);
            view.IsLiveSorting = true;
        }
        SortBox.SelectedIndex = (int)viewSettings.SortBy;
        FavoritesOnlyBox.IsChecked = viewSettings.FavoritesOnly;
        ExeSelectionOnlyBox.IsChecked = viewSettings.ExeSelectionOnly;
        initializingView = false; ApplyListOptions(false);
        using (var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/GameTracker;component/Assets/GameTracker.ico")).Stream)
        using (var resourceIcon = new System.Drawing.Icon(iconStream, 32, 32))
            trayIcon = (System.Drawing.Icon)resourceIcon.Clone();
        tray = new Forms.NotifyIcon { Text = "GameTracker · takip açık", Icon = trayIcon, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("GameTracker'ı aç", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Tamamen çık", null, (_, _) => Dispatcher.Invoke(Exit)); tray.ContextMenuStrip = menu;
        Closing += Window_Closing;
        monitor.Tick += Monitor_Tick; monitor.Start();
        rescanTimer.Tick += async (_, _) => { rescanTimer.Stop(); if (!busy) await ScanAsync(); else ScheduleScan(); };
        SystemEvents.PowerModeChanged += PowerChanged;
        NameBox.TextChanged += (_, _) => detailDirty = true;
        VersionBox.TextChanged += (_, _) => { detailDirty = true; versionDirty = true; };
        ArgsBox.TextChanged += (_, _) => detailDirty = true;
        WorkingBox.TextChanged += (_, _) => detailDirty = true;
        Loaded += async (_, _) => { await ScanAsync(); };
        UpdateSummary(); SetupWatchers();
    }

    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true; Hide(); tray.ShowBalloonTip(1800, "GameTracker", "Oyun takibi sistem tepsisinde devam ediyor.", Forms.ToolTipIcon.Info);
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();
    private void Exit()
    {
        if (busy) { MessageBox.Show(this, "Devam eden işlem bitince tekrar çıkmayı dene. Taramayı durdurabilirsin.", "GameTracker"); return; }
        exiting = true; lifetime.Cancel(); monitor.Stop(); rescanTimer.Stop();
        SystemEvents.PowerModeChanged -= PowerChanged;
        foreach (var watcher in watchers) watcher.Dispose();
        versionScan?.Cancel(); versionHttp.Dispose();
        tracker.Stop(); tray.Dispose(); trayIcon.Dispose(); Close(); System.Windows.Application.Current.Shutdown();
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() => tracker.Suspend());

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFolderDialog { Title = "Her alt klasörü bir oyun olan ana dizini seç" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            PathPolicy.ValidateRoot(dialog.FolderName, roots);
            var root = new LibraryRoot { Path = PathPolicy.Normalize(dialog.FolderName) };
            store.SaveRoot(root); roots.Add(root); SetupWatchers(); await ScanAsync();
        }
        catch (Exception ex) { ShowError("Klasör ekleme", ex); }
    }
    private async void Rescan_Click(object sender, RoutedEventArgs e) { if (!busy) await ScanAsync(); }
    private void CancelScan_Click(object sender, RoutedEventArgs e) { scan?.Cancel(); versionScan?.Cancel(); }
    private void UpdateCancelButton() => CancelButton.Visibility = scan is not null || versionScan is not null ? Visibility.Visible : Visibility.Collapsed;
    private void ScheduleScan()
    {
        if (exiting) return;
        rescanTimer.Stop(); rescanTimer.Start();
    }
    private void SetupWatchers()
    {
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        foreach (var root in roots.Where(x => Directory.Exists(x.Path)))
        {
            try
            {
                var watcher = new FileSystemWatcher(root.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
                watcher.Changed += WatchChanged; watcher.Created += WatchChanged; watcher.Deleted += WatchChanged; watcher.Renamed += WatchChanged;
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(ScheduleScan);
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { store.Log("Watcher", ex); }
        }
    }
    private void WatchChanged(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(ScheduleScan);

    private async Task ScanAsync()
    {
        if (busy || exiting) return; busy = true;
        scan = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); CancelButton.Visibility = Visibility.Visible;
        try
        {
            foreach (var root in roots)
            {
                scan.Token.ThrowIfCancellationRequested();
                if (!Directory.Exists(root.Path))
                { foreach (var g in games.Where(x => x.RootId == root.Id)) { g.Available = false; g.Refresh(); store.SaveGame(g); } continue; }
                string[] folders;
                try
                {
                    PathPolicy.EnsureNoLinks(root.Path);
                    folders = await Task.Run(() => Directory.GetDirectories(root.Path), scan.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { store.Log("Kök tarama", ex); foreach (var g in games.Where(x => x.RootId == root.Id)) { g.Available = false; store.SaveGame(g); g.Refresh(); } continue; }
                foreach (var existing in games.Where(x => x.RootId == root.Id && !x.Deleted))
                { existing.Available = folders.Any(x => PathPolicy.Equal(x, existing.FolderPath)); store.SaveGame(existing); existing.Refresh(); }
                foreach (var folder in folders)
                {
                    scan.Token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                    var game = games.FirstOrDefault(x => !x.Deleted && PathPolicy.Equal(x.FolderPath, folder));
                    if (game?.Hidden == true) continue;
                    if (game is not null && game.Name == Path.GetFileName(folder)) game.Name = TitleMatcher.Clean(game.Name);
                    if (game is null)
                    {
                        game = new Game { RootId = root.Id, RootPath = root.Path, FolderPath = folder, Name = TitleMatcher.Clean(Path.GetFileName(folder)), InitialPlayHistoryPending = true };
                        if (string.IsNullOrWhiteSpace(game.Name)) game.Name = Path.GetFileName(folder);
                        games.Add(game);
                    }
                    try { GameOperations.BindIdentity(game); }
                    catch (IOException ex) { game.Available = false; store.SaveGame(game); game.Refresh(); store.Log("Klasör kimliği", ex); continue; }
                    StatusText.Text = "Taranıyor: " + game.Name;
                    var result = await Task.Run(() => GameScanner.Scan(folder, game.Name, scan.Token), scan.Token);
                    game.SizeBytes = result.Bytes; game.SkippedEntries = result.Skipped; game.Candidates = result.Candidates;
                    game.SizeUpdatedAt = DateTimeOffset.UtcNow; game.Available = true;
                    if (game.LaunchExe is not null)
                    {
                        try { PathPolicy.ResolveExe(game, game.LaunchExe); }
                        catch (IOException) { game.LaunchExe = null; game.ManualExe = false; }
                    }
                    if (game.LaunchExe is null && GameScanner.Suggest(result.Candidates) is { } suggested) game.LaunchExe = suggested.RelativePath;
                    await DetectVersionAsync(game, scan.Token);
                    store.SaveGame(game); game.Refresh();
                    await initialPlayHistory.ImportAsync(game, scan.Token);
                    if (GameListComparer.IsVisible(game)) _ = FetchCoverAsync(game);
                }
            }
            StatusText.Text = $"Tarama tamamlandı · {games.Count(x => GameListComparer.IsVisible(x) && x.Available && x.NeedsExeSelection)} oyun için exe seçimi gerekiyor.";
            SetupWatchers();
        }
        catch (OperationCanceledException) { StatusText.Text = "Tarama durduruldu. Tamamlanan kayıtlar korundu."; }
        catch (Exception ex) { ShowError("Tarama", ex); }
        finally { scan.Dispose(); scan = null; busy = false; UpdateCancelButton(); UpdateSummary(); UpdateDetails(false); }
    }
    private async Task FetchCoverAsync(Game game)
    {
        try { await covers.ResolveAsync(game, lifetime.Token); if (Selected == game) UpdateDetails(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { store.Log("Kapak kuyruğu", ex); }
    }
    private async void Monitor_Tick(object? sender, EventArgs e)
    {
        if (ticking || exiting) return; ticking = true;
        try
        {
            var processes = await Task.Run(WindowsProcesses.Snapshot);
            if (exiting) return;
            tracker.Observe(games, processes, Environment.TickCount64, DateTimeOffset.UtcNow);
            if (Environment.TickCount64 - lastRootCheck > 30000)
            {
                lastRootCheck = Environment.TickCount64;
                foreach (var root in roots)
                {
                    var available = Directory.Exists(root.Path);
                    if (rootAvailability.TryGetValue(root.Id, out var previous) && previous != available) ScheduleScan();
                    rootAvailability[root.Id] = available;
                }
                foreach (var game in games.Where(g => GameListComparer.IsVisible(g) && g.CoverPath is null && g.CoverRetryAt <= DateTimeOffset.UtcNow)) _ = FetchCoverAsync(game);
            }
            UpdateDetails(false);
        }
        catch (Exception ex) { store.Log("Süreç takibi", ex); StatusText.Text = "Bazı süreçler takip edilemedi; log kaydedildi."; }
        finally { ticking = false; }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (games is not null) UpdateSummary(); }
    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initializingView || games is null || SortBox.SelectedIndex < 0) return;
        viewSettings.SortBy = (GameSortOrder)SortBox.SelectedIndex;
        viewSettings.Descending = viewSettings.SortBy != GameSortOrder.Name;
        ApplyListOptions(true);
    }
    private void ReverseSort_Click(object sender, RoutedEventArgs e)
    {
        viewSettings.Descending = !viewSettings.Descending; ApplyListOptions(true);
    }
    private void FavoritesOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (initializingView || games is null) return;
        viewSettings.FavoritesOnly = FavoritesOnlyBox.IsChecked == true; ApplyListOptions(true);
    }
    private void ApplyListOptions(bool save)
    {
        IsGridView = viewSettings.Layout == LibraryLayout.Grid;
        GridColumns = viewSettings.GridColumns;
        initializingView = true;
        ListViewButton.IsChecked = !IsGridView; GridViewButton.IsChecked = IsGridView;
        ThreeColumnsButton.IsChecked = GridColumns == 3; FourColumnsButton.IsChecked = GridColumns == 4;
        GridColumnOptions.Visibility = IsGridView ? Visibility.Visible : Visibility.Collapsed;
        initializingView = false;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(games);
        view.CustomSort = new GameListComparer(viewSettings.SortBy, viewSettings.Descending);
        ReverseSortButton.Content = viewSettings.SortBy switch
        {
            GameSortOrder.LastPlayed => viewSettings.Descending ? "Yeni → Eski" : "Eski → Yeni",
            GameSortOrder.Size => viewSettings.Descending ? "Büyük → Küçük" : "Küçük → Büyük",
            _ => viewSettings.Descending ? "Z → A" : "A → Z"
        };
        if (save)
        {
            try { store.SaveViewSettings(viewSettings); }
            catch (Exception ex) { ShowError("Liste ayarlarını kaydetme", ex); }
        }
        UpdateSummary();
    }
    private void ExeSelectionOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (initializingView || games is null) return;
        viewSettings.ExeSelectionOnly = ExeSelectionOnlyBox.IsChecked == true; ApplyListOptions(true);
    }
    private void ViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (initializingView || games is null) return;
        var selected = Selected;
        viewSettings.Layout = sender == GridViewButton ? LibraryLayout.Grid : LibraryLayout.List;
        ApplyListOptions(true);
        if (selected is not null) Dispatcher.BeginInvoke(() => GamesList.ScrollIntoView(selected), DispatcherPriority.Loaded);
    }
    private void GridColumns_Changed(object sender, RoutedEventArgs e)
    {
        if (initializingView || games is null) return;
        viewSettings.GridColumns = sender == FourColumnsButton ? 4 : 3; ApplyListOptions(true);
    }
    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        var game = (sender as FrameworkElement)?.DataContext as Game ?? Selected;
        if (game is null) return;
        var previous = game.IsFavorite;
        try
        {
            game.IsFavorite = !previous; store.SaveGame(game); game.Refresh();
            UpdateSummary(); UpdateDetails(false);
        }
        catch (Exception ex) { game.IsFavorite = previous; game.Refresh(); ShowError("Favori kaydetme", ex); }
    }
    private void UpdateSummary()
    {
        var view = CollectionViewSource.GetDefaultView(games); view.Refresh();
        var visible = view.Cast<Game>().ToArray();
        SummaryText.Text = $"{visible.Length} oyun · {visible.Sum(x => x.SizeBytes) / 1e9:N1} GB";
        EmptyText.Text = !games.Any(GameListComparer.IsVisible) ? roots.Count == 0
            ? "Oyun klasörünü ekleyerek başla.\n\nHer alt klasör bir oyun olarak taranacak."
            : "Gösterilecek oyun bulunamadı.\n\nExe seçilmemiş 50 MB altı klasörler listede gösterilmez."
            : viewSettings.ExeSelectionOnly ? "Bu filtreye uygun exe seçilmesi gereken oyun yok.\n\nArama ve favori filtresini de kontrol edebilirsin."
            : viewSettings.FavoritesOnly ? "Bu filtreye uygun favori oyun yok.\n\nOyun kartındaki ☆ ile favorilere ekleyebilirsin." : "Aramaya uygun oyun bulunamadı.";
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Game_Selected(object sender, SelectionChangedEventArgs e) => UpdateDetails(true);
    private void UpdateDetails(bool populate)
    {
        var g = Selected; PlayButton.IsEnabled = g is { Available: true, LaunchExe: not null, IsRunning: false } && !busy;
        DetailExeWarning.Visibility = g?.NeedsExeSelection == true ? Visibility.Visible : Visibility.Collapsed;
        FavoriteButton.IsEnabled = g is not null;
        ManualCoverButton.IsEnabled = g is not null && !editingCover;
        RetryCoverButton.IsEnabled = g is not null && !editingCover;
        ShoppingSearchButton.IsEnabled = g is not null;
        CheckVersionsButton.IsEnabled = !busy && versionScan is null;
        CheckVersionButton.IsEnabled = g is { HasVersion: true } && !busy && versionScan is null;
        DetectVersionButton.IsEnabled = g is { Available: true } && !busy;
        VersionCheckText.Text = g?.VersionStatusText ?? "";
        VersionCheckText.ToolTip = g?.VersionTooltip;
        VersionCheckText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(g?.IsOutdated == true ? "#FFCA66" : "#8E9AB3"));
        VersionSourceText.Text = g?.VersionSource is { } source ? "Kaynak: " + source : "";
        VersionNewsButton.IsEnabled = g?.VersionCheckUrl is not null;
        if (g?.SteamVersion is { } steamVersion) VersionCheckText.Text += " · Steam: " + steamVersion;
        RetryCoverButton.Content = g?.ManualCover == true ? "Otomatik kapağa dön" : "Kapağı yeniden ara";
        FavoriteButton.Content = g is null ? "☆ Favorilere ekle" : g.FavoriteGlyph + " " + g.FavoriteActionText;
        if (g is null) { DetailTitle.Text = "Bir oyun seç"; DetailInfo.Text = ""; SessionsText.Text = ""; AttributionText.Text = ""; NameBox.Text = VersionBox.Text = ArgsBox.Text = WorkingBox.Text = ""; detailDirty = versionDirty = false; return; }
        DetailTitle.Text = g.Name;
        DetailInfo.Text = $"{g.StatusText}\n{g.SizeText} · {g.TimeText}\nSon oynama: {g.LastPlayedText}\n\n{g.FolderPath}\nExe: {g.LaunchExe ?? "Henüz seçilmedi"}\nBoyut ölçümü: {g.SizeUpdatedAt?.ToLocalTime():dd.MM HH:mm}";
        if (g.LastPlayedFromPrefetch) DetailInfo.Text += "\nSon oynama kaynağı: Windows Prefetch";
        if (populate || !detailDirty) { NameBox.Text = g.Name; VersionBox.Text = g.LocalVersion ?? ""; ArgsBox.Text = g.Arguments; WorkingBox.Text = g.WorkingDirectory ?? ""; detailDirty = versionDirty = false; }
        SessionsText.Text = string.Join("\n\n", store.Sessions(g.Id).Select(s => $"{s.StartedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {TimeSpan.FromMilliseconds(s.DurationMs).TotalMinutes:N0} dk\n{(s.Status == "Running" ? "Çalışıyor" : s.Status == "Completed" ? "Tamamlandı" : "Takip kesildi")}"));
        AttributionText.Text = g.ManualCover ? "Kapak: elle seçildi" : g.CoverProvider is null ? g.CoverStatus : "Kapak kaynağı: " + g.CoverProvider + "\n" + g.CoverAttribution;
    }
    private async void SelectExe_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { } game) return;
        var picker = new ExePicker(game) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            game.LaunchExe = picker.SelectedRelativePath; game.ManualExe = true;
            store.SaveGame(game); game.Refresh(); UpdateSummary(); UpdateDetails(true);
            try
            {
                await DetectVersionAsync(game, lifetime.Token);
                if (!exiting) { store.SaveGame(game); game.Refresh(); UpdateDetails(false); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!exiting) ShowError("Sürüm algılama", ex); }
        }
    }
    private void SelectRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { } game) return;
        var picker = new ExePicker(game, true) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedRelativePath is { } path)
        { if (!game.RuntimeExes.Contains(path)) game.RuntimeExes.Add(path); store.SaveGame(game); StatusText.Text = "Takip exe'si kaydedildi: " + path; }
    }
    private void Game_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2 || busy) return;
        var item = GameCardInteraction.FindItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not Game game || !game.Available || game.IsRunning) return;
        GamesList.SelectedItem = game; e.Handled = true; Play_Click(sender, e);
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { Available: true, IsRunning: false } game) return;
        if (game.LaunchExe is null) { SelectExe_Click(sender, e); if (game.LaunchExe is null) return; }
        try { var result = GameOperations.Launch(game); tracker.RegisterLaunch(game, result.Pid, result.Started); StatusText.Text = "Başlatıldı: " + game.Name; }
        catch (Exception ex) { ShowError("Başlatma", ex); }
    }
    private void SaveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { } game) return;
        try
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) throw new IOException("Oyun adı boş olamaz.");
            string? cwd = string.IsNullOrWhiteSpace(WorkingBox.Text) ? null : PathPolicy.Normalize(WorkingBox.Text);
            if (cwd is not null)
            { if (!Directory.Exists(cwd) || !PathPolicy.Equal(cwd, game.FolderPath) && !PathPolicy.Within(game.FolderPath, cwd)) throw new IOException("Çalışma dizini oyun klasörünün içinde olmalı."); PathPolicy.EnsureNoLinks(cwd); }
            var changed = game.Name != NameBox.Text.Trim();
            if (VersionBox.Text.Trim().Length > 80) throw new IOException("Sürüm en fazla 80 karakter olabilir.");
            var version = string.IsNullOrWhiteSpace(VersionBox.Text) ? null : GameVersionDetector.ParseText(VersionBox.Text.Trim()) ?? VersionBox.Text.Trim();
            if (versionDirty || game.LocalVersion != version)
            {
                game.LocalVersion = version; game.ManualVersion = true; game.VersionSource = "Elle girildi";
                game.VersionKind = LocalVersionKind.Release; game.ResetVersionCheck();
            }
            if (changed) game.ResetVersionCheck();
            game.Name = NameBox.Text.Trim(); game.Arguments = ArgsBox.Text; game.WorkingDirectory = cwd;
            if (changed && game.CoverPath is null) { game.CoverRetryAt = null; _ = FetchCoverAsync(game); }
            store.SaveGame(game); game.Refresh(); detailDirty = false; UpdateSummary(); UpdateDetails(true);
        }
        catch (Exception ex) { ShowError("Detay kaydetme", ex); }
    }
    private async Task DetectVersionAsync(Game game, CancellationToken ct)
    {
        if (game.ManualVersion) return;
        var exe = game.LaunchExe; var folder = game.FolderPath;
        var probe = new Game { LaunchExe = exe, FolderPath = folder, RootPath = game.RootPath };
        try
        {
            var result = await Task.Run(() => GameVersionDetector.Detect(probe, ct), ct);
            if (game.ManualVersion || game.LaunchExe != exe || game.FolderPath != folder) return;
            if (game.LocalVersion != result?.Value || game.VersionKind != (result?.Kind ?? LocalVersionKind.Unknown)) game.ResetVersionCheck();
            game.LocalVersion = result?.Value; game.VersionSource = result?.Source; game.VersionKind = result?.Kind ?? LocalVersionKind.Unknown;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { store.Log("Yerel sürüm okuma", ex); }
    }
    private async void DetectVersion_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { } game) return;
        busy = true; UpdateDetails(false);
        try
        {
            game.ManualVersion = false;
            await DetectVersionAsync(game, lifetime.Token);
            store.SaveGame(game); game.Refresh(); UpdateDetails(true);
            StatusText.Text = game.HasVersion ? "Sürüm algılandı: " + game.LocalVersion : "Dosyalarda oyun sürümü bulunamadı; elle girebilirsin.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Sürüm algılama", ex); }
        finally { busy = false; UpdateDetails(false); }
    }
    private async void CheckVersions_Click(object sender, RoutedEventArgs e)
    {
        var eligible = games.Where(g => GameListComparer.IsVisible(g) && g.Available && g.HasVersion).ToArray();
        await CheckVersionsAsync(eligible);
    }
    private async void CheckVersion_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { HasVersion: true } game) await CheckVersionsAsync([game]);
    }
    private async Task CheckVersionsAsync(Game[] eligible)
    {
        if (busy || exiting || versionScan is not null) return;
        if (eligible.Length == 0) { StatusText.Text = "Kontrol edilecek sürümü bilinen oyun yok."; return; }
        versionScan = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = versionScan.Token; UpdateCancelButton(); UpdateDetails(false);
        int completed = 0, outdated = 0, unavailable = 0;
        try
        {
            foreach (var game in eligible)
            {
                ct.ThrowIfCancellationRequested();
                if (!game.HasVersion || game.Deleted || game.Hidden) continue;
                var title = game.Name; var local = game.LocalVersion!; var kind = game.VersionKind;
                StatusText.Text = $"Steam sürümü kontrol ediliyor ({completed + 1}/{eligible.Length}): {title}";
                SteamVersionResult result;
                try { result = await versions.CheckAsync(title, local, kind, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or IOException or InvalidOperationException or KeyNotFoundException)
                { store.Log("Steam sürüm kontrolü", ex); result = new(VersionCheckState.Error, null, "Steam'e erişilemedi veya yanıt okunamadı. Daha sonra yeniden deneyebilirsin."); }
                ct.ThrowIfCancellationRequested();
                // A rescan or manual edit may have changed the game while HTTP ran.
                if (game.Name != title || game.LocalVersion != local || game.VersionKind != kind || game.Deleted || game.Hidden) continue;
                game.VersionCheck = result.State; game.SteamVersion = result.Version;
                game.VersionCheckMessage = result.Message; game.VersionCheckUrl = result.Url; game.VersionCheckedAt = DateTimeOffset.UtcNow;
                store.SaveGame(game); game.Refresh(); completed++;
                if (game.IsOutdated) outdated++;
                if (result.State is VersionCheckState.Unavailable or VersionCheckState.Error) unavailable++;
                UpdateDetails(false);
            }
            StatusText.Text = $"Sürüm kontrolü tamamlandı · {completed} oyun · {outdated} güncel değil · {unavailable} karşılaştırılamadı. Sürümü olmayanlar atlandı.";
        }
        catch (OperationCanceledException) { if (!exiting) StatusText.Text = "Sürüm kontrolü durduruldu; tamamlanan sonuçlar saklandı."; }
        catch (Exception ex) { if (!exiting) ShowError("Sürüm kontrolü", ex); }
        finally { versionScan.Dispose(); versionScan = null; if (!exiting) { UpdateCancelButton(); UpdateDetails(false); } }
    }
    private void VersionNews_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.VersionCheckUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host is "steamcommunity.com" or "store.steampowered.com")
        {
            try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Steam duyurusunu açma", ex); }
        }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } game) return;
        try { PathPolicy.ValidateGame(game); Process.Start(new ProcessStartInfo(game.FolderPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Klasör açma", ex); }
    }
    private async void RetryCover_Click(object sender, RoutedEventArgs e)
    {
        if (editingCover || Selected is not { } game) return;
        editingCover = true; UpdateDetails(false);
        try { await covers.UseAutomaticAsync(game, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Kapak arama", ex); }
        finally { editingCover = false; UpdateDetails(false); }
    }
    private async void ManualCover_Click(object sender, RoutedEventArgs e)
    {
        if (editingCover || Selected is not { } game) return;
        var picker = new OpenFileDialog { Title = "Kapak görselini seç", Filter = "Görseller|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        editingCover = true; UpdateDetails(false);
        try
        {
            var data = await Task.Run(() => CoverImageImporter.ReadPng(picker.FileName), lifetime.Token);
            await covers.ImportManualAsync(game, data, lifetime.Token);
            StatusText.Text = "Kapak değiştirildi: " + game.Name;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Kapak değiştirme", ex); }
        finally { editingCover = false; UpdateDetails(false); }
    }
    private void ShoppingSearch_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } game) return;
        try { Process.Start(new ProcessStartInfo(ShoppingMatcher.SearchUrl(game.Name)) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Google Shopping açma", ex); }
    }
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { } game) return;
        game.Hidden = true; store.SaveGame(game); UpdateSummary();
    }
    private void RestoreHidden_Click(object sender, RoutedEventArgs e)
    {
        foreach (var game in games.Where(x => x.Hidden)) { game.Hidden = false; store.SaveGame(game); }
        UpdateSummary(); ScheduleScan();
    }
    private void Rebind_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { IsRunning: false } game) return;
        var picker = new OpenFolderDialog { Title = "Bu oyunun yeni klasörünü seç" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var folder = PathPolicy.Normalize(picker.FolderName);
            var root = roots.FirstOrDefault(x => PathPolicy.DirectChild(x.Path, folder)) ?? throw new IOException("Klasör eklenmiş oyun köklerinden birinin doğrudan çocuğu olmalı.");
            if (games.Any(x => x.Id != game.Id && !x.Deleted && PathPolicy.Equal(x.FolderPath, folder))) throw new IOException("Bu klasör başka bir oyun kaydına bağlı.");
            if (MessageBox.Show(this, $"{game.Name} geçmişi şu klasöre bağlanacak:\n{folder}\n\nAynı oyun olduğundan emin misin?", "Yeniden bağlama", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var probe = new Game { RootPath = root.Path, FolderPath = folder }; GameOperations.BindIdentity(probe);
            if (game.WorkingDirectory is { } oldCwd) game.WorkingDirectory = Path.Combine(folder, Path.GetRelativePath(game.FolderPath, oldCwd));
            game.RootId = root.Id; game.RootPath = root.Path; game.FolderPath = folder; game.RootIdentity = probe.RootIdentity; game.FolderIdentity = probe.FolderIdentity; game.Available = true;
            store.SaveGame(game); game.Refresh(); UpdateDetails(true); ScheduleScan();
        }
        catch (Exception ex) { ShowError("Yeniden bağlama", ex); }
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || Selected is not { } game) return;
        var mode = MessageBox.Show(this, $"{game.Name}\n{game.FolderPath}\n\nTüm oyun klasörü kaldırılacak. İçindeki save dosyaları da dahildir.\n\nEvet: Geri dönüşüm kutusuna taşı (alan hemen boşalmaz).\nHayır: Kalıcı silme onayına geç.\nİptal: Vazgeç.", "Oyun klasörünü sil", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (mode == MessageBoxResult.Cancel) return;
        bool permanent = mode == MessageBoxResult.No;
        if (permanent && MessageBox.Show(this, $"{game.FolderPath}\n\nBu klasör KALICI olarak silinecek. Devam edilsin mi?", "Kalıcı silme", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        busy = true;
        try
        {
            StatusText.Text = "Silme kontrol ediliyor…";
            var snapshot = await Task.Run(WindowsProcesses.Snapshot);
            // Recycle dialogs must run on the WPF STA thread. Permanent IO is a background task.
            if (permanent) await Task.Run(() => GameOperations.Delete(game, true, snapshot));
            else GameOperations.Delete(game, false, snapshot);
            game.Deleted = true; game.Available = false; store.SaveGame(game); UpdateSummary(); StatusText.Text = "Oyun klasörü kaldırıldı; oturum geçmişi korundu.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Silme iptal edildi."; }
        catch (Exception ex) { ShowError("Silme tamamlanamadı", ex); ScheduleScan(); }
        finally { busy = false; UpdateDetails(false); }
    }
    private void Attribution_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.CoverAttribution is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
    private void ShowError(string context, Exception ex)
    { store.Log(context, ex); StatusText.Text = context + ": " + ex.Message; MessageBox.Show(this, ex.Message, context, MessageBoxButton.OK, MessageBoxImage.Warning); }
}
