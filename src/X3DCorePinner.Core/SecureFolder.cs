// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace X3DCorePinner.Core;

/// <summary>
/// Hardening helpers for an elevated app. Anything this app executes or hands to
/// Task Scheduler must live where only administrators can write; otherwise a
/// non-elevated program running as the same user could swap the file and have it
/// run with admin rights.
/// </summary>
public static class SecureFolder
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    // NT SERVICE\TrustedInstaller
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>%ProgramData%\X3DCorePinner - created admin-only on first use.</summary>
    public static string ProtectedRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.DataFolderName);

    /// <summary>
    /// Create (or re-lock) a directory so only Administrators and SYSTEM can write.
    /// Users keep read and execute. Inherited permissions are removed.
    /// </summary>
    public static string EnsureAdminOnly(string path)
    {
        var dir = Directory.CreateDirectory(path);
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);
        return dir.FullName;
    }

    /// <summary>
    /// True if anyone other than Administrators, SYSTEM or TrustedInstaller can modify
    /// the file or folder (for example the signed-in user, Users, Everyone).
    /// Returns true on any error (fail closed).
    /// </summary>
    public static bool IsWritableByNonAdmins(string path)
    {
        try
        {
            AuthorizationRuleCollection rules;
            if (Directory.Exists(path))
                rules = new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            else if (File.Exists(path))
                rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            else
                return true;

            foreach (FileSystemAccessRule r in rules)
            {
                if (r.AccessControlType != AccessControlType.Allow) continue;
                if ((r.FileSystemRights & WriteRights) == 0) continue;
                var sid = (SecurityIdentifier)r.IdentityReference;
                if (sid.Equals(Admins) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller)) continue;
                // CREATOR OWNER only applies to children created later - by admins in a protected folder.
                if (sid.Equals(CreatorOwner) && r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True if the running exe and its folder are protected from non-admin changes.</summary>
    public static bool AppLocationIsProtected(out string reason)
    {
        string dir = Path.GetDirectoryName(AppInfo.ExePath) ?? AppInfo.ExeDir;
        if (IsWritableByNonAdmins(dir))
        {
            reason = $"The folder \"{dir}\" can be modified without administrator rights.";
            return false;
        }
        if (IsWritableByNonAdmins(AppInfo.ExePath))
        {
            reason = $"\"{AppInfo.ExePath}\" can be modified without administrator rights.";
            return false;
        }
        reason = "";
        return true;
    }

    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
