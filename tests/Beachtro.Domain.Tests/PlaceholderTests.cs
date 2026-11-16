namespace Beachtro.Domain.Tests;

public sealed class DomainAssemblyTests
{
    [Fact]
    public void Domain_assembly_loads()
    {
        Assert.Equal("Beachtro.Domain", typeof(DomainAssemblyMarker).Assembly.GetName().Name);
    }
}
