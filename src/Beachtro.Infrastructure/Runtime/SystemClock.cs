using Beachtro.Application.Ports;

namespace Beachtro.Infrastructure.Runtime;

public sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public long UtcNowMilliseconds => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    public long MonotonicMilliseconds => Environment.TickCount64;
}
