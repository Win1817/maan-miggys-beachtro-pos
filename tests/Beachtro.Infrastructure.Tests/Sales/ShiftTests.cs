using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Serialization;
using Microsoft.Data.Sqlite;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>T-11 shifts and cash movements (FR-019, FR-020).</summary>
public sealed class ShiftTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private Guid ItemOf(SaleReceipt sale) =>
        Guid.Parse(_h.Security.Database.Scalar<string>($"SELECT Id FROM OrderItems WHERE OrderId = '{sale.OrderId}' AND LineNo = 1"));

    private async Task<CorrectionReceipt> RefundOneAsync(UserSession session, SaleReceipt sale, RefundEvidence evidence)
    {
        var requestId = Guid.NewGuid();
        Ok(await _h.Corrections.PrepareAsync(session, requestId,
            new CorrectionInput(sale.OrderId, CorrectionKind.Refund, [new CorrectionLineInput(ItemOf(sale), 1000, StockDisposition.Return)], "customer returned item")));
        return Ok(await _h.Corrections.CompleteAsync(session, requestId, evidence));
    }

    [Fact]
    public async Task Expected_cash_follows_the_equation_and_closing_freezes_a_snapshot()
    {
        var shift = Ok(await _h.Shifts.OpenAsync(_h.Owner, 1_000_00)).Shift;

        await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Tea, 2)), new CashPayment(500_00)); // 200 applied, 300 change
        await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Cookie, 1)), new GCashPayment(true, "REF-1", null)); // GCash excluded
        var refunded = await _h.SellCashAsync((_h.Tea, 1));
        await RefundOneAsync(_h.Owner, refunded, new CashPaidOut());
        Ok(await _h.Shifts.RecordMovementAsync(_h.Owner, Guid.NewGuid(), shift.ShiftId, CashDirection.In, 50_00, "change float top-up"));
        Ok(await _h.Shifts.RecordMovementAsync(_h.Owner, Guid.NewGuid(), shift.ShiftId, CashDirection.Out, 30_00, "ice purchase"));

        var live = Ok(await _h.Shifts.GetCurrentAsync(_h.Owner))!.Shift.Cash;
        Assert.Equal(300_00, live.CashSalesMinor);
        Assert.Equal(100_00, live.CashRefundsMinor);
        Assert.Equal(50_00, live.GCashSalesMinor);
        Assert.Equal(1_000_00 + 300_00 + 50_00 - 30_00 - 100_00, live.ExpectedMinor);

        var closed = Ok(await _h.Shifts.CloseAsync(_h.Owner, shift.ShiftId, live.ExpectedMinor, null)).Shift;
        Assert.False(closed.IsOpen);
        Assert.Equal(0, closed.VarianceMinor);

        var json = _h.Security.Database.Scalar<string>($"SELECT CloseSnapshotJson FROM CashierShifts WHERE Id = '{shift.ShiftId}'");
        Assert.Equal(CanonicalJson.Sha256(json), _h.Security.Database.Scalar<string>($"SELECT CloseSnapshotSha256 FROM CashierShifts WHERE Id = '{shift.ShiftId}'"));
        Assert.Contains("\"expectedMinor\":122000", json, StringComparison.Ordinal);
        Assert.Contains("\"movementCount\":2", json, StringComparison.Ordinal);

        // The closed row is protected by a guard trigger.
        var ex = Assert.Throws<SqliteException>(() => _h.Security.Database.Execute($"UPDATE CashierShifts SET CountedCashM = 0 WHERE Id = '{shift.ShiftId}'"));
        Assert.Contains("closed shift is immutable", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ResultStatus.Conflict, (await _h.Shifts.RecordMovementAsync(_h.Owner, Guid.NewGuid(), shift.ShiftId, CashDirection.In, 1_00, "late")).Status);
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Shifts.Closed'"));
    }

    [Fact]
    public async Task One_open_shift_per_cashier_and_a_variance_needs_a_reason()
    {
        var shift = Ok(await _h.Shifts.OpenAsync(_h.Owner, 500_00)).Shift;
        Assert.Equal(ResultStatus.Conflict, (await _h.Shifts.OpenAsync(_h.Owner, 0)).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Shifts.OpenAsync(_h.Owner, -1)).Status);

        var noReason = await _h.Shifts.CloseAsync(_h.Owner, shift.ShiftId, 480_00, null);
        Assert.Equal(ResultStatus.Validation, noReason.Status);
        Assert.Contains("short by 20.00", noReason.Message, StringComparison.Ordinal);

        var closed = Ok(await _h.Shifts.CloseAsync(_h.Owner, shift.ShiftId, 480_00, "miscounted change")).Shift;
        Assert.Equal(-20_00, closed.VarianceMinor);
        Assert.Equal(ResultStatus.Conflict, (await _h.Shifts.CloseAsync(_h.Owner, shift.ShiftId, 480_00, "again")).Status);

        // A new shift can open after closing; no reopening of the old one.
        Ok(await _h.Shifts.OpenAsync(_h.Owner, 0));
        Assert.Equal(2L, _h.Count("CashierShifts"));
    }

    [Fact]
    public async Task Cash_movements_need_a_reason_are_idempotent_and_cannot_overdraw()
    {
        var shift = Ok(await _h.Shifts.OpenAsync(_h.Owner, 100_00)).Shift;
        Assert.Equal(ResultStatus.Validation, (await _h.Shifts.RecordMovementAsync(_h.Owner, Guid.NewGuid(), shift.ShiftId, CashDirection.In, 10_00, " ")).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Shifts.RecordMovementAsync(_h.Owner, Guid.NewGuid(), shift.ShiftId, CashDirection.In, 0, "zero")).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Shifts.RecordMovementAsync(_h.Owner, Guid.NewGuid(), shift.ShiftId, CashDirection.Out, 100_01, "too much")).Status);

        var request = Guid.NewGuid();
        var first = Ok(await _h.Shifts.RecordMovementAsync(_h.Owner, request, shift.ShiftId, CashDirection.Out, 40_00, "supplier"));
        var repeat = Ok(await _h.Shifts.RecordMovementAsync(_h.Owner, request, shift.ShiftId, CashDirection.Out, 40_00, "supplier"));
        Assert.Equal(first.MovementId, repeat.MovementId);
        Assert.Equal(1L, _h.Count("CashMovements"));
        Assert.Equal(ResultStatus.Conflict, (await _h.Shifts.RecordMovementAsync(_h.Owner, request, shift.ShiftId, CashDirection.Out, 41_00, "supplier")).Status);
        Assert.Equal(60_00, Ok(await _h.Shifts.GetCurrentAsync(_h.Owner))!.Shift.Cash.ExpectedMinor);
    }

    [Fact]
    public async Task Cashier_keeps_own_shift_but_cannot_move_cash_or_close_others()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        var ownerShift = Ok(await _h.Shifts.OpenAsync(_h.Owner, 0)).Shift;
        var shift = Ok(await _h.Shifts.OpenAsync(cashier, 200_00)).Shift;

        Assert.Equal(ResultStatus.Forbidden, (await _h.Shifts.RecordMovementAsync(cashier, Guid.NewGuid(), shift.ShiftId, CashDirection.In, 10_00, "x")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Shifts.CloseAsync(cashier, ownerShift.ShiftId, 0, "not mine")).Status);
        Assert.Equal(ResultStatus.NotFound, (await _h.Shifts.GetAsync(cashier, ownerShift.ShiftId)).Status);
        Assert.Single(Ok(await _h.Shifts.ListAsync(cashier, openOnly: false)));
        Assert.Equal(2, Ok(await _h.Shifts.ListAsync(_h.Owner, openOnly: true)).Count);

        Ok(await _h.Shifts.CloseAsync(cashier, shift.ShiftId, 200_00, null));
    }

    [Fact]
    public async Task Manager_closes_an_abandoned_shift_only_with_a_reason_and_fresh_password()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.two", RoleTemplates.Cashier);
        var (_, manager) = await _h.Security.CreateUserAsync(_h.Owner, "manager.one", RoleTemplates.Manager);
        var shift = Ok(await _h.Shifts.OpenAsync(cashier, 150_00)).Shift;

        Assert.Equal(ResultStatus.Validation, (await _h.Shifts.CloseAsync(manager, shift.ShiftId, 150_00, null)).Status);

        _h.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(ResultStatus.ReauthenticationRequired, (await _h.Shifts.CloseAsync(manager, shift.ShiftId, 150_00, "cashier left without closing")).Status);

        Ok(await _h.Security.Identity.ReauthenticateAsync(manager, UserPassword));
        var closed = Ok(await _h.Shifts.CloseAsync(manager, shift.ShiftId, 150_00, "cashier left without closing")).Shift;
        Assert.Equal("Test manager.one", closed.ClosedByName);
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Shifts.ClosedByManager'"));
    }

    [Fact]
    public async Task Requiring_a_shift_blocks_selling_until_one_is_open()
    {
        Assert.False(await _h.Shifts.IsShiftRequiredAsync());
        await _h.SellCashAsync((_h.Tea, 1)); // optional by default: sale has no shift
        Assert.Equal(1L, _h.Count("Orders", "ShiftId IS NULL"));

        Ok(await _h.Security.Profiles.SetOperationalSettingAsync(_h.Owner, OperationalSettingKeys.ShiftsRequired, "true"));
        Assert.True(await _h.Shifts.IsShiftRequiredAsync());
        var blocked = await _h.Checkout.PrepareAsync(_h.Owner, Guid.NewGuid(), SalesHarness.Cart((_h.Tea, 1)));
        Assert.Equal(ResultStatus.Validation, blocked.Status);
        Assert.Contains("Open your shift", blocked.Message, StringComparison.Ordinal);

        var shift = Ok(await _h.Shifts.OpenAsync(_h.Owner, 0)).Shift;
        await _h.SellCashAsync((_h.Tea, 1));
        Assert.Equal(1L, _h.Count("Orders", $"ShiftId = '{shift.ShiftId}'"));
    }

    [Fact]
    public async Task Refund_paid_in_a_later_shift_changes_that_shift_not_the_closed_one()
    {
        var first = Ok(await _h.Shifts.OpenAsync(_h.Owner, 0)).Shift;
        var sale = await _h.SellCashAsync((_h.Tea, 2));
        var closed = Ok(await _h.Shifts.CloseAsync(_h.Owner, first.ShiftId, 200_00, null)).Shift;

        _h.Clock.Advance(TimeSpan.FromHours(3)); // past Manila midnight: next business date
        await _h.ReauthOwnerAsync();
        var second = Ok(await _h.Shifts.OpenAsync(_h.Owner, 500_00)).Shift;
        var refund = await RefundOneAsync(_h.Owner, sale, new CashPaidOut());
        Assert.Equal("2025-11-17", refund.BusinessDate);

        var firstAfter = Ok(await _h.Shifts.GetAsync(_h.Owner, first.ShiftId)).Shift;
        Assert.Equal(closed.Cash, firstAfter.Cash);
        Assert.Equal(0, firstAfter.Cash.CashRefundsMinor);
        Assert.Equal(400_00, Ok(await _h.Shifts.GetAsync(_h.Owner, second.ShiftId)).Shift.Cash.ExpectedMinor);
    }
}
