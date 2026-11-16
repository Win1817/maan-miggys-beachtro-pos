using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Beachtro.Infrastructure.Security;

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>
/// Maintained ASP.NET Core Identity PasswordHasher used as a library (security design
/// §Authentication): versioned V3 format, PBKDF2-HMAC-SHA512, unique salt, fixed-time compare.
/// No custom hash construction.
/// </summary>
public sealed class PosPasswordHasher
{
    /// <summary>Minimum work factor selected by the security design; re-benchmark on target hardware.</summary>
    public const int DefaultIterations = 210_000;

    private static readonly LocalCredential Subject = new();
    private readonly PasswordHasher<LocalCredential> _hasher;

    public PosPasswordHasher()
        : this(DefaultIterations)
    {
    }

    public PosPasswordHasher(int iterations)
    {
        _hasher = new PasswordHasher<LocalCredential>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterations,
        }));
        Iterations = iterations;
        DummyHash = _hasher.HashPassword(Subject, "dummy-password-for-timing-equalization");
    }

    public int Iterations { get; }

    /// <summary>Verified against when the account is unknown, so response time does not reveal it.</summary>
    internal string DummyHash { get; }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return _hasher.HashPassword(Subject, password);
    }

    public PasswordCheck Verify(string hash, string password)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(password);
        try
        {
            return _hasher.VerifyHashedPassword(Subject, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
                _ => PasswordCheck.Failed,
            };
        }
        catch (FormatException)
        {
            return PasswordCheck.Failed;
        }
    }

    /// <summary>Reads the iteration count from a V3 hash header (for tests and diagnostics).</summary>
    public static int ReadIterationCount(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        if (bytes.Length < 13 || bytes[0] != 0x01)
        {
            throw new FormatException("Not an Identity V3 password hash.");
        }

        return (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8];
    }

    /// <summary>Marker type required by the generic hasher; carries no data.</summary>
    public sealed class LocalCredential;
}
