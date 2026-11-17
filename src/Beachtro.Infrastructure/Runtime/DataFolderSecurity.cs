using System.Security.AccessControl;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Beachtro.Infrastructure.Runtime;

public sealed record FolderProtection(bool IsPrivate, IReadOnlyList<string> Problems);

/// <summary>
/// Keeps the data folder private (security design §Secret, file and cloud protection; POS-DEC-011): only the
/// Windows account running the POS, SYSTEM and Administrators may access it, and nothing is inherited from
/// a parent folder that might be shared. Applied at every start; a folder that cannot be made private is
/// reported, not silently accepted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataFolderSecurity
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static FolderProtection Protect(string root)
    {
        var problems = new List<string>();
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in Allowed())
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(root).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            problems.Add("The data folder's permissions could not be set: " + ex.Message);
        }

        var check = Check(root);
        return new FolderProtection(problems.Count == 0 && check.IsPrivate, [.. problems, .. check.Problems]);
    }

    public static FolderProtection Check(string root)
    {
        var problems = new List<string>();
        var allowed = Allowed().ToHashSet();
        var rules = new DirectoryInfo(root).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && !allowed.Contains(sid))
            {
                var name = Translate(sid);
                problems.Add((rule.FileSystemRights & WriteRights) != 0
                    ? $"{name} can change files in the data folder."
                    : $"{name} can read the data folder.");
            }
        }

        return new FolderProtection(problems.Count == 0, problems);
    }

    private static IEnumerable<SecurityIdentifier> Allowed()
    {
        if (WindowsIdentity.GetCurrent().User is { } me)
        {
            yield return me;
        }

        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    }

    private static string Translate(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
