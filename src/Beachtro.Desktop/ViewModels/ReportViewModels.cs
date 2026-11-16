using System.Globalization;
using System.IO;
using System.Windows.Input;
using Beachtro.Application.Reports;
using Beachtro.Domain.Calendar;
using Beachtro.Domain.Model;

namespace Beachtro.Desktop.ViewModels;

/// <summary>Daily indicators (FR-021). Every figure comes from the report service.</summary>
public sealed class DashboardViewModel : PageViewModel
{
    private string _dateText = string.Empty;
    private DashboardView? _view;

    public DashboardViewModel(ShellViewModel shell)
        : base(shell)
    {
        RefreshCommand = Command(_ => RefreshAsync());
        TodayCommand = Command(async _ =>
        {
            DateText = await TodayAsync();
            await RefreshAsync();
        });
        CloseDayCommand = Command(_ => CloseDayAsync(), _ => View is not null);
    }

    public override string Title => "Dashboard";

    public string DateText { get => _dateText; set => Set(ref _dateText, value); }

    public DashboardView? View
    {
        get => _view;
        private set
        {
            if (Set(ref _view, value))
            {
                OnPropertyChanged(nameof(Tiles));
            }
        }
    }

    public IReadOnlyList<DashboardTile> Tiles => View is not { } v
        ? []
        :
        [
            new("Sales today", v.SaleCount.ToString(CultureInfo.InvariantCulture), "Completed sales on this business date"),
            new("Net revenue (ex-tax)", Formatting.Money(v.NetRevenueExTaxMinor, v.CurrencyExponent), "Sales minus refunds and voids, without tax"),
            new("Net collected", Formatting.Money(v.NetCollectedMinor, v.CurrencyExponent), "Money taken minus money refunded, including tax"),
            new("Cash", Formatting.Money(v.NetCashMinor, v.CurrencyExponent), "Net cash collected (change not counted)"),
            new("GCash", Formatting.Money(v.NetGCashMinor, v.CurrencyExponent), "Net GCash collected (recorded, not a statement check)"),
            new("Average sale", Formatting.Money(v.AverageSaleMinor, v.CurrencyExponent), "Including tax"),
            new("Refunds and voids", $"{v.CorrectionCount} · {Formatting.Money(v.CorrectionsTotalMinor, v.CurrencyExponent)}", "Made on this business date"),
            new("Net tax", Formatting.Money(v.NetTaxMinor, v.CurrencyExponent), "TEST tax settings: not for filing"),
            new("Open shifts", v.OpenShiftCount.ToString(CultureInfo.InvariantCulture), "Shifts not yet closed (any date)"),
            new("Out of stock", v.OutOfStockCount.ToString(CultureInfo.InvariantCulture), "Active tracked products at zero or less"),
        ];

    public ICommand RefreshCommand { get; }
    public ICommand TodayCommand { get; }
    public ICommand CloseDayCommand { get; }

    public override async Task LoadAsync()
    {
        DateText = await TodayAsync();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var result = await Services.Reports.GetDashboardAsync(Session, DateText.Trim());
        View = Check(result) ? result.Value : null;
        Info = View?.LastCloseSummary;
    }

    private async Task CloseDayAsync()
    {
        var result = await Services.Reports.CloseBusinessDayAsync(Session, DateText.Trim());
        if (Check(result))
        {
            await RefreshAsync();
            Info = $"Business day {result.Value!.BusinessDate} closed (revision {result.Value.Revision}). Sales and refunds are not locked; a later change shows here.";
        }
    }
}

public sealed record DashboardTile(string Label, string Value, string Hint);

public sealed record ReportOption(ReportType Type, string Name, string Description);

/// <summary>Report generation, preview and CSV/PDF export (FR-022, FR-023).</summary>
public sealed class ReportsViewModel : PageViewModel
{
    private ReportOption _selected;
    private string _startText = string.Empty;
    private string _endText = string.Empty;
    private ReportRunView? _run;

    public ReportsViewModel(ShellViewModel shell)
        : base(shell)
    {
        _selected = Options[0];
        GenerateCommand = Command(_ => GenerateAsync());
        RangeCommand = Command(p => SetRangeAsync(p as string ?? "today"));
        ExportCsvCommand = Command(_ => ExportAsync(ReportFormat.Csv), _ => Run is not null);
        ExportPdfCommand = Command(_ => ExportAsync(ReportFormat.Pdf), _ => Run is not null);
    }

    public override string Title => "Reports";

    public IReadOnlyList<ReportOption> Options { get; } =
    [
        new(ReportType.DailySales, "Daily Sales", "Sales, refunds, tax and money collected for each business day in the range."),
        new(ReportType.MonthlySales, "Monthly Sales", "The same figures grouped by month."),
        new(ReportType.PaymentSummary, "Payment Summary", "Cash and GCash collected and refunded."),
        new(ReportType.ProductSales, "Product Sales", "Units sold and returned, and sales per product."),
        new(ReportType.TransactionDetail, "Transaction Detail", "Every sale, refund and void in the range."),
        new(ReportType.CashierShift, "Cashier Shifts", "Shifts opened in the range: expected and counted cash."),
        new(ReportType.InventoryStatus, "Inventory Status", "Current stock on hand (dates are ignored)."),
    ];

    public ReportOption Selected { get => _selected; set => Set(ref _selected, value); }
    public string StartText { get => _startText; set => Set(ref _startText, value); }
    public string EndText { get => _endText; set => Set(ref _endText, value); }

    public ReportRunView? Run
    {
        get => _run;
        private set
        {
            if (Set(ref _run, value))
            {
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(RunSummary));
            }
        }
    }

    public string Preview => Run is null ? string.Empty : string.Join(Environment.NewLine, Run.PreviewLines);

    public string RunSummary => Run is null
        ? string.Empty
        : $"Revision {Run.Revision}, generated {new CalendarPolicy(Run.Document.TimeZoneId, 0).LocalTimeText(Run.GeneratedUtc)} ({Run.Document.TimeZoneId}) by {Run.GeneratedByName}"
          + (Run.ReusedExistingRun ? " — nothing changed since, so the stored report was reused." : ".");

    public ICommand GenerateCommand { get; }
    public ICommand RangeCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportPdfCommand { get; }

    public override async Task LoadAsync() => await SetRangeAsync("today");

    private async Task SetRangeAsync(string range)
    {
        var today = DateOnly.ParseExact(await TodayAsync(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        (DateOnly Start, DateOnly End) span = range switch
        {
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "week" => (today.AddDays(-6), today),
            "month" => (monthStart, today),
            "lastmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            _ => (today, today),
        };
        StartText = Iso(span.Start);
        EndText = Iso(span.End);
    }

    private async Task GenerateAsync()
    {
        if (!DateOnly.TryParseExact(StartText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(EndText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
        {
            Error = "Enter dates as YYYY-MM-DD.";
            return;
        }

        Info = null;
        var result = await Services.Reports.GenerateAsync(Session, new ReportRequest(Selected.Type, Iso(start), Iso(last.AddDays(1))));
        Run = Check(result) ? result.Value : null;
    }

    private async Task ExportAsync(ReportFormat format)
    {
        if (Run is null)
        {
            return;
        }

        var file = await Services.Reports.RenderAsync(Session, Run.RunId, format);
        if (!Check(file))
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = file.Value!.FileName,
            Filter = format == ReportFormat.Csv ? "CSV file (*.csv)|*.csv" : "PDF file (*.pdf)|*.pdf",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Write a temporary file next to the destination, then replace, so a failure never leaves half a file.
        var temp = dialog.FileName + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temp, file.Value.Content);
            File.Move(temp, dialog.FileName, overwrite: true);
            Info = $"Saved to {dialog.FileName}. Anyone who can open that folder can read this file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "The file could not be saved there. Choose another folder and try again.";
            Serilog.Log.Warning(ex, "Report export to the chosen folder failed");
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(cleanup, "Could not remove partial export file");
            }
        }
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
