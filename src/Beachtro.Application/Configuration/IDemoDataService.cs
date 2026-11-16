using Beachtro.Application.Common;
using Beachtro.Application.Security;

namespace Beachtro.Application.Configuration;

public sealed record DemoDataResult(int ProfilesCreated, int ProductsCreated);

/// <summary>
/// Test profile only (POS-DEC-005): loads synthetic Test profiles, a TEST business identity and sample
/// products so the screens can be exercised before the settings screens exist. Refused in Production.
/// </summary>
public interface IDemoDataService
{
    Task<Result<DemoDataResult>> LoadSampleDataAsync(UserSession session, CancellationToken cancellationToken = default);
}
