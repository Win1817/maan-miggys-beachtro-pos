namespace Beachtro.Application.Security;

/// <summary>
/// Delegation rule (security design matrix, users.manage; POS-DEC-008). Only a holder of roles.manage
/// (Owner) may touch an account holding the Owner role or grant/remove it; otherwise an actor may only
/// grant, remove or administer roles whose capabilities it holds itself. Shared by the identity service
/// and the Users screen so the screen never offers what the service refuses.
/// </summary>
public static class RoleDelegation
{
    public const string OwnerOnlyMessage = "Only an Owner can manage Owner accounts or the Owner role.";
    public const string MissingPermissionsMessage = "You cannot manage roles with permissions you do not hold yourself.";

    /// <summary>Null when allowed; otherwise the reason the actor may not grant or remove this role.</summary>
    public static string? WhyNot(UserSession actor, string roleKey)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (roleKey == RoleTemplates.Owner && !actor.Has(Capabilities.RolesManage))
        {
            return OwnerOnlyMessage;
        }

        return RoleTemplates.Grants.TryGetValue(roleKey, out var grants) && grants.Any(c => !actor.Has(c))
            ? MissingPermissionsMessage
            : null;
    }

    /// <param name="accountRoles">Roles the target account has before and after the change.</param>
    /// <param name="rolesToManage">Roles being granted/removed, or the account's roles for reset/activation.</param>
    public static string? Check(UserSession actor, IEnumerable<string> accountRoles, IEnumerable<string> rolesToManage)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var manage = rolesToManage.ToArray();
        if (accountRoles.Concat(manage).Contains(RoleTemplates.Owner, StringComparer.Ordinal) && !actor.Has(Capabilities.RolesManage))
        {
            return OwnerOnlyMessage;
        }

        return manage.Select(k => WhyNot(actor, k)).FirstOrDefault(m => m is not null);
    }
}
