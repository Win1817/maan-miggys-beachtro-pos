using System.IO;
using System.Security.Principal;
using System.Windows;
using Beachtro.Infrastructure.Recovery;

namespace Beachtro.Desktop;

/// <summary>
/// Started with <c>--maintenance</c> (POS-DEC-011). The POS itself is not running (same single-instance lock).
/// Resetting a password also needs Windows administrator rights, checked here.
/// </summary>
public partial class MaintenanceWindow
{
    private readonly MaintenanceService _maintenance;
    private readonly bool _isAdministrator;

    public MaintenanceWindow(MaintenanceService maintenance, bool newerData)
    {
        _maintenance = maintenance;
        InitializeComponent();
        using var identity = WindowsIdentity.GetCurrent();
        _isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Loaded += async (_, _) => await ShowStatusAsync(newerData);
    }

    private static string WindowsAccount => string.Concat(Environment.UserDomainName, @"\", Environment.UserName);

    private async Task ShowStatusAsync(bool newerData)
    {
        var s = await _maintenance.GetStatusAsync();
        StatusText.Text = string.Join(Environment.NewLine,
            $"Data folder:      {s.DataRoot}",
            $"Database changes: {s.AppliedMigrations} applied, this version knows {s.KnownMigrations}",
            $"Mode:             {s.Mode ?? "not available"}",
            $"Active owners:    {(s.ActiveOwners.Count == 0 ? "none found" : string.Join(", ", s.ActiveOwners))}",
            $"Latest backup:    {s.LastBackup ?? "none"}",
            $"Windows account:  {WindowsAccount} ({(_isAdministrator ? "administrator" : "not administrator")})");
        if (newerData)
        {
            Message.Text = "The data was written by a newer version. Install that version to use maintenance actions.";
            ResetPanel.IsEnabled = false;
        }
        else if (!_isAdministrator)
        {
            Message.Text = "Close this window, then right-click Beachtro POS Maintenance and choose Run as administrator to reset a password.";
            ResetButton.IsEnabled = false;
        }
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        if (Confirmed.IsChecked != true || string.IsNullOrWhiteSpace(Username.Text))
        {
            Message.Text = "Enter the Owner's username and tick the confirmation.";
            return;
        }

        ResetButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _maintenance.ResetOwnerPasswordAsync(Username.Text.Trim(), WindowsAccount));
            if (!result.IsSuccess)
            {
                Message.Text = result.Message;
                ResetButton.IsEnabled = true;
                return;
            }

            Message.Text = $"A verified backup was made, then the password for {Username.Text.Trim()} was reset and everyone was signed out. "
                + "Write down this temporary password now; it is shown only once and must be changed at the next sign-in.";
            Temporary.Text = result.Value!.TemporaryPassword;
            Temporary.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Serilog.Log.Error(ex, "Maintenance reset failed");
            Message.Text = "The reset failed; nothing was changed after the backup. Details are in the log file.";
            ResetButton.IsEnabled = true;
        }
    }
}
