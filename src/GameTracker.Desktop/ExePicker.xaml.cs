using System.Windows;
using GameTracker.Core;

namespace GameTracker.Desktop;

public partial class ExePicker : Window
{
    private readonly Game game;
    public string? SelectedRelativePath { get; private set; }
    public ExePicker(Game value, bool runtime = false)
    {
        InitializeComponent(); game = value;
        TitleText.Text = runtime ? "Takip edilecek oyun süreci" : value.Name + " — exe nerede?";
        CandidatesList.ItemsSource = game.Candidates;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesList.SelectedItem is ExeCandidate selected) Select(selected.RelativePath);
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Oyun exe'si|*.exe", InitialDirectory = game.FolderPath };
        if (picker.ShowDialog(this) == true) Select(Path.GetRelativePath(game.FolderPath, picker.FileName));
    }
    private void Select(string relative)
    {
        try { PathPolicy.ResolveExe(game, relative); SelectedRelativePath = relative; DialogResult = true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Exe seçimi"); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
