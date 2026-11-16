using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Application.Shifts;

/// <summary>
/// Inputs of the expected-cash equation (finance design §Reporting dictionary):
/// opening + cash sales applied + cash-in − cash-out − cash refunds paid. GCash is shown for
/// information and never enters the drawer equation.
/// </summary>
public sealed record ShiftCashSummary(
    long OpeningMinor,
    long CashSalesMinor,
    long CashInMinor,
    long CashOutMinor,
    long CashRefundsMinor,
    long ExpectedMinor,
    int SaleCount,
    int RefundCount,
    long GCashSalesMinor,
    long GCashRefundsMinor);

public sealed record CashMovementView(Guid MovementId, CashDirection Direction, long AmountMinor, string Reason, string ActorName, long CreatedUtc);

public sealed record ShiftView(
    Guid ShiftId,
    Guid CashierUserId,
    string CashierName,
    long OpenedUtc,
    long? ClosedUtc,
    ShiftCashSummary Cash,
    long? CountedMinor,
    long? VarianceMinor,
    string? ClosedByName,
    string? CloseReason)
{
    public bool IsOpen => ClosedUtc is null;
}

public sealed record ShiftDetail(ShiftView Shift, IReadOnlyList<CashMovementView> Movements);

/// <summary>
/// Cashier shifts and cash movements (FR-019, FR-020). Shifts are optional unless the
/// "require an open shift" setting is on (B-03, D-08). One open shift per cashier; a closed shift
/// is never edited or reopened.
/// </summary>
public interface IShiftService
{
    /// <summary>The caller's open shift with live expected cash, or null.</summary>
    Task<Result<ShiftDetail?>> GetCurrentAsync(UserSession session, CancellationToken cancellationToken = default);

    Task<Result<ShiftDetail>> OpenAsync(UserSession session, long openingCashMinor, CancellationToken cancellationToken = default);

    /// <summary>Cash in or out of an open drawer. Idempotent by request ID.</summary>
    Task<Result<CashMovementView>> RecordMovementAsync(UserSession session, Guid requestId, Guid shiftId, CashDirection direction, long amountMinor, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes with counted cash. Own shift needs shifts.own; another cashier's (abandoned) shift needs
    /// shifts.manage, a reason and recent password confirmation.
    /// </summary>
    Task<Result<ShiftDetail>> CloseAsync(UserSession session, Guid shiftId, long countedCashMinor, string? reason, CancellationToken cancellationToken = default);

    Task<Result<ShiftDetail>> GetAsync(UserSession session, Guid shiftId, CancellationToken cancellationToken = default);

    /// <summary>Recent shifts, newest first: all with shifts.manage, otherwise only the caller's.</summary>
    Task<Result<IReadOnlyList<ShiftView>>> ListAsync(UserSession session, bool openOnly, int take = 100, CancellationToken cancellationToken = default);

    Task<bool> IsShiftRequiredAsync(CancellationToken cancellationToken = default);
}
