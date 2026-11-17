using System.Collections.ObjectModel;
using System.Windows.Input;
using Beachtro.Application.Security;
using Beachtro.Desktop.Mvvm;

namespace Beachtro.Desktop.ViewModels;

/// <summary>A role check box. Roles the signed-in user may not grant are disabled with the reason (POS-DEC-008).</summary>
public sealed class RoleOption(string key, string name, string? blockedReason) : ObservableObject
{
    private bool _isChecked;

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string? BlockedReason { get; } = blockedReason;
    public bool IsEnabled => BlockedReason is null;
    public string Permissions { get; } = string.Join(", ", RoleTemplates.Grants[key].Order(StringComparer.Ordinal));
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

public sealed record UserRow(UserSummary User)
{
    public string Username => User.Username;
    public string DisplayName => User.DisplayName;
    public string Roles => string.Join(", ", User.RoleKeys.Select(k => RoleTemplates.Names.TryGetValue(k, out var n) ? n : k));
    public string Status => !User.IsActive ? "Deactivated" : User.MustChangePassword ? "Active — must change password" : "Active";
}

/// <summary>
/// User accounts (IB-25, FR-002–003): list, create, reset password, change roles and deactivate.
/// Every change needs a reason and a fresh password confirmation; the identity service authorizes
/// and enforces delegation and last-owner rules itself.
/// </summary>
public sealed class UsersViewModel : PageViewModel
{
    private UserRow? _selected;
    private string _newUsername = string.Empty;
    private string _newDisplayName = string.Empty;
    private string _reason = string.Empty;
    private string? _issuedPassword;
    private string? _issuedFor;

    public UsersViewModel(ShellViewModel shell)
        : base(shell)
    {
        CreateCommand = Command(CreateAsync);
        SaveRolesCommand = Command(SaveRolesAsync, _ => Selected is not null);
        ResetPasswordCommand = Command(ResetAsync, _ => Selected is not null);
        ToggleActiveCommand = Command(ToggleActiveAsync, _ => Selected is not null);
        DismissPasswordCommand = new RelayCommand(_ => IssuedPassword = null);
        NewRoles = BuildRoleOptions();
        SelectedRoles = BuildRoleOptions();
    }

    public override string Title => "Users";

    public ObservableCollection<UserRow> Users { get; } = [];
    public IReadOnlyList<RoleOption> NewRoles { get; }
    public IReadOnlyList<RoleOption> SelectedRoles { get; }

    public UserRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                foreach (var r in SelectedRoles)
                {
                    r.IsChecked = value?.User.RoleKeys.Contains(r.Key, StringComparer.Ordinal) ?? false;
                }

                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(ActiveButtonText));
                OnPropertyChanged(nameof(SelectedHeading));
            }
        }
    }

    public bool HasSelection => Selected is not null;
    public string SelectedHeading => Selected is null ? "Select an account" : $"{Selected.DisplayName} ({Selected.Username})";
    public string ActiveButtonText => Selected?.User.IsActive == false ? "Reactivate account" : "Deactivate account";
    public bool IsOwner => Services.Sessions.Current?.Has(Capabilities.RolesManage) ?? false;

    public string DelegationNote => IsOwner
        ? "As an Owner you can manage every role. The last active Owner cannot be removed or deactivated."
        : "You can only manage roles whose permissions you hold yourself. Owner accounts and roles that can sell (Cashier, Manager) are managed by an Owner.";

    public string NewUsername { get => _newUsername; set => Set(ref _newUsername, value); }
    public string NewDisplayName { get => _newDisplayName; set => Set(ref _newDisplayName, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }

    /// <summary>Shown once after create or reset; cleared on dismiss or when leaving the page.</summary>
    public string? IssuedPassword
    {
        get => _issuedPassword;
        private set
        {
            if (Set(ref _issuedPassword, value))
            {
                OnPropertyChanged(nameof(IssuedNote));
            }
        }
    }

    public string IssuedNote => IssuedPassword is null ? string.Empty
        : $"Temporary password for {_issuedFor}. Give it to them privately; it is shown only once and must be changed at first sign-in.";

    public ICommand CreateCommand { get; }
    public ICommand SaveRolesCommand { get; }
    public ICommand ResetPasswordCommand { get; }
    public ICommand ToggleActiveCommand { get; }
    public ICommand DismissPasswordCommand { get; }

    public override Task LoadAsync() => RefreshAsync(null);

    private List<RoleOption> BuildRoleOptions()
    {
        var session = Services.Sessions.Current;
        return [.. new[] { RoleTemplates.Cashier, RoleTemplates.Manager, RoleTemplates.Administrator, RoleTemplates.Owner }
            .Select(k => new RoleOption(k, RoleTemplates.Names[k], session is null ? "Not signed in." : RoleDelegation.WhyNot(session, k)))];
    }

    private async Task RefreshAsync(Guid? select)
    {
        var result = await Services.Identity.ListUsersAsync(Session);
        if (!Check(result))
        {
            return;
        }

        select ??= Selected?.User.UserId;
        Users.Clear();
        foreach (var u in result.Value!)
        {
            Users.Add(new UserRow(u));
        }

        Selected = null;
        Selected = Users.FirstOrDefault(u => u.User.UserId == select);
    }

    /// <summary>Account changes need a reason and the password re-entered (security design §Sessions).</summary>
    private async Task<bool> ConfirmAsync(object? passwordBox)
    {
        var password = ShellViewModel.TakePassword(passwordBox);
        if (string.IsNullOrWhiteSpace(Reason))
        {
            Error = "Enter a reason for this change.";
            return false;
        }

        if (string.IsNullOrEmpty(password))
        {
            Error = "Confirm with your password.";
            return false;
        }

        return Check(await Task.Run(() => Services.Identity.ReauthenticateAsync(Session, password)));
    }

    private async Task CreateAsync(object? passwordBox)
    {
        var roles = NewRoles.Where(r => r.IsChecked).Select(r => r.Key).ToArray();
        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewDisplayName) || roles.Length == 0)
        {
            Error = "Enter a username and a display name, and choose at least one role.";
            ShellViewModel.TakePassword(passwordBox);
            return;
        }

        if (!await ConfirmAsync(passwordBox))
        {
            return;
        }

        var result = await Task.Run(() => Services.Identity.CreateUserAsync(Session, new CreateUserRequest(NewUsername.Trim(), NewDisplayName.Trim(), roles, Reason.Trim())));
        if (!Check(result))
        {
            return;
        }

        _issuedFor = NewUsername.Trim();
        IssuedPassword = result.Value!.TemporaryPassword;
        Info = $"Account {_issuedFor} created.";
        NewUsername = NewDisplayName = Reason = string.Empty;
        foreach (var r in NewRoles)
        {
            r.IsChecked = false;
        }

        await RefreshAsync(result.Value.UserId);
    }

    private async Task SaveRolesAsync(object? passwordBox)
    {
        var target = Selected!.User;
        var roles = SelectedRoles.Where(r => r.IsChecked).Select(r => r.Key).ToArray();
        if (roles.Length == 0)
        {
            Error = "Choose at least one role. To stop someone signing in, deactivate the account instead.";
            ShellViewModel.TakePassword(passwordBox);
            return;
        }

        if (!await ConfirmAsync(passwordBox) || !Check(await Services.Identity.SetUserRolesAsync(Session, target.UserId, roles, Reason.Trim())))
        {
            return;
        }

        Info = $"Roles for {target.Username} saved. They are signed out and see the new roles at their next sign-in.";
        Reason = string.Empty;
        await AfterChangeAsync(target.UserId);
    }

    private async Task ResetAsync(object? passwordBox)
    {
        var target = Selected!.User;
        if (!await ConfirmAsync(passwordBox))
        {
            return;
        }

        var result = await Task.Run(() => Services.Identity.ResetPasswordAsync(Session, target.UserId, Reason.Trim()));
        if (!Check(result))
        {
            return;
        }

        _issuedFor = target.Username;
        IssuedPassword = result.Value!.TemporaryPassword;
        Info = $"Password for {target.Username} reset. Their open sessions were ended.";
        Reason = string.Empty;
        await AfterChangeAsync(target.UserId);
    }

    private async Task ToggleActiveAsync(object? passwordBox)
    {
        var target = Selected!.User;
        var activate = !target.IsActive;
        if (!await ConfirmAsync(passwordBox) || !Check(await Services.Identity.SetUserActiveAsync(Session, target.UserId, activate, Reason.Trim())))
        {
            return;
        }

        Info = activate ? $"{target.Username} can sign in again." : $"{target.Username} is deactivated and was signed out. Their history is kept.";
        Reason = string.Empty;
        await AfterChangeAsync(target.UserId);
    }

    /// <summary>Changing your own account ends your session; the next call reports it and the shell signs out.</summary>
    private async Task AfterChangeAsync(Guid target)
    {
        if (target == Session.UserId)
        {
            Shell.SignOut("Your own account changed. Please sign in again.");
            return;
        }

        await RefreshAsync(target);
    }
}
