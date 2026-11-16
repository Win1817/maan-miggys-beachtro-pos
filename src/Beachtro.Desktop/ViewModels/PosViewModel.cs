using System.Collections.ObjectModel;
using System.Windows.Input;
using Beachtro.Application.Catalog;
using Beachtro.Application.Common;
using Beachtro.Application.Receipts;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Desktop.Mvvm;
using Beachtro.Domain.Model;

namespace Beachtro.Desktop.ViewModels;

public sealed class CartLineViewModel(ProductView product) : ObservableObject
{
    private int _units = 1;
    private string _lineTotal = string.Empty;

    public ProductView Product { get; } = product;
    public string Name => Product.Name;
    public string UnitPrice => Formatting.Money(Product.UnitPriceMinor, Product.CurrencyExponent);
    public int Units { get => _units; set => Set(ref _units, value); }
    public string LineTotal { get => _lineTotal; set => Set(ref _lineTotal, value); }
}

public sealed record CategoryFilter(Guid? Id, string Name);

/// <summary>
/// POS screen (IB-13). Totals always come from the server-side quote; the screen never computes
/// money itself. A request id is created when payment starts and reused for retries of the same cart,
/// so a double tap or a lost response can never complete two sales.
/// </summary>
public sealed class PosViewModel : PageViewModel
{
    private IReadOnlyList<ProductView> _allProducts = [];
    private CategoryFilter? _category;
    private string _search = string.Empty;
    private bool _isTakeout;
    private bool _isGCash;
    private string _tenderedText = string.Empty;
    private bool _gcashVerified;
    private string _gcashReference = string.Empty;
    private Guid? _requestId;
    private long _totalMinor;
    private string _subtotal = "0.00";
    private string _discount = string.Empty;
    private string _tax = string.Empty;
    private string _total = "0.00";
    private string? _lastSale;
    private PrintJobStatus? _lastPrint;

    public PosViewModel(ShellViewModel shell)
        : base(shell)
    {
        AddCommand = Command(p => p is ProductView product ? AddAsync(product) : Task.CompletedTask);
        IncreaseCommand = Command(p => ChangeUnitsAsync(p as CartLineViewModel, +1));
        DecreaseCommand = Command(p => ChangeUnitsAsync(p as CartLineViewModel, -1));
        RemoveCommand = Command(p => ChangeUnitsAsync(p as CartLineViewModel, int.MinValue));
        ClearCommand = Command(_ => ResetCartAsync());
        ExactCashCommand = new RelayCommand(_ => TenderedText = Formatting.Money(_totalMinor));
        QuickCashCommand = new RelayCommand(p => TenderedText = p?.ToString() ?? string.Empty);
        CompleteCommand = Command(_ => CompleteAsync(), _ => Cart.Count > 0 && _totalMinor > 0);
        ConfirmPrintedCommand = Command(p => ConfirmPrintAsync(p is true or "True"));
        RetryPrintCommand = Command(_ => RetryPrintAsync(), _ => _lastPrint is { State: PrintJobState.Failed });
    }

    public override string Title => "Sell";

    public ObservableCollection<CategoryFilter> Categories { get; } = [];
    public ObservableCollection<ProductView> Products { get; } = [];
    public ObservableCollection<CartLineViewModel> Cart { get; } = [];

    public CategoryFilter? Category { get => _category; set { if (Set(ref _category, value)) { Filter(); } } }
    public string Search { get => _search; set { if (Set(ref _search, value)) { Filter(); } } }

    public bool IsTakeout { get => _isTakeout; set { if (Set(ref _isTakeout, value)) { InvalidateRequest(); } } }
    public bool IsDineIn { get => !_isTakeout; set => IsTakeout = !value; }

    public bool IsGCash { get => _isGCash; set { if (Set(ref _isGCash, value)) { OnPropertyChanged(nameof(IsCash)); OnPropertyChanged(nameof(ChangeText)); } } }
    public bool IsCash { get => !_isGCash; set => IsGCash = !value; }
    public string TenderedText { get => _tenderedText; set { if (Set(ref _tenderedText, value)) { OnPropertyChanged(nameof(ChangeText)); } } }
    public bool GCashVerified { get => _gcashVerified; set => Set(ref _gcashVerified, value); }
    public string GCashReference { get => _gcashReference; set => Set(ref _gcashReference, value); }

    public string Subtotal { get => _subtotal; private set => Set(ref _subtotal, value); }
    public string Discount { get => _discount; private set => Set(ref _discount, value); }
    public string Tax { get => _tax; private set => Set(ref _tax, value); }
    public string Total { get => _total; private set => Set(ref _total, value); }

    /// <summary>Display hint only; the recorded change comes from the completed sale.</summary>
    public string ChangeText => IsCash && Formatting.TryParseMoney(TenderedText, out var tendered) && tendered >= _totalMinor && _totalMinor > 0
        ? "Change " + Formatting.Money(tendered - _totalMinor)
        : string.Empty;

    public string? LastSale { get => _lastSale; private set => Set(ref _lastSale, value); }
    public PrintJobStatus? LastPrint { get => _lastPrint; private set { if (Set(ref _lastPrint, value)) { OnPropertyChanged(nameof(PrintStatusText)); OnPropertyChanged(nameof(AskPrintConfirmation)); } } }
    public bool AskPrintConfirmation => LastPrint is { State: PrintJobState.SpoolAccepted };
    public string PrintStatusText => LastPrint switch
    {
        null => string.Empty,
        { State: PrintJobState.SpoolAccepted } => $"Receipt sent to {LastPrint.PrinterName}. Did it print?",
        { State: PrintJobState.OperatorConfirmed } => "Receipt printed.",
        { State: PrintJobState.Failed } => "Receipt not printed: " + LastPrint.LastError,
        _ => "Receipt status: " + LastPrint.State,
    };

    public ICommand AddCommand { get; }
    public ICommand IncreaseCommand { get; }
    public ICommand DecreaseCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ExactCashCommand { get; }
    public ICommand QuickCashCommand { get; }
    public ICommand CompleteCommand { get; }
    public ICommand ConfirmPrintedCommand { get; }
    public ICommand RetryPrintCommand { get; }

    public override async Task LoadAsync()
    {
        var categories = await Services.Catalog.ListCategoriesAsync(Session, includeInactive: false);
        var products = await Services.Catalog.SearchProductsAsync(Session, null, includeInactive: false);
        if (!Check(categories) || !Check(products))
        {
            return;
        }

        Categories.Clear();
        Categories.Add(new CategoryFilter(null, "All"));
        foreach (var c in categories.Value!)
        {
            Categories.Add(new CategoryFilter(c.Id, c.Name));
        }

        _allProducts = products.Value!;
        _category = Categories[0];
        OnPropertyChanged(nameof(Category));
        Filter();
        if (_allProducts.Count == 0)
        {
            Info = "No products yet. An owner can load sample data in Settings (Test profile) or add products.";
        }
    }

    private async Task RefreshProductsAsync()
    {
        var products = await Services.Catalog.SearchProductsAsync(Session, null, includeInactive: false);
        if (products.IsSuccess)
        {
            _allProducts = products.Value!;
            Filter();
        }
    }

    private void Filter()
    {
        Products.Clear();
        foreach (var p in _allProducts.Where(p => (Category?.Id is null || p.CategoryId == Category.Id)
                     && (Search.Length == 0 || p.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || p.Sku.Contains(Search, StringComparison.OrdinalIgnoreCase))))
        {
            Products.Add(p);
        }
    }

    private async Task AddAsync(ProductView product)
    {
        LastSale = null;
        LastPrint = null;
        var line = Cart.FirstOrDefault(l => l.Product.Id == product.Id);
        if (line is null)
        {
            Cart.Add(new CartLineViewModel(product));
        }
        else
        {
            line.Units++;
        }

        await RequoteAsync();
    }

    private async Task ChangeUnitsAsync(CartLineViewModel? line, int delta)
    {
        if (line is null)
        {
            return;
        }

        if (delta == int.MinValue || line.Units + delta <= 0)
        {
            Cart.Remove(line);
        }
        else
        {
            line.Units += delta;
        }

        await RequoteAsync();
    }

    private async Task ResetCartAsync()
    {
        Cart.Clear();
        TenderedText = string.Empty;
        GCashVerified = false;
        GCashReference = string.Empty;
        await RequoteAsync();
    }

    private CartInput CurrentCart() =>
        new(IsTakeout ? OrderType.Takeout : OrderType.DineIn, Cart.Select(l => new CartLineInput(l.Product.Id, l.Units * 1000L)).ToArray());

    private void InvalidateRequest() => _requestId = null;

    private async Task RequoteAsync()
    {
        InvalidateRequest();
        if (Cart.Count == 0)
        {
            SetTotals(0, 0, 0, 0);
            Error = null;
            return;
        }

        var quote = await Services.Checkout.QuoteAsync(Session, CurrentCart());
        if (!Check(quote))
        {
            return;
        }

        var calc = quote.Value!.Calculation;
        foreach (var line in quote.Value.Lines)
        {
            Cart[line.LineNo - 1].LineTotal = Formatting.Money(line.Calculation.PriceBasisMinor);
        }

        SetTotals(calc.PriceBasisSubtotalMinor, calc.DiscountBasisMinor, calc.TaxMinor, calc.TotalMinor);
    }

    private void SetTotals(long subtotal, long discount, long tax, long total)
    {
        _totalMinor = total;
        Subtotal = Formatting.Money(subtotal);
        Discount = discount == 0 ? string.Empty : "-" + Formatting.Money(discount);
        Tax = tax == 0 ? string.Empty : Formatting.Money(tax) + " (included)";
        Total = Formatting.Money(total);
        OnPropertyChanged(nameof(ChangeText));
    }

    private async Task CompleteAsync()
    {
        PaymentInput payment;
        if (IsCash)
        {
            if (!Formatting.TryParseMoney(TenderedText, out var tendered))
            {
                Error = "Enter the cash received, for example 500 or 250.50.";
                return;
            }

            payment = new CashPayment(tendered);
        }
        else
        {
            payment = new GCashPayment(GCashVerified, GCashReference, null);
        }

        var cart = CurrentCart();
        _requestId ??= Guid.NewGuid();
        var prepared = await Services.Checkout.PrepareAsync(Session, _requestId.Value, cart);
        if (!Check(prepared))
        {
            return;
        }

        var result = await Services.Checkout.CompleteAsync(Session, _requestId.Value, payment);
        if (result.Status == ResultStatus.StaleQuote)
        {
            Error = result.Message;
            await RequoteAsync();
            return;
        }

        if (result.Status == ResultStatus.OutcomeUnknown)
        {
            // Keep the request id: retrying returns the original sale if it did commit.
            Error = result.Message;
            return;
        }

        if (!Check(result))
        {
            return;
        }

        var sale = result.Value!;
        LastSale = sale.Method == PaymentMethod.Cash
            ? $"Sale {sale.TransactionNumber} complete. Total {Formatting.Money(sale.TotalMinor)} · Change {Formatting.Money(sale.ChangeMinor ?? 0)}"
            : $"Sale {sale.TransactionNumber} complete. Total {Formatting.Money(sale.TotalMinor)} · GCash";
        Info = sale.Warnings.Count > 0 ? string.Join(" ", sale.Warnings) : null;
        var saleId = sale.OrderId;
        _requestId = null;
        await ResetCartAsync();
        await RefreshProductsAsync(); // stock on the tiles reflects the sale
        Services.Sessions.RecordActivity();

        // Printing is outside the sale: a printer problem never undoes or blocks the sale.
        var print = await Services.Print.PrintOriginalAsync(Session, saleId, null);
        LastPrint = print.IsSuccess ? print.Value : null;
        _lastSaleId = saleId;
    }

    private Guid? _lastSaleId;

    private async Task RetryPrintAsync()
    {
        if (_lastSaleId is { } id)
        {
            var print = await Services.Print.PrintOriginalAsync(Session, id, null);
            if (Check(print))
            {
                LastPrint = print.Value;
            }
        }
    }

    private async Task ConfirmPrintAsync(bool printed)
    {
        if (LastPrint is null)
        {
            return;
        }

        var result = await Services.Print.ConfirmAsync(Session, LastPrint.JobId, printed);
        if (Check(result))
        {
            LastPrint = LastPrint with
            {
                State = printed ? PrintJobState.OperatorConfirmed : PrintJobState.Failed,
                LastError = printed ? null : "Reported as not printed. Use Retry, or reprint later from Orders.",
            };
        }
    }

    internal bool HasCapability(string capability) => Session.Has(capability);

    public bool CanDiscount => Services.Sessions.Current?.Has(Capabilities.DiscountsApply) ?? false;
}
