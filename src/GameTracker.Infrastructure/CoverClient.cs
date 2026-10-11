using System.Net.Http.Json;
using System.Text.Json;
using GameTracker.Core;

namespace GameTracker.Infrastructure;

public sealed class DesktopSettings
{
    public string? CoverServiceUrl { get; set; }
    public static DesktopSettings Load()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(file)) ?? new() : new();
    }
}

public sealed class CoverClient : IDisposable
{
    private readonly LocalStore store;
    private readonly DesktopSettings settings;
    private readonly HttpClient http;
    private readonly SemaphoreSlim serial = new(1);
    public const int MaxBytes = 8 * 1024 * 1024;
    private static readonly string[] Providers = ["Steam", "SteamGridDB", "IGDB", "RAWG", "GoogleShopping"];
    public CoverClient(LocalStore store, DesktopSettings settings) : this(store, settings,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) }) { }
    public CoverClient(LocalStore store, DesktopSettings settings, HttpClient client)
    { this.store = store; this.settings = settings; http = client; http.DefaultRequestHeaders.UserAgent.ParseAdd("GameTracker/0.1"); }

    private string LookupKey(Game game) => "public-artwork-v3:" + TitleMatcher.ArtworkKey(game.Name) + ":" + settings.CoverServiceUrl;
    private bool ShouldSkip(Game game) => game.ManualCover || game.CoverPath is not null && File.Exists(game.CoverPath)
        || game.CoverLookupKey == LookupKey(game) && game.CoverRetryAt > DateTimeOffset.UtcNow;

    public async Task ResolveAsync(Game game, CancellationToken ct)
    {
        if (ShouldSkip(game)) return;
        await serial.WaitAsync(ct);
        try
        {
            if (ShouldSkip(game)) return;
            bool failed = false;
            var publicArt = new PublicArtwork(http);
            foreach (var resolve in new Func<string, CancellationToken, Task<IReadOnlyList<CoverMatch>>>[] { publicArt.Steam, publicArt.SteamGrid })
            {
                try
                {
                    using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    providerTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    foreach (var match in await resolve(game.Name, providerTimeout.Token))
                    {
                        try { await DownloadAsync(game, match, ct); return; }
                        catch (Exception ex) when (IsOptionalFailure(ex, ct)) { failed = true; store.Log("Kapak görseli", ex); }
                    }
                }
                catch (Exception ex) when (IsOptionalFailure(ex, ct)) { failed = true; store.Log("Kapak sağlayıcısı", ex); }
            }
            // Authenticated providers remain on the separately hosted service. Revisit
            // SteamGridDB there if anonymous web search failed, then IGDB, RAWG and Shopping.
            if (Uri.TryCreate(settings.CoverServiceUrl, UriKind.Absolute, out var service)
                && (service.Scheme == "https" || service.Scheme == "http" && service.IsLoopback))
            {
                var after = "Steam";
                for (var attempt = 0; attempt < Providers.Length - 1; attempt++)
                {
                    try
                    {
                        var endpoint = new Uri(service.ToString().TrimEnd('/') + "/api/covers?title=" + Uri.EscapeDataString(TitleMatcher.ArtworkTitle(game.Name)) + "&after=" + after);
                        var result = await http.GetFromJsonAsync<CoverResponse>(endpoint, ct);
                        if (result?.Match is not { } match) { failed |= result?.Retryable == true; break; }
                        if (TitleMatcher.ArtworkKey(match.Title) != TitleMatcher.ArtworkKey(game.Name)) throw new IOException("Kapak oyun adıyla eşleşmiyor.");
                        var rank = Array.IndexOf(Providers, match.Provider);
                        if (rank <= Array.IndexOf(Providers, after)) throw new IOException("Geçersiz sağlayıcı sırası.");
                        after = match.Provider;
                        try { await DownloadAsync(game, match, ct); return; }
                        catch (Exception ex) when (IsOptionalFailure(ex, ct)) { failed = true; store.Log("Kapak görseli", ex); }
                    }
                    catch (Exception ex) when (IsOptionalFailure(ex, ct)) { failed = true; store.Log("Kapak servisi", ex); break; }
                }
            }
            game.CoverPath = null; game.CoverProvider = null; game.CoverAttribution = null;
            game.CoverStatus = failed ? "Placeholder · bağlantı tekrar denenecek" : "Placeholder · kapak bulunamadı";
            game.CoverRetryAt = DateTimeOffset.UtcNow.Add(failed ? TimeSpan.FromMinutes(15) : TimeSpan.FromDays(7));
        }
        finally
        {
            // Cancellation must not poison the next retry, but completed attempts must
            // bypass old failure cooldowns only once after an algorithm/settings change.
            if (!ct.IsCancellationRequested) game.CoverLookupKey = LookupKey(game);
            try { store.SaveGame(game); game.Refresh(); }
            finally { serial.Release(); }
        }
    }
    private static bool IsOptionalFailure(Exception ex, CancellationToken ct) => ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException
        || ex is OperationCanceledException && !ct.IsCancellationRequested;

    private async Task DownloadAsync(Game game, CoverMatch match, CancellationToken ct)
    {
        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        downloadTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = downloadTimeout.Token;
        if (!Uri.TryCreate(match.ImageUrl, UriKind.Absolute, out var url) || !IsAllowedImage(url)) throw new IOException("Geçersiz kapak kaynağı.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("image/jpeg,image/png");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("Kapak boyutu sınırı aşıldı.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        { if (buffer.Length + count > MaxBytes) throw new IOException("Kapak çok büyük."); await buffer.WriteAsync(chunk.AsMemory(0, count), ct); }
        var data = buffer.ToArray();
        var path = await SaveImageAsync(game, data, ct);
        game.CoverPath = path; game.CoverProvider = match.Provider; game.CoverAttribution = match.Attribution;
        game.CoverStatus = "Kapak: " + (match.Provider == "GoogleShopping" ? "Google Shopping" : match.Provider); game.CoverRetryAt = null;
    }
    private async Task<string> SaveImageAsync(Game game, byte[] data, CancellationToken ct)
    {
        if (data.Length > MaxBytes) throw new IOException("Kapak boyutu sınırı 8 MB.");
        var png = data.Length > 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = data.Length > 3 && data[0] == 255 && data[1] == 216 && data[2] == 255;
        if (!png && !jpeg) throw new IOException("Yalnızca PNG/JPEG kapak destekleniyor.");
        var directory = Path.Combine(store.DataDirectory, "covers"); Directory.CreateDirectory(directory);
        // New URI avoids WPF's bitmap cache showing the previous cover after a change.
        var path = Path.Combine(directory, game.Id + "-" + Guid.NewGuid() + (png ? ".png" : ".jpg"));
        try { await File.WriteAllBytesAsync(path + ".tmp", data, ct); File.Move(path + ".tmp", path); }
        finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
        return path;
    }
    public async Task ImportManualAsync(Game game, byte[] pngOrJpeg, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            var path = await SaveImageAsync(game, pngOrJpeg, ct);
            var previous = (game.CoverPath, game.CoverProvider, game.CoverAttribution, game.CoverStatus, game.CoverRetryAt, game.ManualCover);
            game.CoverPath = path; game.CoverProvider = "Manual"; game.CoverAttribution = null;
            game.CoverStatus = "Kapak: elle seçildi"; game.CoverRetryAt = null; game.ManualCover = true;
            try { store.SaveGame(game); }
            catch { (game.CoverPath, game.CoverProvider, game.CoverAttribution, game.CoverStatus, game.CoverRetryAt, game.ManualCover) = previous; throw; }
            game.Refresh();
        }
        finally { serial.Release(); }
    }
    public async Task UseAutomaticAsync(Game game, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            game.ManualCover = false; game.CoverPath = null; game.CoverProvider = null; game.CoverAttribution = null;
            game.CoverRetryAt = null; game.CoverLookupKey = null; game.CoverStatus = "Kapak aranıyor";
            store.SaveGame(game); game.Refresh();
        }
        finally { serial.Release(); }
        await ResolveAsync(game, ct);
    }
    public static bool IsAllowedImage(Uri uri) => PublicArtwork.IsAllowedImage(uri);
    public void Dispose() { http.Dispose(); serial.Dispose(); }
}
