using Beachtro.Application.Ports;

namespace Beachtro.Infrastructure.Tests.Runtime;

public sealed class FixedTestClock : IClock
{
    public long UtcNowMilliseconds => 1_763_300_000_000;

    public long MonotonicMilliseconds => 0;
}
