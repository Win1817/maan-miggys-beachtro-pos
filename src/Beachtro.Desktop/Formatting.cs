using System.Globalization;
using Beachtro.Application.Common;

namespace Beachtro.Desktop;

/// <summary>Display and input conversion only. All arithmetic stays in the integer money kernel.</summary>
public static class Formatting
{
    public const int PhpExponent = 2;

    public static string Money(long minor, int exponent = PhpExponent)
    {
        var scale = (decimal)Math.Pow(10, exponent);
        return (minor / scale).ToString("#,##0." + new string('0', exponent), CultureInfo.InvariantCulture).TrimEnd('.');
    }

    /// <summary>Parses "1,234.50" into exact minor units; rejects more decimals than the currency has.</summary>
    public static bool TryParseMoney(string? text, out long minor, int exponent = PhpExponent)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)
            || !decimal.TryParse(text.Trim().Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var scaled = value * (decimal)Math.Pow(10, exponent);
        if (scaled != decimal.Truncate(scaled) || scaled < 0 || scaled > 100_000_000_000m)
        {
            return false;
        }

        minor = (long)scaled;
        return true;
    }

    public static string Units(long quantityQ) =>
        quantityQ % 1000 == 0
            ? (quantityQ / 1000).ToString(CultureInfo.InvariantCulture)
            : (quantityQ / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    public static string LocalTime(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Safe message for a failed result; raw exceptions never reach the screen.</summary>
    public static string Describe<T>(Result<T> result) => result.Status switch
    {
        ResultStatus.Success => string.Empty,
        ResultStatus.ReauthenticationRequired => "Please confirm with your password and try again.",
        _ => result.Message ?? result.Status.ToString(),
    };
}
