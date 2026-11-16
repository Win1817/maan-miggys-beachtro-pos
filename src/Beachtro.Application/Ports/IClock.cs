namespace Beachtro.Application.Ports;

/// <summary>Time source. All persisted instants are UTC Unix milliseconds (database design §Physical conventions).</summary>
public interface IClock
{
    long UtcNowMilliseconds { get; }
}
