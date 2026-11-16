using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Beachtro.Application.Runtime;
using Beachtro.Application.Security;
using Beachtro.Desktop.Mvvm;

namespace Beachtro.Desktop.ViewModels;

/// <summary>
/// Top-level state: first-run setup → sign-in → (forced password change) → main pages, with the
/// inactivity lock and Ctrl+L. Navigation is filtered by capability for convenience only; every
/// service call authorizes independently.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly DispatcherTimer _inactivityTimer;
    private object? _currentPage;
    private object? _pageBeforeLock;
    private string _operatorName = string.Empty;
    private string _businessDate = string.Empty;
    private string? _notice;

    public ShellViewModel(PosServices services)
    {
        Services = services;
        ShowPos = new RelayCommand(_ => Navigate(new PosViewModel(this)), _ => Can(Capabilities.PosSell));
        ShowOrders = new RelayCommand(_ => Navigate(new OrdersViewModel(this)), _ => Can(Capabilities.OrdersViewOwn) || Can(Capabilities.OrdersViewAll));
        ShowShifts = new RelayCommand(_ => Navigate(new ShiftsViewModel(this)), _ => Can(Capabilities.ShiftsOwn) || Can(Capabilities.ShiftsManage));
        ShowDashboard = new RelayCommand(_ => Navigate(new DashboardViewModel(this)), _ => Can(Capabilities.ReportsViewExport));
        ShowReports = new RelayCommand(_ => Navigate(new ReportsViewModel(this)), _ => Can(Capabilities.ReportsViewExport));
        ShowProducts = new RelayCommand(_ => Navigate(new ProductsViewModel(this)), _ => Can(Capabilities.ProductsManage));
        ShowInventory = new RelayCommand(_ => Navigate(new InventoryViewModel(this)), _ => Can(Capabilities.InventoryAdjust));
        ShowSettings = new RelayCommand(_ => Navigate(new SettingsViewModel(this)),
            _ => Can(Capabilities.SettingsOperational) || Can(Capabilities.SettingsFinancial) || Can(Capabilities.BackupCreateVerify));
        LockCommand = new RelayCommand(_ => Lock(), _ => IsSignedIn && CurrentPage is not LockViewModel);
        SignOutCommand = new RelayCommand(_ => SignOut(null), _ => IsSignedIn);

        _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _inactivityTimer.Tick += (_, _) =>
        {
            if (IsSignedIn && CurrentPage is not LockViewModel && Services.Sessions.EnforceInactivity())
            {
                ShowLock();
            }
        };
    }

    public PosServices Services { get; }

    public bool IsTestProfile => Services.Paths.Profile == RuntimeProfile.Test;
    public string BannerText => IsTestProfile ? "TEST PROFILE — synthetic data only — NOT FOR REAL SALES — receipts are NOT tax invoices" : string.Empty;

    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (Set(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsSignedIn));
                OnPropertyChanged(nameof(ShowNavigation));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsSignedIn => Services.Sessions.Current is { IsEnded: false };
    public bool ShowNavigation => IsSignedIn && CurrentPage is PageViewModel;
    public string OperatorName { get => _operatorName; private set => Set(ref _operatorName, value); }
    public string BusinessDate { get => _businessDate; private set => Set(ref _businessDate, value); }

    /// <summary>Message shown on the sign-in screen after a forced sign-out.</summary>
    public string? Notice { get => _notice; set => Set(ref _notice, value); }

    public ICommand ShowPos { get; }
    public ICommand ShowOrders { get; }
    public ICommand ShowShifts { get; }
    public ICommand ShowDashboard { get; }
    public ICommand ShowReports { get; }
    public ICommand ShowProducts { get; }
    public ICommand ShowInventory { get; }
    public ICommand ShowSettings { get; }
    public ICommand LockCommand { get; }
    public ICommand SignOutCommand { get; }

    public async Task StartAsync()
    {
        CurrentPage = await Services.Identity.IsBootstrapRequiredAsync() ? new BootstrapViewModel(this) : new LoginViewModel(this);
        _inactivityTimer.Start();
    }

    public void RecordActivity()
    {
        if (CurrentPage is not LockViewModel)
        {
            Services.Sessions.RecordActivity();
        }
    }

    internal void ShowLogin() => CurrentPage = new LoginViewModel(this);

    internal async Task OnSignedInAsync(UserSession session)
    {
        Services.Sessions.Start(session);
        OperatorName = session.DisplayName;
        Notice = null;
        if (session.MustChangePassword)
        {
            CurrentPage = new ChangePasswordViewModel(this);
            return;
        }

        await EnterMainAsync();
    }

    internal async Task EnterMainAsync()
    {
        var calendar = await Services.Profiles.GetEffectiveCalendarProfileAsync(Services.Clock.UtcNowMilliseconds);
        BusinessDate = calendar.IsSuccess ? "Business date " + calendar.Value!.Policy.BusinessDateFor(Services.Clock.UtcNowMilliseconds) : "No calendar profile";

        var session = Services.Sessions.Current!;
        if (session.Has(Capabilities.PosSell))
        {
            Navigate(new PosViewModel(this));
        }
        else if (session.Has(Capabilities.ReportsViewExport))
        {
            Navigate(new DashboardViewModel(this));
        }
        else if (session.Has(Capabilities.OrdersViewAll))
        {
            Navigate(new OrdersViewModel(this));
        }
        else
        {
            Navigate(new SettingsViewModel(this));
        }
    }

    internal void Lock()
    {
        Services.Sessions.Lock();
        ShowLock();
    }

    internal void Unlocked()
    {
        CurrentPage = _pageBeforeLock ?? new LoginViewModel(this);
        _pageBeforeLock = null;
    }

    /// <summary>A service reported the session locked or ended.</summary>
    internal void OnSessionProblem()
    {
        var session = Services.Sessions.Current;
        if (session is null || session.IsEnded)
        {
            SignOut("Your session ended (for example, your account or permissions changed). Please sign in again.");
        }
        else if (session.IsLocked)
        {
            ShowLock();
        }
    }

    internal void SignOut(string? notice)
    {
        Services.Sessions.SignOut();
        OperatorName = string.Empty;
        BusinessDate = string.Empty;
        _pageBeforeLock = null;
        Notice = notice;
        CurrentPage = new LoginViewModel(this);
    }

    private void ShowLock()
    {
        _pageBeforeLock = CurrentPage;
        CurrentPage = new LockViewModel(this);
    }

    private bool Can(string capability) => Services.Sessions.Current is { IsEnded: false, IsLocked: false } s && s.Has(capability) && CurrentPage is PageViewModel;

    private async void Navigate(PageViewModel page)
    {
        CurrentPage = page;
        try
        {
            await page.LoadAsync();
        }
        catch (Exception ex)
        {
            page.Error = "The page could not load. Details were written to the log.";
            Serilog.Log.Error(ex, "Loading {Page} failed", page.Title);
        }
    }

    /// <summary>Reads a PasswordBox passed as a command parameter, then clears it.</summary>
    internal static string TakePassword(object? parameter)
    {
        if (parameter is PasswordBox box)
        {
            var value = box.Password;
            box.Clear();
            return value;
        }

        return string.Empty;
    }
}
