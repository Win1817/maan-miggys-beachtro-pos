namespace Beachtro.Application.Runtime;

/// <summary>
/// Installation profile. Test and Production are separate installations with separate data
/// roots; test sale records never become production history (finance design §Money and policy).
/// </summary>
public enum RuntimeProfile
{
    Test,
    Production,
}

public static class RuntimeProfilePolicy
{
    /// <summary>
    /// POS-DEC-002 authorizes milestones 0-3 with synthetic data only. Production activation is a
    /// deliberate future readiness action (IB-32), so this build refuses to start in Production.
    /// </summary>
    public const bool ProductionEnabledInThisBuild = false;

    public static RuntimeProfile Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--profile", StringComparison.OrdinalIgnoreCase))
            {
                return Enum.TryParse<RuntimeProfile>(args[i + 1], ignoreCase: true, out var p)
                    && Enum.IsDefined(p)
                    ? p
                    : throw new ProfileNotAllowedException($"Unknown profile '{args[i + 1]}'.");
            }
        }

        return RuntimeProfile.Test;
    }

    public static void EnsureAllowed(RuntimeProfile profile)
    {
        if (profile == RuntimeProfile.Production && !ProductionEnabledInThisBuild)
        {
            throw new ProfileNotAllowedException(
                "The Production profile is not enabled in this build. Only the Test profile with synthetic data is authorized.");
        }
    }
}

public sealed class ProfileNotAllowedException(string message) : Exception(message);
