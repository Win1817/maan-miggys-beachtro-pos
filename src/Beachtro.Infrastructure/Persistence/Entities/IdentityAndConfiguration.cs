using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;

namespace Beachtro.Infrastructure.Persistence.Entities;

// Persistence records only: no behavior. Column names follow the database design data dictionary.
// Suffixes: M = signed integer minor units, Q = thousandths of a unit, Ppm = parts per million,
// Utc = Unix milliseconds.

public sealed class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedCount { get; set; }
    public long? LockedUntilUtc { get; set; }
    public int SecurityVersion { get; set; } = 1;
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class Role
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class Permission
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Description { get; set; }
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}

public sealed class PolicyVersion
{
    public Guid Id { get; set; }
    public PolicyKind Kind { get; set; }
    public int Revision { get; set; }
    public long EffectiveFromUtc { get; set; }
    public long? EffectiveToUtc { get; set; }
    public ProfileApprovalState ApprovalState { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? EvidenceReference { get; set; }
    public int PayloadSchemaVersion { get; set; }
    public required string CanonicalPayloadJson { get; set; }
    public required string PayloadSha256 { get; set; }
    public long CreatedUtc { get; set; }
}

public sealed class TaxConfiguration
{
    public Guid Id { get; set; }
    public Guid PolicyVersionId { get; set; }
    public required string TaxKey { get; set; }
    public int RatePpm { get; set; }
    public PriceMode PriceMode { get; set; }
    public TreatmentCode TreatmentCode { get; set; }
    public required string StrategyKey { get; set; }
}

public sealed class BusinessProfile
{
    public Guid Id { get; set; }
    public int Revision { get; set; }
    public required string RegisteredName { get; set; }
    public required string Address { get; set; }
    public string? Contact { get; set; }
    public string? TaxIdentifier { get; set; }
    public required string RegistrationJson { get; set; }
    public string? LogoArtifactRef { get; set; }
    public int TemplateVersion { get; set; }
    public ProfileApprovalState ApprovalState { get; set; }
    public string? EvidenceReference { get; set; }
    public long CreatedUtc { get; set; }
}

public sealed class BusinessSetting
{
    public required string Key { get; set; }
    public required string ValueJson { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public long UpdatedUtc { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class InstallationState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public Guid InstallationId { get; set; }
    public int SchemaVersion { get; set; }
    public InstallationMode Mode { get; set; }
    public bool ProductionEnabled { get; set; }
    public Guid? CurrentCalendarPolicyId { get; set; }
    public Guid? CurrentCalculationPolicyId { get; set; }
    public Guid? CurrentBusinessProfileId { get; set; }
    public long LastObservedUtc { get; set; }
    public string? LastFinancialBusinessDate { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class CloudStorageSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public required string Provider { get; set; }
    public string? AccountLabel { get; set; }
    public string? RootFolderId { get; set; }
    public string? OAuthClientConfigRef { get; set; }
    public string? TokenSecretRef { get; set; }
    public bool Enabled { get; set; }
    public long? LastConnectedUtc { get; set; }
    public int Version { get; set; } = 1;
}
