namespace Beachtro.Domain.Financial;

/// <summary>
/// ISO-style currency code and its minor-unit exponent. Snapshotted on every historical record
/// so history is independent of current settings.
/// </summary>
public readonly record struct Currency
{
    public Currency(string code, int exponent)
    {
        if (code is null || code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new FinancialRuleException(FinancialErrorCode.InvalidCurrency, "Currency code must be three uppercase letters.");
        }

        if (exponent is < 0 or > FinancialLimits.MaxCurrencyExponent)
        {
            throw new FinancialRuleException(FinancialErrorCode.InvalidCurrency, $"Currency exponent must be 0..{FinancialLimits.MaxCurrencyExponent}.");
        }

        Code = code;
        Exponent = exponent;
    }

    public string Code { get; }
    public int Exponent { get; }

    /// <summary>Philippine peso, two decimals. Provisional business default (B-01, POS-DEC-002).</summary>
    public static Currency Php { get; } = new("PHP", 2);

    public override string ToString() => $"{Code}/{Exponent}";
}
