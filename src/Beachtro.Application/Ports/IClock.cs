namespace Beachtro.Application.Ports;

/// <summary>Time sources. Persisted instants are UTC Unix milliseconds (database design §Physical conventions).</summary>
public interface IClock
{
    /// <summary>Wall-clock UTC Unix milliseconds; can jump if the Windows clock is changed.</summary>
    long UtcNowMilliseconds { get; }

    /// <summary>Monotonic milliseconds for durations (inactivity, reauthentication age); never persisted.</summary>
    long MonotonicMilliseconds { get; }
}
