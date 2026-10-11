using System.Windows;
using System.Windows.Input;
using GameTracker.Core;

namespace GameTracker.Desktop;

public enum GameDeleteChoice { Cancel, Permanent, Recycle }

// This dialog only chooses a mode. All filesystem/process/identity checks stay
// in GameOperations, and closing the window always leaves the choice at Cancel.
public partial class DeleteGameDialog : Window
{
    public GameDeleteChoice Choice { get; private set; } = GameDeleteChoice.Cancel;

    public DeleteGameDialog(Game game)
    {
        InitializeComponent();
        GameNameText.Text = game.Name; GameNameText.ToolTip = game.Name;
        FolderPathText.Text = game.FolderPath;
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Permanent_Click(object sender, RoutedEventArgs e) => Complete(GameDeleteChoice.Permanent);
    private void Recycle_Click(object sender, RoutedEventArgs e) => Complete(GameDeleteChoice.Recycle);
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Complete(GameDeleteChoice choice) { Choice = choice; DialogResult = true; }
    private void Title_MouseDown(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
