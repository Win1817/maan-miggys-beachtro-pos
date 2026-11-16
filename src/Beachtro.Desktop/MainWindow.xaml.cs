using Beachtro.Application.Runtime;
using Beachtro.Infrastructure.Runtime;

namespace Beachtro.Desktop;

public partial class MainWindow
{
    public MainWindow(LocalDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InitializeComponent();

        if (paths.Profile == RuntimeProfile.Test)
        {
            ProfileBannerText.Text = "TEST PROFILE — synthetic data only — NOT FOR REAL SALES — NOT A TAX INVOICE SYSTEM";
            Title = "Beachtro POS [TEST]";
        }
        else
        {
            ProfileBanner.Visibility = System.Windows.Visibility.Collapsed;
        }

        DataRootText.Text = $"Data folder: {paths.Root}";
    }
}
