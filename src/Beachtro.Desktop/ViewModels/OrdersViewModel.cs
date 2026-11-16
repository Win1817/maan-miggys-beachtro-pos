using System.Collections.ObjectModel;
using System.Windows.Input;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Desktop.Mvvm;
using Beachtro.Domain.Model;

namespace Beachtro.Desktop.ViewModels;

public sealed class RefundLineViewModel(OrderLineDetail line) : ObservableObject
{
    private int _returnUnits;
    private bool _returnToStock = line.TrackStock;

    public OrderLineDetail Line { get; } = line;
    public string Name => Line.Name;
    public string Sold => Formatting.Units(Line.QuantityQ);
    public string Remaining => Formatting.Units(Line.RemainingQ);
    public string Total => Formatting.Money(Line.TotalMinor);
    public bool TrackStock => Line.TrackStock;
    public int ReturnUnits { get => _returnUnits; set => Set(ref _returnUnits, Math.Clamp(value, 0, (int)(Line.RemainingQ / 1000))); }

    /// <summary>Tracked items: back to stock (Return) or written off (Discard).</summary>
    public bool ReturnToStock { get => _returnToStock; set => Set(ref _returnToStock, value); }

    public StockDisposition Disposition => !TrackStock ? StockDisposition.NotTracked : ReturnToStock ? StockDisposition.Return : StockDisposition.Discard;
}

/// <summary>Order history, receipt preview, reprint and refund/void (IB-14/IB-15; FR-016–018).</summary>
public sealed class OrdersViewModel : PageViewModel
{
    private string _dateFilter = string.Empty;
    private string _search = string.Empty;
    private OrderSummary? _selected;
    private OrderDetail? _detail;
    private string _receiptText = string.Empty;
    private string _reason = string.Empty;
    private bool _moneyReturned;
    private string _gcashReference = string.Empty;

    public OrdersViewModel(ShellViewModel shell)
        : base(shell)
    {
        RefreshCommand = Command(_ => LoadAsync());
        PrintOriginalCommand = Command(_ => PrintAsync(reprint: false), _ => Detail is not null);
        ReprintCommand = Command(_ => PrintAsync(reprint: true), _ => Detail is not null && CanReprint);
        RefundCommand = Command(p => CorrectAsync(CorrectionKind.Refund, p), _ => Detail is not null && CanRefund);
        VoidCommand = Command(p => CorrectAsync(CorrectionKind.Void, p), _ => Detail is not null && CanVoid && Detail.Corrections.Count == 0);
    }

    public override string Title => "Orders";

    public ObservableCollection<OrderSummary> Orders { get; } = [];
    public ObservableCollection<RefundLineViewModel> Lines { get; } = [];
    public ObservableCollection<CorrectionSummary> Corrections { get; } = [];

    public string DateFilter { get => _dateFilter; set => Set(ref _dateFilter, value); }
    public string Search { get => _search; set => Set(ref _search, value); }
    public OrderSummary? Selected { get => _selected; set { if (Set(ref _selected, value)) { _ = LoadDetailAsync(); } } }
    public OrderDetail? Detail { get => _detail; private set { if (Set(ref _detail, value)) { OnPropertyChanged(nameof(DetailHeading)); OnPropertyChanged(nameof(IsGCashSale)); } } }
    public string DetailHeading => Detail is null ? "Select a sale" : $"{Detail.Summary.TransactionNumber} · {Detail.Summary.Method} · {Formatting.Money(Detail.Summary.TotalMinor)}";
    public string ReceiptText { get => _receiptText; private set => Set(ref _receiptText, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    public bool MoneyReturned { get => _moneyReturned; set => Set(ref _moneyReturned, value); }
    public string GCashReference { get => _gcashReference; set => Set(ref _gcashReference, value); }
    public bool IsGCashSale => Detail?.Summary.Method == PaymentMethod.GCash;

    public bool CanReprint => Services.Sessions.Current?.Has(Capabilities.ReceiptsReprint) ?? false;
    public bool CanRefund => Services.Sessions.Current?.Has(Capabilities.CorrectionsRefund) ?? false;
    public bool CanVoid => Services.Sessions.Current?.Has(Capabilities.CorrectionsVoid) ?? false;

    public ICommand RefreshCommand { get; }
    public ICommand PrintOriginalCommand { get; }
    public ICommand ReprintCommand { get; }
    public ICommand RefundCommand { get; }
    public ICommand VoidCommand { get; }

    public static string Money(long minor) => Formatting.Money(minor);

    public override async Task LoadAsync()
    {
        var result = await Services.Orders.ListAsync(Session, string.IsNullOrWhiteSpace(DateFilter) ? null : DateFilter.Trim(), Search);
        if (!Check(result))
        {
            return;
        }

        Orders.Clear();
        foreach (var o in result.Value!)
        {
            Orders.Add(o);
        }

        Info = Orders.Count == 0 ? "No sales found." : null;
    }

    private async Task LoadDetailAsync()
    {
        Lines.Clear();
        Corrections.Clear();
        ReceiptText = string.Empty;
        Detail = null;
        if (Selected is null)
        {
            return;
        }

        var detail = await Services.Orders.GetAsync(Session, Selected.OrderId);
        if (!Check(detail))
        {
            return;
        }

        Detail = detail.Value;
        foreach (var line in Detail!.Lines)
        {
            Lines.Add(new RefundLineViewModel(line));
        }

        foreach (var c in Detail.Corrections)
        {
            Corrections.Add(c);
        }

        var receipt = await Services.Print.RenderSaleAsync(Session, Selected.OrderId);
        ReceiptText = receipt.IsSuccess ? receipt.Value!.Text : string.Empty;
    }

    private async Task PrintAsync(bool reprint)
    {
        if (Detail is null)
        {
            return;
        }

        var result = reprint
            ? await Services.Print.ReprintAsync(Session, Detail.Summary.OrderId, null, Reason)
            : await Services.Print.PrintOriginalAsync(Session, Detail.Summary.OrderId, null);
        if (Check(result))
        {
            Info = result.Value!.State == PrintJobState.SpoolAccepted
                ? $"Sent to {result.Value.PrinterName}."
                : "Not printed: " + result.Value.LastError;
        }
    }

    private async Task CorrectAsync(CorrectionKind kind, object? passwordBox)
    {
        if (Detail is null)
        {
            return;
        }

        var password = ShellViewModel.TakePassword(passwordBox);
        if (!MoneyReturned)
        {
            Error = Detail.Summary.Method == PaymentMethod.Cash
                ? "Tick the box once the cash has been handed back to the customer."
                : "Tick the box once the GCash refund has been sent and confirmed.";
            return;
        }

        var lines = kind == CorrectionKind.Void
            ? Lines.Select(l => new CorrectionLineInput(l.Line.OrderItemId, l.Line.QuantityQ, l.Disposition)).ToArray()
            : Lines.Where(l => l.ReturnUnits > 0).Select(l => new CorrectionLineInput(l.Line.OrderItemId, l.ReturnUnits * 1000L, l.Disposition)).ToArray();
        if (lines.Length == 0)
        {
            Error = "Set how many units are being returned.";
            return;
        }

        // Refunds and voids need the password re-entered (security design §Sessions).
        if (!Check(await Task.Run(() => Services.Identity.ReauthenticateAsync(Session, password))))
        {
            return;
        }

        var requestId = Guid.NewGuid();
        var prepared = await Services.Corrections.PrepareAsync(Session, requestId, new CorrectionInput(Detail.Summary.OrderId, kind, lines, Reason));
        if (!Check(prepared))
        {
            return;
        }

        RefundEvidence evidence = Detail.Summary.Method == PaymentMethod.Cash ? new CashPaidOut() : new GCashRefunded(GCashReference);
        var completed = await Services.Corrections.CompleteAsync(Session, requestId, evidence);
        if (!Check(completed))
        {
            return;
        }

        var receipt = completed.Value!;
        Info = $"{receipt.Kind} recorded: {Formatting.Money(receipt.TotalMinor)} returned.";
        var print = await Services.Print.PrintOriginalAsync(Session, null, receipt.CorrectionId);
        if (print.IsSuccess && print.Value!.State != PrintJobState.SpoolAccepted)
        {
            Info += " Refund slip not printed: " + print.Value.LastError;
        }

        Reason = string.Empty;
        MoneyReturned = false;
        GCashReference = string.Empty;
        var keep = Selected;
        await LoadAsync();
        Selected = Orders.FirstOrDefault(o => o.OrderId == keep?.OrderId);
    }
}
