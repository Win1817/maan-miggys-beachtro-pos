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
