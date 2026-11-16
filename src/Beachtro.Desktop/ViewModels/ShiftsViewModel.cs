using System.Collections.ObjectModel;
using System.Windows.Input;
using Beachtro.Application.Security;
using Beachtro.Application.Shifts;
using Beachtro.Domain.Model;

namespace Beachtro.Desktop.ViewModels;

/// <summary>
/// Shifts and cash movements (FR-019, FR-020). The expected cash shown is computed by the shift
/// service from recorded sales, refunds and movements; the screen only enters counts and amounts.
/// </summary>
public sealed class ShiftsViewModel : PageViewModel
{
    private ShiftView? _selected;
    private ShiftDetail? _detail;
    private ShiftDetail? _myShift;
    private string _openingText = "0.00";
    private string _countedText = string.Empty;
    private string _closeReason = string.Empty;
    private string _movementAmount = string.Empty;
    private string _movementReason = string.Empty;
    private bool _movementIsOut;
    private bool _shiftRequired;

    public ShiftsViewModel(ShellViewModel shell)
        : base(shell)
    {
        OpenCommand = Command(_ => OpenAsync(), _ => CanOwnShift && MyShift is null);
        RecordMovementCommand = Command(_ => RecordMovementAsync(), _ => CanMoveCash && Detail is { Shift.IsOpen: true });
        CloseCommand = Command(CloseAsync, _ => Detail is { Shift.IsOpen: true });
        RefreshCommand = Command(_ => LoadAsync());
    }

    public override string Title => "Shifts";

    public ObservableCollection<ShiftView> Shifts { get; } = [];
    public ObservableCollection<CashMovementView> Movements { get; } = [];

    public bool CanOwnShift => Session.Has(Capabilities.ShiftsOwn);
    public bool CanMoveCash => Session.Has(Capabilities.CashMovements);
    public bool ShiftRequired
    {
        get => _shiftRequired;
        private set
        {
            if (Set(ref _shiftRequired, value))
            {
                OnPropertyChanged(nameof(ShiftPolicyText));
            }
        }
    }

    public string ShiftPolicyText => ShiftRequired
        ? "Selling requires an open shift (set in Settings)."
        : "Shifts are optional: you can sell without one. Sales and refunds made while your shift is open count toward its drawer.";

    public ShiftDetail? MyShift
    {
        get => _myShift;
        private set
        {
            if (Set(ref _myShift, value))
            {
                OnPropertyChanged(nameof(HasNoShift));
            }
        }
    }

    public bool HasNoShift => CanOwnShift && MyShift is null;

    public ShiftView? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                _ = LoadDetailAsync();
            }
        }
    }

    public ShiftDetail? Detail
    {
        get => _detail;
        private set
        {
            if (Set(ref _detail, value))
            {
                OnPropertyChanged(nameof(DetailHeading));
                OnPropertyChanged(nameof(Equation));
                OnPropertyChanged(nameof(IsOthersOpenShift));
                OnPropertyChanged(nameof(ClosedSummary));
            }
        }
    }

    public string DetailHeading => Detail is not { } d
        ? "Select a shift"
        : $"{d.Shift.CashierName} — opened {Formatting.LocalTime(d.Shift.OpenedUtc)}" + (d.Shift.IsOpen ? " (open)" : $", closed {Formatting.LocalTime(d.Shift.ClosedUtc!.Value)}");

    /// <summary>The expected-cash equation, line by line.</summary>
    public string Equation => Detail is not { } d
        ? string.Empty
        : string.Join(Environment.NewLine,
            $"Opening cash          {Formatting.Money(d.Shift.Cash.OpeningMinor),14}",
            $"+ Cash sales ({d.Shift.Cash.SaleCount,3})    {Formatting.Money(d.Shift.Cash.CashSalesMinor),14}",
            $"+ Cash in             {Formatting.Money(d.Shift.Cash.CashInMinor),14}",
            $"- Cash out            {Formatting.Money(d.Shift.Cash.CashOutMinor),14}",
            $"- Cash refunds        {Formatting.Money(d.Shift.Cash.CashRefundsMinor),14}",
            $"= Expected in drawer  {Formatting.Money(d.Shift.Cash.ExpectedMinor),14}",
            string.Empty,
            $"GCash (not in drawer): sales {Formatting.Money(d.Shift.Cash.GCashSalesMinor)}, refunds {Formatting.Money(d.Shift.Cash.GCashRefundsMinor)}");

    public string ClosedSummary => Detail is { Shift: { IsOpen: false } s }
        ? $"Counted {Formatting.Money(s.CountedMinor ?? 0)}, variance {Formatting.Money(s.VarianceMinor ?? 0)}, closed by {s.ClosedByName}"
          + (string.IsNullOrEmpty(s.CloseReason) ? "." : $": {s.CloseReason}")
        : string.Empty;

    public bool IsOthersOpenShift => Detail is { Shift.IsOpen: true } d && d.Shift.CashierUserId != Session.UserId;

    public string OpeningText { get => _openingText; set => Set(ref _openingText, value); }
    public string CountedText { get => _countedText; set => Set(ref _countedText, value); }
    public string CloseReason { get => _closeReason; set => Set(ref _closeReason, value); }
    public string MovementAmount { get => _movementAmount; set => Set(ref _movementAmount, value); }
    public string MovementReason { get => _movementReason; set => Set(ref _movementReason, value); }
    public bool MovementIsOut { get => _movementIsOut; set => Set(ref _movementIsOut, value); }

    public ICommand OpenCommand { get; }
    public ICommand RecordMovementCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshCommand { get; }

    public override async Task LoadAsync()
    {
        ShiftRequired = await Services.Shifts.IsShiftRequiredAsync();
        if (CanOwnShift)
        {
            var mine = await Services.Shifts.GetCurrentAsync(Session);
            MyShift = Check(mine) ? mine.Value : null;
        }

        var list = await Services.Shifts.ListAsync(Session, openOnly: false);
        if (!Check(list))
        {
            return;
        }

        var keep = Selected?.ShiftId ?? MyShift?.Shift.ShiftId;
        Shifts.Clear();
        foreach (var s in list.Value!)
        {
            Shifts.Add(s);
        }

        _selected = null;
        Selected = Shifts.FirstOrDefault(s => s.ShiftId == keep) ?? Shifts.FirstOrDefault();
    }

    private async Task LoadDetailAsync()
    {
        Movements.Clear();
        if (Selected is null)
        {
            Detail = null;
            return;
        }

        var detail = await Services.Shifts.GetAsync(Session, Selected.ShiftId);
        Detail = Check(detail) ? detail.Value : null;
        foreach (var m in Detail?.Movements ?? [])
        {
            Movements.Add(m);
        }
    }

    private async Task OpenAsync()
    {
        if (!Formatting.TryParseMoney(OpeningText, out var opening))
        {
            Error = "Enter the opening cash, for example 1000.00.";
            return;
        }

        if (Check(await Services.Shifts.OpenAsync(Session, opening)))
        {
            Info = $"Shift opened with {Formatting.Money(opening)} in the drawer.";
            _selected = null;
            await LoadAsync();
        }
    }

    private async Task RecordMovementAsync()
    {
        if (Detail is null || !Formatting.TryParseMoney(MovementAmount, out var amount) || amount <= 0)
        {
            Error = "Enter an amount greater than zero, for example 250.00.";
            return;
        }

        var direction = MovementIsOut ? CashDirection.Out : CashDirection.In;
        if (Check(await Services.Shifts.RecordMovementAsync(Session, Guid.NewGuid(), Detail.Shift.ShiftId, direction, amount, MovementReason)))
        {
            Info = $"Cash {(MovementIsOut ? "out" : "in")} of {Formatting.Money(amount)} recorded.";
            MovementAmount = string.Empty;
            MovementReason = string.Empty;
            await LoadAsync();
        }
    }

    private async Task CloseAsync(object? passwordBox)
    {
        if (Detail is null || !Formatting.TryParseMoney(CountedText, out var counted))
        {
            Error = "Count the drawer and enter the total, for example 1520.00.";
            return;
        }

        if (IsOthersOpenShift)
        {
            var password = ShellViewModel.TakePassword(passwordBox);
            if (!Check(await Task.Run(() => Services.Identity.ReauthenticateAsync(Session, password))))
            {
                return;
            }
        }

        var result = await Services.Shifts.CloseAsync(Session, Detail.Shift.ShiftId, counted, CloseReason);
        if (Check(result))
        {
            var s = result.Value!.Shift;
            Info = s.VarianceMinor == 0
                ? "Shift closed. The drawer matches the expected cash."
                : $"Shift closed with a variance of {Formatting.Money(s.VarianceMinor ?? 0)} ({(s.VarianceMinor > 0 ? "over" : "short")}).";
            CountedText = string.Empty;
            CloseReason = string.Empty;
            await LoadAsync();
        }
    }
}
