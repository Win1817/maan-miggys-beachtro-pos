namespace Beachtro.Application.Security;

/// <summary>
/// Password rules (security design §Authentication; B-03 provisional): 15–128 characters,
/// spaces and Unicode allowed, never trimmed, case-folded or silently truncated; no
/// character-class rules or expiry.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 15;
    public const int MaximumLength = 128;

    /// <summary>Returns a safe validation message, or null if acceptable. Never echoes the password.</summary>
    public static string? Validate(string? password, string? username = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "A password is required.";
        }

        // Count Unicode scalar values, so emoji/accents count as one character each.
        var length = password.EnumerateRunes().Count();
        if (length > MaximumLength)
        {
            return $"Passwords may be at most {MaximumLength} characters.";
        }

        if (length < MinimumLength)
        {
            return $"Passwords must be at least {MinimumLength} characters.";
        }

        if (password.EnumerateRunes().Distinct().Count() == 1)
        {
            return "Passwords cannot be one repeated character.";
        }

        if (!string.IsNullOrWhiteSpace(username) && password.Contains(username.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "Passwords cannot contain the username.";
        }

        return null;
    }
}

/// <summary>Username normalization: trim + invariant upper-case folding; one documented algorithm.</summary>
public static class UsernameRules
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 64;

    public static string Normalize(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        return username.Trim().ToUpperInvariant();
    }

    public static string? Validate(string? username)
    {
        var trimmed = username?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "A username is required.";
        }

        if (trimmed.Length is < MinimumLength or > MaximumLength)
        {
            return $"Usernames must be {MinimumLength}–{MaximumLength} characters.";
        }

        if (trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return "Usernames cannot contain spaces or control characters.";
        }

        return null;
    }
}

/// <summary>Bounded free-text fields (database design: name 150, reason 500).</summary>
public static class TextRules
{
    public const int NameMaximum = 150;
    public const int ReasonMaximum = 500;

    public static string? ValidateRequired(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{field} is required.";
        }

        if (value.Length > maximum)
        {
            return $"{field} may be at most {maximum} characters.";
        }

        return value.Any(c => char.IsControl(c) && c is not '\n' and not '\r')
            ? $"{field} cannot contain control characters."
            : null;
    }
}
