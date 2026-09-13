using System.Security.AccessControl;
using System.Security.Principal;

namespace Wolfstare.Service.Storage;

/// <summary>
/// Locks the data directory (<c>%ProgramData%\Wolfstare</c>) to SYSTEM and Administrators only,
/// so a standard (non-administrator) user cannot delete or edit the block database, the
/// integrity key, or the API token.
///
/// This is what makes the non-admin scenario airtight: with the service running as LocalSystem
/// and the everyday account a standard user, the block cannot be tampered with through the
/// filesystem — the user simply lacks the rights. The served UI still works, because the token
/// is injected into the page by the service rather than read from disk by the browser.
///
/// Applied only when running as an installed Windows service. A dev console run leaves the
/// directory alone, so it does not lock a non-elevated developer out of their own scratch data.
/// Not unit-tested — it sets a real ACL (spec §13).
/// </summary>
public static class DataDirectoryHardening
{
    public static void Harden(string directory)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = new DirectorySecurity();
        security.SetOwner(system);

        // Drop all inherited access (which would otherwise let Users read), then grant only the
        // two trusted principals.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        new DirectoryInfo(directory).SetAccessControl(security);

        // Note: this locks everything under the data directory, so no user-launched executable
        // may live here. The block stub — which IFEO launches in the standard user's context —
        // is installed to a world-runnable location (Program Files) instead, referenced by
        // Wolfstare:Enforcement:StubPath. Keep it that way, or a blocked launch fails with a raw
        // "access denied" instead of showing the block screen.
    }
}
