using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Reports;
using Beachtro.Application.Security;
using Beachtro.Domain.Calendar;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Serialization;
using Beachtro.Infrastructure.Shifts;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Reports;

/// <summary>
/// Dashboard, report runs and exports (IB-17, IB-18; FR-021 to FR-023). A run stores the exact
/// report content and a ledger watermark. Asking again for the same report reuses the latest run
/// when nothing changed, otherwise records the next revision; history is never rewritten.
/// </summary>
public sealed class ReportService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IProfileService profiles,
    IClock clock,
    WriteGate writeGate) : IReportService
{
    public const int MaxRangeDays = 366;
    public const int MaxMonthlyRangeDays = 1096;
    public const string WatermarkSchema = "watermark.v1";
    public const string NoFilters = "{}";
    public const string CloseFilters = """{"purpose":"close"}""";

    public async Task<Result<DashboardView>> GetDashboardAsync(UserSession session, string businessDate, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReportsViewExport, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<DashboardView>();
        }

        if (!CalendarPolicy.TryParseDate(businessDate, out var date))
        {
            return Result<DashboardView>.Fail(ResultStatus.Validation, "Enter the date as YYYY-MM-DD.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var facts = await LedgerQuery.LoadAsync(db, businessDate, NextDay(date), cancellationToken).ConfigureAwait(false);
        var totals = LedgerTotals.From(facts.Sales, facts.Corrections);
        var openShifts = await db.CashierShifts.CountAsync(s => s.ClosedUtc == null, cancellationToken).ConfigureAwait(false);
        var outOfStock = await (from p in db.Products.AsNoTracking()
                                where p.IsActive && p.TrackStock
                                join i in db.Inventory.AsNoTracking() on p.Id equals i.ProductId
                                where i.QuantityQ <= 0
                                select p.Id).CountAsync(cancellationToken).ConfigureAwait(false);
        var close = await CloseStatusAsync(db, businessDate, facts, cancellationToken).ConfigureAwait(false);
        var closeText = close is null
            ? null
            : close.ChangedSinceClose
                ? $"Closed (revision {close.Revision}) but changed since: a refund or void was recorded afterwards. Close again to record the new figures."
                : $"Closed (revision {close.Revision}) by {close.ClosedByName}.";

        return Result<DashboardView>.Ok(new DashboardView(
            businessDate,
            totals.SaleCount,
            totals.NetRevenueExTaxM,
            totals.NetTaxM,
            totals.NetCollectedM,
            totals.NetCashM,
            totals.NetGCashM,
            totals.AverageSaleM,
            totals.CorrectionCount,
            totals.CorrectionsTotalM,
            openShifts,
            outOfStock,
            closeText,
            facts.Sales.FirstOrDefault()?.CurrencyExponent ?? Currency.Php.Exponent));
    }

    public Task<Result<ReportRunView>> GenerateAsync(UserSession session, ReportRequest request, CancellationToken cancellationToken = default) =>
        GenerateCoreAsync(session, request, NoFilters, cancellationToken);

    public async Task<Result<ReportRunView>> GetRunAsync(UserSession session, Guid runId, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReportsViewExport, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<ReportRunView>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        return run is null
            ? Result<ReportRunView>.Fail(ResultStatus.NotFound, "Report not found.")
            : Result<ReportRunView>.Ok(await ViewAsync(db, run, reused: false, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<ReportFile>> RenderAsync(UserSession session, Guid runId, ReportFormat format, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReportsViewExport, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<ReportFile>();
        }

        if (format is not (ReportFormat.Csv or ReportFormat.Pdf))
        {
            return Result<ReportFile>.Fail(ResultStatus.Validation, "Choose CSV or PDF.");
        }

        ReportRun? run;
        await using (var read = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            run = await read.ReportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        }

        if (run is null)
        {
            return Result<ReportFile>.Fail(ResultStatus.NotFound, "Report not found.");
        }

        // Rendering happens outside the write gate; only the artifact intent is a write.
        var file = Render(run, format);

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        await AddIntentAsync(db, run.Id, format, now, cancellationToken).ConfigureAwait(false);

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Reports.Exported", "ReportRun", run.Id.ToString(), null,
            new { format = format.ToString(), file.FileName, bytes = file.Content.Length });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<ReportFile>.Ok(file);
    }

    public async Task<Result<BusinessCloseView>> CloseBusinessDayAsync(UserSession session, string businessDate, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReportsViewExport, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<BusinessCloseView>();
        }

        if (!CalendarPolicy.TryParseDate(businessDate, out var date))
        {
            return Result<BusinessCloseView>.Fail(ResultStatus.Validation, "Enter the date as YYYY-MM-DD.");
        }

        var calendar = await profiles.GetEffectiveCalendarProfileAsync(clock.UtcNowMilliseconds, cancellationToken).ConfigureAwait(false);
        if (calendar.IsSuccess && string.CompareOrdinal(businessDate, calendar.Value!.Policy.BusinessDateFor(clock.UtcNowMilliseconds)) > 0)
        {
            return Result<BusinessCloseView>.Fail(ResultStatus.Validation, "A future business date cannot be closed.");
        }

        var run = await GenerateCoreAsync(session, new ReportRequest(ReportType.DailySales, businessDate, NextDay(date)), CloseFilters, cancellationToken).ConfigureAwait(false);
        if (!run.IsSuccess)
        {
            return run.As<BusinessCloseView>();
        }

        var v = run.Value!;

        // The close's stored CSV and PDF are written and queued in the background (IB-23 daily report).
        await QueueRunFilesAsync(v.RunId, businessDate, cancellationToken).ConfigureAwait(false);
        return Result<BusinessCloseView>.Ok(new BusinessCloseView(businessDate, v.Revision, v.GeneratedUtc, v.GeneratedByName, ChangedSinceClose: false));
    }

    public async Task<Result<BusinessCloseView?>> GetBusinessCloseAsync(UserSession session, string businessDate, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReportsViewExport, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<BusinessCloseView?>();
        }

        if (!CalendarPolicy.TryParseDate(businessDate, out var date))
        {
            return Result<BusinessCloseView?>.Fail(ResultStatus.Validation, "Enter the date as YYYY-MM-DD.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var facts = await LedgerQuery.LoadAsync(db, businessDate, NextDay(date), cancellationToken).ConfigureAwait(false);
        return Result<BusinessCloseView?>.Ok(await CloseStatusAsync(db, businessDate, facts, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Records CSV and PDF artifact intents for a stored run; the materializer writes the files later.</summary>
    internal async Task QueueRunFilesAsync(Guid runId, string? closedBusinessDate, CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        await AddIntentAsync(db, runId, ReportFormat.Csv, now, cancellationToken).ConfigureAwait(false);
        await AddIntentAsync(db, runId, ReportFormat.Pdf, now, cancellationToken).ConfigureAwait(false);
        if (closedBusinessDate is not null)
        {
            await Sync.JobScheduler.RecordDailyCloseAsync(db, closedBusinessDate, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddIntentAsync(PosDbContext db, Guid runId, ReportFormat format, long now, CancellationToken cancellationToken)
    {
        var key = $"report/{runId:D}/{format.ToString().ToLowerInvariant()}";
        if (!await db.ArtifactRecords.AnyAsync(a => a.LogicalKey == key, cancellationToken).ConfigureAwait(false))
        {
            db.ArtifactRecords.Add(new ArtifactRecord
            {
                Id = Guid.NewGuid(),
                LogicalKey = key,
                Type = format == ReportFormat.Csv ? ArtifactType.ReportCsv : ArtifactType.ReportPdf,
                SourceId = runId,
                ReportRunId = runId,
                PayloadSchemaVersion = 1,
                State = ArtifactState.Intent,
                CreatedUtc = now,
            });
        }
    }

    // ---------------------------------------------------------------- rendering

    public static ReportFile Render(ReportRun run, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(run);
        var document = ReportJson.Read(run.SnapshotJson);
        var header = Header(run, document);
        var bytes = format == ReportFormat.Csv ? ReportCsv.Render(document, header) : ReportPdf.Render(document, header);
        var name = FileStem(run) + (format == ReportFormat.Csv ? ".csv" : ".pdf");
        return new ReportFile(name, bytes);
    }

    /// <summary>Human file name without extension: type, range, revision and snapshot hash prefix.</summary>
    public static string FileStem(ReportRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return $"{Slug(run.Type)}_{run.RangeStartDate}_to_{ReportBuilder.LastDate(run.RangeEndExclusiveDate)}_r{run.Revision}_{run.SnapshotSha256[..8]}";
    }

    public static ReportRunHeader Header(ReportRun run, ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(document);
        var local = new CalendarPolicy(document.TimeZoneId, 0).LocalTimeText(run.GeneratedUtc);
        return new ReportRunHeader(run.Revision, local, CanonicalJson.Sha256(run.LedgerWatermarkJson), run.FiltersJson == CloseFilters);
    }

    // ---------------------------------------------------------------- internals

    private async Task<Result<ReportRunView>> GenerateCoreAsync(UserSession session, ReportRequest request, string filtersJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var auth = await identity.AuthorizeAsync(session, Capabilities.ReportsViewExport, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<ReportRunView>();
        }

        if (!Enum.IsDefined(request.Type))
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, "Choose a report.");
        }

        var now = clock.UtcNowMilliseconds;
        var calendarResult = await profiles.GetEffectiveCalendarProfileAsync(now, cancellationToken).ConfigureAwait(false);
        if (!calendarResult.IsSuccess)
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, "No calendar profile is in effect; business dates cannot be reported.");
        }

        var calendar = calendarResult.Value!;
        var (start, end) = (request.StartDate, request.EndExclusiveDate);
        if (request.Type == ReportType.InventoryStatus)
        {
            // Inventory is a current balance: the run is keyed to today's business date.
            start = calendar.Policy.BusinessDateFor(now);
            end = NextDay(DateOnly.ParseExact(start, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!CalendarPolicy.TryParseDate(start, out var startDate) || !CalendarPolicy.TryParseDate(end, out var endDate))
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, "Enter dates as YYYY-MM-DD.");
        }

        var days = endDate.DayNumber - startDate.DayNumber;
        var maxDays = request.Type == ReportType.MonthlySales ? MaxMonthlyRangeDays : MaxRangeDays;
        if (days <= 0)
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, "The last date must be on or after the first date.");
        }

        if (days > maxDays)
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, $"Choose a range of at most {maxDays} days for this report.");
        }

        Built built;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            built = await BuildAsync(db, request.Type, start, end, calendar.Policy, now, cancellationToken).ConfigureAwait(false);
        }
        catch (ReportTooLargeException)
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, "There are too many transactions for one report. Choose a shorter range.");
        }
        catch (Exception ex) when (ex is FinancialRuleException or OverflowException)
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, "A total is outside the supported range. Choose a shorter range.");
        }
        catch (MixedCurrencyException)
        {
            return Result<ReportRunView>.Fail(ResultStatus.Validation, "This range contains sales in more than one currency, which reports do not combine.");
        }

        var snapshot = ReportJson.Write(built.Document);
        var snapshotSha = CanonicalJson.Sha256(snapshot);
        var filtersSha = CanonicalJson.Sha256(filtersJson);

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var write = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var latest = await write.ReportRuns.AsNoTracking()
            .Where(r => r.Type == request.Type && r.RangeStartDate == start && r.RangeEndExclusiveDate == end && r.FiltersSha256 == filtersSha)
            .OrderByDescending(r => r.Revision).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest.SnapshotSha256 == snapshotSha && latest.LedgerWatermarkJson == built.WatermarkJson)
        {
            return Result<ReportRunView>.Ok(await ViewAsync(write, latest, reused: true, cancellationToken).ConfigureAwait(false));
        }

        var run = new ReportRun
        {
            Id = Guid.NewGuid(),
            Type = request.Type,
            RangeStartDate = start,
            RangeEndExclusiveDate = end,
            CalendarPolicyId = calendar.PolicyId,
            FiltersJson = filtersJson,
            FiltersSha256 = filtersSha,
            LedgerWatermarkJson = built.WatermarkJson,
            Revision = (latest?.Revision ?? 0) + 1,
            GeneratedUtc = now,
            GeneratedByUserId = session.UserId,
            SnapshotJson = snapshot,
            SnapshotSha256 = snapshotSha,
        };
        write.ReportRuns.Add(run);
        AuditRecorder.Record(write, now, session.UserId, session.DisplayName, filtersJson == CloseFilters ? "Reports.BusinessDayClosed" : "Reports.Generated",
            "ReportRun", run.Id.ToString(), null, new { type = request.Type.ToString(), start, end, run.Revision });
        await write.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<ReportRunView>.Ok(await ViewAsync(write, run, reused: false, cancellationToken).ConfigureAwait(false));
    }

    private sealed record Built(ReportDocument Document, string WatermarkJson);

    private static async Task<Built> BuildAsync(PosDbContext db, ReportType type, string start, string end, CalendarPolicy calendar, long now, CancellationToken cancellationToken)
    {
        var installation = await db.InstallationState.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var isTest = !installation.ProductionEnabled;

        if (type == ReportType.InventoryStatus)
        {
            var products = await (from p in db.Products.AsNoTracking()
                                  join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                                  join i in db.Inventory.AsNoTracking() on p.Id equals i.ProductId into balances
                                  from i in balances.DefaultIfEmpty()
                                  select new InventoryRow(p.Sku, p.Name, c.Name, p.TrackStock, p.TrackStock && i != null ? i.QuantityQ : null, p.IsActive))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var ctx = new ReportContext(type, start, end, Currency.Php.Code, Currency.Php.Exponent, calendar, isTest);
            var doc = ReportBuilder.InventoryStatus(ctx, products);
            var movements = await db.InventoryMovements.CountAsync(cancellationToken).ConfigureAwait(false);
            var lastMovement = await db.InventoryMovements.MaxAsync(m => (long?)m.CreatedUtc, cancellationToken).ConfigureAwait(false) ?? 0;
            return new Built(doc, Watermark(w =>
            {
                w.WriteNumber("movements", movements);
                w.WriteNumber("lastMovementUtc", lastMovement);
                w.WriteString("contentSha256", CanonicalJson.Sha256(ReportJson.Write(doc)));
            }));
        }

        if (type == ReportType.CashierShift)
        {
            var from = calendar.StartUtcOf(start);
            var to = calendar.StartUtcOf(end);
            var shifts = await db.CashierShifts.AsNoTracking().Where(s => s.OpenedUtc >= from && s.OpenedUtc < to)
                .OrderBy(s => s.OpenedUtc).ThenBy(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<ShiftRow>(shifts.Count);
            foreach (var shift in shifts)
            {
                var view = await ShiftService.ViewAsync(db, shift, cancellationToken).ConfigureAwait(false);
                rows.Add(new ShiftRow(view.CashierName, view.OpenedUtc, view.ClosedUtc, view.Cash, view.CountedMinor, view.VarianceMinor));
            }

            var ctx = new ReportContext(type, start, end, Currency.Php.Code, Currency.Php.Exponent, calendar, isTest);
            var doc = ReportBuilder.CashierShifts(ctx, rows);
            return new Built(doc, Watermark(w =>
            {
                w.WriteNumber("shifts", shifts.Count);
                w.WriteString("contentSha256", CanonicalJson.Sha256(ReportJson.Write(doc)));
            }));
        }

        var facts = await LedgerQuery.LoadAsync(db, start, end, cancellationToken).ConfigureAwait(false);
        var currencies = facts.Sales.Select(s => (s.CurrencyCode, s.CurrencyExponent)).Distinct().ToArray();
        if (currencies.Length > 1)
        {
            throw new MixedCurrencyException();
        }

        var (code, exponent) = currencies.Length == 1 ? currencies[0] : (Currency.Php.Code, Currency.Php.Exponent);
        var context = new ReportContext(type, start, end, code, exponent, calendar, isTest);
        var document = type switch
        {
            ReportType.DailySales => ReportBuilder.Sales(context, facts, d => d, "Date"),
            ReportType.MonthlySales => ReportBuilder.Sales(context, facts, ReportBuilder.MonthOf, "Month"),
            ReportType.PaymentSummary => ReportBuilder.PaymentSummary(context, facts),
            ReportType.ProductSales => ReportBuilder.ProductSales(context, await LedgerQuery.LoadProductLinesAsync(db, facts, cancellationToken).ConfigureAwait(false)),
            ReportType.TransactionDetail => ReportBuilder.TransactionDetail(context, facts,
                await ShiftService.NamesAsync(db, facts.Sales.Select(s => s.CashierUserId).Concat(facts.Corrections.Select(c => c.ProcessedByUserId)), cancellationToken).ConfigureAwait(false)),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        return new Built(document, LedgerWatermark(facts));
    }

    private static string LedgerWatermark(LedgerFacts facts) =>
        Watermark(w =>
        {
            w.WriteNumber("sales", facts.Sales.Count);
            w.WriteNumber("corrections", facts.Corrections.Count);
            w.WriteNumber("lastEventUtc", facts.LastEventUtc);
            w.WriteString("inclusionSha256", facts.InclusionSha256());
        });

    private static string Watermark(Action<System.Text.Json.Utf8JsonWriter> body) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", WatermarkSchema);
            body(w);
            w.WriteEndObject();
        });

    private static async Task<BusinessCloseView?> CloseStatusAsync(PosDbContext db, string businessDate, LedgerFacts current, CancellationToken cancellationToken)
    {
        var date = DateOnly.ParseExact(businessDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var end = NextDay(date);
        var closeSha = CanonicalJson.Sha256(CloseFilters);
        var latest = await db.ReportRuns.AsNoTracking()
            .Where(r => r.Type == ReportType.DailySales && r.RangeStartDate == businessDate && r.RangeEndExclusiveDate == end && r.FiltersSha256 == closeSha)
            .OrderByDescending(r => r.Revision).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return null;
        }

        var names = await ShiftService.NamesAsync(db, [latest.GeneratedByUserId], cancellationToken).ConfigureAwait(false);
        return new BusinessCloseView(businessDate, latest.Revision, latest.GeneratedUtc, names.GetValueOrDefault(latest.GeneratedByUserId, "?"),
            latest.LedgerWatermarkJson != LedgerWatermark(current));
    }

    private static async Task<ReportRunView> ViewAsync(PosDbContext db, ReportRun run, bool reused, CancellationToken cancellationToken)
    {
        var names = await ShiftService.NamesAsync(db, [run.GeneratedByUserId], cancellationToken).ConfigureAwait(false);
        var document = ReportJson.Read(run.SnapshotJson);
        return new ReportRunView(run.Id, run.Revision, run.GeneratedUtc, names.GetValueOrDefault(run.GeneratedByUserId, "?"),
            CanonicalJson.Sha256(run.LedgerWatermarkJson), run.FiltersJson == CloseFilters, reused, document, ReportText.Lines(document, Header(run, document)));
    }

    private static string NextDay(DateOnly date) => date.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Slug(ReportType type) => type switch
    {
        ReportType.DailySales => "daily-sales",
        ReportType.PaymentSummary => "payment-summary",
        ReportType.ProductSales => "product-sales",
        ReportType.InventoryStatus => "inventory-status",
        ReportType.CashierShift => "cashier-shifts",
        ReportType.MonthlySales => "monthly-sales",
        ReportType.TransactionDetail => "transaction-detail",
        _ => "report",
    };

    private sealed class MixedCurrencyException() : Exception("Mixed currencies.");
}
