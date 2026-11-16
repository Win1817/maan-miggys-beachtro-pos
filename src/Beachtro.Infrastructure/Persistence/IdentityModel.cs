using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using static Beachtro.Infrastructure.Persistence.ModelBuilderHelpers;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>Identity and configuration tables (database design §Identity and configuration).</summary>
internal static class IdentityModel
{
    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<User>(b =>
        {
            b.ToTable("Users", t => t
                .Check("Counters", """ "FailedCount" >= 0 AND "SecurityVersion" >= 1 AND "Version" >= 1 """)
                .Check("Username", NotBlank("Username") + " AND " + NotBlank("NormalizedUsername"))
                .Check("PasswordHash", NotBlank("PasswordHash")));
            b.HasIndex(x => x.NormalizedUsername).IsUnique();
        });

        mb.Entity<Role>(b =>
        {
            b.ToTable("Roles", t => t.Check("Key", NotBlank("Key")));
            b.HasIndex(x => x.Key).IsUnique();
        });

        mb.Entity<Permission>(b =>
        {
            b.ToTable("Permissions", t => t.Check("Key", NotBlank("Key")));
            b.HasIndex(x => x.Key).IsUnique();
        });

        mb.Entity<UserRole>(b =>
        {
            b.ToTable("UserRoles");
            b.HasKey(x => new { x.UserId, x.RoleId });
            b.Fk<UserRole, User>(x => x.UserId).Fk<UserRole, Role>(x => x.RoleId);
            b.HasIndex(x => x.RoleId);
        });

        mb.Entity<RolePermission>(b =>
        {
            b.ToTable("RolePermissions");
            b.HasKey(x => new { x.RoleId, x.PermissionId });
            b.Fk<RolePermission, Role>(x => x.RoleId).Fk<RolePermission, Permission>(x => x.PermissionId);
            b.HasIndex(x => x.PermissionId);
        });

        mb.Entity<PolicyVersion>(b =>
        {
            b.ToTable("PolicyVersions", t => t
                .Check("Revision", """ "Revision" >= 1 AND "PayloadSchemaVersion" >= 1 """)
                .Check("Interval", """ "EffectiveToUtc" IS NULL OR "EffectiveToUtc" > "EffectiveFromUtc" """)
                .Check("ApprovedBy", """ "ApprovalState" <> 'Approved' OR "ApprovedByUserId" IS NOT NULL """)
                .Check("PayloadSha256", Sha256("PayloadSha256")));
            b.HasIndex(x => new { x.Kind, x.Revision }).IsUnique();
            b.Fk<PolicyVersion, User>(x => x.ApprovedByUserId);
        });

        mb.Entity<TaxConfiguration>(b =>
        {
            b.ToTable("TaxConfigurations", t => t
                .Check("Rate", """ "RatePpm" BETWEEN 0 AND 1000000 """)
                .Check("TaxKey", NotBlank("TaxKey")));
            b.HasIndex(x => new { x.PolicyVersionId, x.TaxKey }).IsUnique();
            b.Fk<TaxConfiguration, PolicyVersion>(x => x.PolicyVersionId);
        });

        mb.Entity<BusinessProfile>(b =>
        {
            b.ToTable("BusinessProfiles", t => t
                .Check("Revision", """ "Revision" >= 1 AND "TemplateVersion" >= 1 """)
                .Check("ApprovedIdentity", """ "ApprovalState" <> 'Approved' OR (length(trim("RegisteredName")) > 0 AND length(trim("Address")) > 0) """));
            b.HasIndex(x => x.Revision).IsUnique();
        });

        mb.Entity<BusinessSetting>(b =>
        {
            b.ToTable("BusinessSettings", t => t.Check("Key", NotBlank("Key")).Check("Version", """ "Version" >= 1 """));
            b.HasKey(x => x.Key);
            b.Fk<BusinessSetting, User>(x => x.UpdatedByUserId);
        });

        mb.Entity<InstallationState>(b =>
        {
            b.ToTable("InstallationState", t => t
                .Check("Singleton", """ "Id" = 1 """)
                .Check("BusinessDate", """ "LastFinancialBusinessDate" IS NULL OR (""" + IsoDate("LastFinancialBusinessDate") + ")")
                .Check("Version", """ "Version" >= 1 AND "SchemaVersion" >= 1 """));
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Fk<InstallationState, PolicyVersion>(x => x.CurrentCalendarPolicyId);
            b.Fk<InstallationState, PolicyVersion>(x => x.CurrentCalculationPolicyId);
            b.Fk<InstallationState, BusinessProfile>(x => x.CurrentBusinessProfileId);
        });

        mb.Entity<CloudStorageSettings>(b =>
        {
            b.ToTable("CloudStorageSettings", t => t.Check("Singleton", """ "Id" = 1 """));
            b.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
