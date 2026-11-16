using Beachtro.Desktop.ViewModels;

namespace Beachtro.Desktop;

public partial class MainWindow
{
    public MainWindow(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        InitializeComponent();
        DataContext = shell;
        Title = shell.IsTestProfile ? "Beachtro POS [TEST]" : "Beachtro POS";

        // Any keyboard or pointer input counts as activity for the inactivity lock.
        PreviewKeyDown += (_, _) => shell.RecordActivity();
        PreviewMouseDown += (_, _) => shell.RecordActivity();
        PreviewTouchDown += (_, _) => shell.RecordActivity();
        Loaded += async (_, _) => await shell.StartAsync();
    }
}
