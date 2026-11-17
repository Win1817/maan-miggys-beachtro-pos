using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using Beachtro.Application.Security;
using Beachtro.Desktop.Mvvm;

namespace Beachtro.Desktop.ViewModels;

/// <summary>Non-page screens (setup, sign-in, password change, lock).</summary>
public abstract class AccessViewModel(ShellViewModel shell) : ObservableObject
{
    private string? _error;
    private bool _busy;

    public ShellViewModel Shell { get; } = shell;
    public string? Error { get => _error; set => Set(ref _error, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    protected AsyncCommand Command(Func<object?, Task> run) =>
        new(async p =>
        {
            Busy = true;
            Error = null;
            try
            {
                await run(p);
            }
            finally
            {
                Busy = false;
            }
        }, null, ex =>
        {
            Error = "Something went wrong. Details were written to the log.";
            Serilog.Log.Error(ex, "Access screen error");
        });
}

/// <summary>First run on a new installation: create the Owner account. Shown only once.</summary>
public sealed class BootstrapViewModel : AccessViewModel
{
    private string _username = string.Empty;
    private string _displayName = string.Empty;

    public BootstrapViewModel(ShellViewModel shell)
        : base(shell)
    {
        CreateCommand = Command(async p =>
        {
            if (p is not object[] { Length: 2 } boxes)
            {
                return;
            }

            var password = ShellViewModel.TakePassword(boxes[0]);
            var confirm = ShellViewModel.TakePassword(boxes[1]);
            if (password != confirm)
            {
                Error = "The passwords do not match.";
                return;
            }

            var result = await Task.Run(() => Shell.Services.Identity.BootstrapOwnerAsync(new BootstrapOwnerRequest(Username, DisplayName, password)));
            if (!result.IsSuccess)
            {
                Error = result.Message;
                return;
            }

            Shell.Notice = "Owner account created. Sign in to continue.";
            Shell.ShowLogin();
        });
    }

    public string Username { get => _username; set => Set(ref _username, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    /// <summary>The database is missing on a PC that was in use: only restoring is offered (POS-DEC-011).</summary>
    public bool RestoreOnly { get; init; }

    public bool CanCreateOwner => !RestoreOnly;

    public string RestoreHeading => RestoreOnly ? "The shop's database is missing" : "Replacing a PC?";

    public string RestoreHint => RestoreOnly
        ? "This PC was in use (its number journal or backups are here) but the database file is gone. Starting empty would lose the ledger, so restore the newest backup package. Sign in with an Owner or Administrator account from that backup. The journals still on this PC are used to prove which receipt numbers were already used."
        : "Restore the shop's last backup package instead of starting empty. Sign in with an Owner or Administrator account from that backup. Selling stays paused until you resolve what the old PC may have done after the backup.";

    // Replacement PC (POS-DEC-010): restore a backup instead of creating a new owner.
    private string _packagePath = string.Empty;
    private string _restoreUsername = string.Empty;
    private string _restoreReason = "Replacement PC";

    public string PackagePath { get => _packagePath; set => Set(ref _packagePath, value); }
    public string RestoreUsername { get => _restoreUsername; set => Set(ref _restoreUsername, value); }
    public string RestoreReason { get => _restoreReason; set => Set(ref _restoreReason, value); }
    public ICommand BrowseCommand => new RelayCommand(_ =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose the backup package", Filter = "Backup package (*.zip)|*.zip", InitialDirectory = Shell.Services.Paths.Backups };
        if (dialog.ShowDialog() == true)
        {
            PackagePath = dialog.FileName;
        }
    });

    public ICommand RestoreCommand => _restore ??= Command(async p =>
    {
        var password = ShellViewModel.TakePassword(p);
        if (string.IsNullOrWhiteSpace(PackagePath) || string.IsNullOrWhiteSpace(RestoreUsername) || password.Length == 0)
        {
            Error = "Choose the backup package and enter an Owner or Administrator sign-in from that backup.";
            return;
        }

        var result = await Task.Run(() => Shell.Services.Backups.RestoreOnNewDeviceAsync(PackagePath.Trim(), RestoreUsername, password, RestoreReason));
        if (!result.IsSuccess)
        {
            Error = result.Message;
            return;
        }

        Shell.Notice = "Backup restored on this PC. Sign in with your account from the backup. Selling stays paused: the Recovery page shows "
            + $"{result.Value!.BlockedSeries.Count} number series and {result.Value.UnresolvedPreparedRequests} unfinished payment(s) to resolve first.";
        Shell.ShowLogin();
    });

    private ICommand? _restore;

    public string PasswordRules => $"{PasswordPolicy.MinimumLength}–{PasswordPolicy.MaximumLength} characters. Spaces and any characters are allowed; a passphrase is easiest.";
    public ICommand CreateCommand { get; }
}

public sealed class LoginViewModel : AccessViewModel
{
    private string _username = string.Empty;

    public LoginViewModel(ShellViewModel shell)
        : base(shell)
    {
        SignInCommand = Command(async p =>
        {
            var password = ShellViewModel.TakePassword(p);
            var result = await Task.Run(() => Shell.Services.Identity.SignInAsync(Username, password));
            if (!result.IsSuccess)
            {
                Error = result.Message;
                return;
            }

            await Shell.OnSignedInAsync(result.Value!);
        });
    }

    public string Username { get => _username; set => Set(ref _username, value); }
    public string? Notice => Shell.Notice;
    public ICommand SignInCommand { get; }
}

public sealed class ChangePasswordViewModel : AccessViewModel
{
    public ChangePasswordViewModel(ShellViewModel shell)
        : base(shell)
    {
        ChangeCommand = Command(async p =>
        {
            if (p is not object[] { Length: 3 } boxes)
            {
                return;
            }

            var current = ShellViewModel.TakePassword(boxes[0]);
            var next = ShellViewModel.TakePassword(boxes[1]);
            var confirm = ShellViewModel.TakePassword(boxes[2]);
            if (next != confirm)
            {
                Error = "The new passwords do not match.";
                return;
            }

            var session = Shell.Services.Sessions.Current!;
            var result = await Task.Run(() => Shell.Services.Identity.ChangePasswordAsync(session, current, next));
            if (!result.IsSuccess)
            {
                Error = result.Message;
                if (result.Status == Application.Common.ResultStatus.Unauthenticated)
                {
                    Shell.SignOut("Please sign in again.");
                }

                return;
            }

            await Shell.EnterMainAsync();
        });
        CancelCommand = new RelayCommand(_ => Shell.SignOut(null));
    }

    public string Heading => "Choose a new password";
    public string PasswordRules => $"{PasswordPolicy.MinimumLength}–{PasswordPolicy.MaximumLength} characters. It cannot contain your username.";
    public ICommand ChangeCommand { get; }
    public ICommand CancelCommand { get; }
}

/// <summary>Locked screen: nothing behind it is visible; the same user's password unlocks.</summary>
public sealed class LockViewModel : AccessViewModel
{
    public LockViewModel(ShellViewModel shell)
        : base(shell)
    {
        UnlockCommand = Command(async p =>
        {
            var password = ShellViewModel.TakePassword(p);
            var session = Shell.Services.Sessions.Current;
            if (session is null)
            {
                Shell.SignOut(null);
                return;
            }

            var result = await Task.Run(() => Shell.Services.Identity.ReauthenticateAsync(session, password));
            if (!result.IsSuccess)
            {
                Error = result.Message;
                if (session.IsEnded)
                {
                    Shell.SignOut("Your session ended. Please sign in again.");
                }

                return;
            }

            Shell.Unlocked();
        });
        SignOutCommand = new RelayCommand(_ => Shell.SignOut(null));
    }

    public string LockedUser => Shell.OperatorName;
    public ICommand UnlockCommand { get; }
    public ICommand SignOutCommand { get; }
}

/// <summary>Passes several PasswordBoxes as one command parameter.</summary>
public sealed class PasswordBoxesConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        values.OfType<PasswordBox>().Cast<object>().ToArray();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The data was written by a newer version (POS-DEC-011). This version never opens it. Recommended: install
/// the newer version again. Otherwise roll back to a backup made before the update, with an Owner or
/// Administrator sign-in from that backup; everything recorded since that backup is left out and the PC
/// starts in recovery mode.
/// </summary>
public sealed class UpdateRollbackViewModel : AccessViewModel
{
    private string _packagePath;
    private string _username = string.Empty;
    private string _reason = "Rolling back an update";

    public UpdateRollbackViewModel(ShellViewModel shell)
        : base(shell)
    {
        var backups = shell.Services.Paths.Backups;
        _packagePath = Directory.Exists(backups)
            ? Directory.EnumerateFiles(backups, "pos_preupdate_*.zip").OrderDescending(StringComparer.Ordinal).FirstOrDefault() ?? string.Empty
            : string.Empty;
        BrowseCommand = new RelayCommand(_ =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose the backup made before the update", Filter = "Backup package (*.zip)|*.zip", InitialDirectory = backups };
            if (dialog.ShowDialog() == true)
            {
                PackagePath = dialog.FileName;
            }
        });
        RollbackCommand = Command(async p =>
        {
            var password = ShellViewModel.TakePassword(p);
            var result = await Task.Run(() => Shell.Services.Backups.RestoreForUpdateRollbackAsync(PackagePath.Trim(), Username, password, Reason));
            if (!result.IsSuccess)
            {
                Error = result.Message;
                return;
            }

            System.Windows.MessageBox.Show(
                "The update was rolled back to the chosen backup. The newer data was kept as evidence in the recovery folder. "
                + "Beachtro POS will now restart; sign in and finish on the Recovery page before selling.",
                "Beachtro POS", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            App.Restart();
        });
    }

    public string Explanation =>
        "This data was saved by a newer version of Beachtro POS, so this version will not open it (that could damage it). "
        + "The safest fix is to install the newer version again. Only if that version cannot be used, roll back to a backup made before the update: "
        + "anything recorded after that backup is not brought back.";

    public string SchemaDetail => "Newer data changes: " + string.Join(", ", Shell.NewerSchema);
    public string PackagePath { get => _packagePath; set => Set(ref _packagePath, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    public ICommand BrowseCommand { get; }
    public ICommand RollbackCommand { get; }
}
