using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ResetMe.Platform;

/// <summary>Restricts config/state files to the current OS user (PRD §28).</summary>
public static class FilePermissions
{
    public static void RestrictToCurrentUser(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path, isDirectory);
        }
        else
        {
            File.SetUnixFileMode(
                path,
                isDirectory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Reports whether anyone other than the owner can access <paramref name="path"/>.</summary>
    public static bool IsRestricted(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsRestrictedWindows(path, isDirectory);
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode others =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & others) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path, bool isDirectory)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve the current Windows user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRestrictedWindows(string path, bool isDirectory)
    {
        var user = WindowsIdentity.GetCurrent().User;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid != user && sid != system && sid != admins)
            {
                return false;
            }
        }

        return true;
    }
}
