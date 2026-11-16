using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Beachtro.Application.Catalog;

namespace Beachtro.Desktop.ViewModels;

/// <summary>Categories and products (IB-14; FR-004–006).</summary>
public sealed class ProductsViewModel : PageViewModel
{
    private ProductView? _selected;
    private Guid? _editingId;
    private int? _editingVersion;
    private string _sku = string.Empty;
    private string _name = string.Empty;
    private string _priceText = string.Empty;
    private CategoryView? _category;
    private string? _taxKey;
    private bool _trackStock = true;
    private bool _isActive = true;
    private string _newCategory = string.Empty;

    public ProductsViewModel(ShellViewModel shell)
        : base(shell)
    {
        NewCommand = new Mvvm.RelayCommand(_ => Edit(null));
        SaveCommand = Command(_ => SaveAsync());
        AddCategoryCommand = Command(_ => AddCategoryAsync(), _ => NewCategory.Trim().Length > 0);
    }

    public override string Title => "Products";

    public ObservableCollection<ProductView> Products { get; } = [];
    public ObservableCollection<CategoryView> Categories { get; } = [];
    public ObservableCollection<string> TaxKeys { get; } = [];

    public ProductView? Selected { get => _selected; set { if (Set(ref _selected, value)) { Edit(value); } } }
    public string EditHeading => _editingId is null ? "New product" : "Edit product";
    public string Sku { get => _sku; set => Set(ref _sku, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string PriceText { get => _priceText; set => Set(ref _priceText, value); }
    public CategoryView? Category { get => _category; set => Set(ref _category, value); }
    public string? TaxKey { get => _taxKey; set => Set(ref _taxKey, value); }
    public bool TrackStock { get => _trackStock; set => Set(ref _trackStock, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string NewCategory { get => _newCategory; set => Set(ref _newCategory, value); }

    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddCategoryCommand { get; }

    public static string Price(ProductView p) => Formatting.Money(p.UnitPriceMinor, p.CurrencyExponent);

    public override async Task LoadAsync()
    {
        var categories = await Services.Catalog.ListCategoriesAsync(Session, includeInactive: true);
        var products = await Services.Catalog.SearchProductsAsync(Session, null, includeInactive: true);
        var profile = await Services.Profiles.GetEffectiveCalculationProfileAsync(Services.Clock.UtcNowMilliseconds);
        if (!Check(categories) || !Check(products))
        {
            return;
        }

        Categories.Clear();
        foreach (var c in categories.Value!)
        {
            Categories.Add(c);
        }

        Products.Clear();
        foreach (var p in products.Value!)
        {
            Products.Add(p);
        }

        TaxKeys.Clear();
        if (profile.IsSuccess)
        {
            foreach (var key in profile.Value!.TaxRules.Keys.Order(StringComparer.Ordinal))
            {
                TaxKeys.Add(key);
            }
        }
        else
        {
            Info = "No calculation profile is in effect. Load sample data in Settings (Test profile) before adding products.";
        }

        Edit(null);
    }

    private void Edit(ProductView? p)
    {
        _editingId = p?.Id;
        _editingVersion = p?.Version;
        Sku = p?.Sku ?? string.Empty;
        Name = p?.Name ?? string.Empty;
        PriceText = p is null ? string.Empty : Formatting.Money(p.UnitPriceMinor, p.CurrencyExponent).Replace(",", string.Empty, StringComparison.Ordinal);
        Category = Categories.FirstOrDefault(c => c.Id == p?.CategoryId) ?? Categories.FirstOrDefault();
        TaxKey = p?.TaxKey ?? TaxKeys.FirstOrDefault();
        TrackStock = p?.TrackStock ?? true;
        IsActive = p?.IsActive ?? true;
        OnPropertyChanged(nameof(EditHeading));
    }

    private async Task SaveAsync()
    {
        if (!Formatting.TryParseMoney(PriceText, out var price) || price <= 0)
        {
            Error = "Enter a price greater than zero, for example 95 or 95.50.";
            return;
        }

        if (Category is null || TaxKey is null)
        {
            Error = "Choose a category and a tax entry.";
            return;
        }

        var result = await Services.Catalog.SaveProductAsync(Session,
            new ProductInput(_editingId, Category.Id, Sku, Name, null, price, 1000, TrackStock, TaxKey, IsActive, _editingVersion));
        if (Check(result))
        {
            Info = $"Saved {Name}.";
            await LoadAsync();
        }
    }

    private async Task AddCategoryAsync()
    {
        if (Check(await Services.Catalog.SaveCategoryAsync(Session, new CategoryInput(null, NewCategory, null, true, null))))
        {
            NewCategory = string.Empty;
            await LoadAsync();
        }
    }
}

/// <summary>Stock on hand, adjustments with reasons and movement history (IB-14; FR-007–008).</summary>
public sealed class InventoryViewModel : PageViewModel
{
    private ProductView? _selected;
    private string _deltaText = string.Empty;
    private string _reason = string.Empty;
    private bool _isOpening;

    public InventoryViewModel(ShellViewModel shell)
        : base(shell)
    {
        ApplyCommand = Command(_ => ApplyAsync(), _ => Selected is not null);
    }

    public override string Title => "Inventory";

    public ObservableCollection<ProductView> Products { get; } = [];
    public ObservableCollection<StockMovementView> Movements { get; } = [];

    public ProductView? Selected { get => _selected; set { if (Set(ref _selected, value)) { _ = LoadMovementsAsync(); } } }
    public string DeltaText { get => _deltaText; set => Set(ref _deltaText, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    public bool IsOpening { get => _isOpening; set => Set(ref _isOpening, value); }
    public ICommand ApplyCommand { get; }

    public static string OnHand(ProductView p) => Formatting.Units(p.OnHandQ);

    public override async Task LoadAsync()
    {
        var products = await Services.Catalog.SearchProductsAsync(Session, null, includeInactive: true);
        if (!Check(products))
        {
            return;
        }

        var keep = Selected?.Id;
        Products.Clear();
        foreach (var p in products.Value!.Where(p => p.TrackStock))
        {
            Products.Add(p);
        }

        Selected = Products.FirstOrDefault(p => p.Id == keep);
    }

    private async Task LoadMovementsAsync()
    {
        Movements.Clear();
        if (Selected is null)
        {
            return;
        }

        var movements = await Services.Catalog.GetMovementsAsync(Session, Selected.Id);
        if (Check(movements))
        {
            foreach (var m in movements.Value!.Reverse())
            {
                Movements.Add(m);
            }
        }
    }

    private async Task ApplyAsync()
    {
        if (Selected is null || !int.TryParse(DeltaText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var units) || units == 0)
        {
            Error = "Enter a whole number of units to add (for example 12) or remove (for example -3).";
            return;
        }

        var result = await Services.Catalog.AdjustStockAsync(Session, new StockAdjustmentInput(Guid.NewGuid(), Selected.Id, units * 1000L, Reason, IsOpening));
        if (Check(result))
        {
            Info = $"{Selected.Name}: on hand is now {Formatting.Units(result.Value)}.";
            DeltaText = string.Empty;
            Reason = string.Empty;
            IsOpening = false;
            await LoadAsync();
        }
    }
}
