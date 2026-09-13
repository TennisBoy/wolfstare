using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Wolfstare.Core.Enforcement;
using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement.Machine;

/// <summary>The two registry paths an IFEO key occupies: the native view and the 32-bit view.</summary>
public readonly record struct IfeoPaths(string Native, string Wow64)
{
    private const string NativeBase =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Wow64Base =
        @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>Builds both paths for an image, canonicalising the name to a bare lowercase exe.</summary>
    public static IfeoPaths For(string imageName)
    {
        var canonical = ImageNameMatcher.Canonical(imageName);
        return new IfeoPaths($@"{NativeBase}\{canonical}", $@"{Wow64Base}\{canonical}");
    }
}

/// <summary>
/// An Image File Execution Options "Debugger" redirection for one executable (spec §8.1).
/// Setting it makes Windows launch the block stub in place of the target — at launch, matched
/// purely by filename, which is why a rule can be written for an application that is not
/// installed yet.
///
/// The constructor refuses a critical process. This is the guard at the enforcement boundary,
/// duplicating <see cref="RuleValidator"/>'s creation-time check on purpose: an IFEO key on
/// explorer.exe leaves no shell, so the value must be impossible to construct, not merely
/// rejected earlier. Written to both registry views so a 32-bit target is also caught.
///
/// Not unit-tested — it writes HKLM (spec §13). Its path builder and guard are pure and tested.
/// </summary>
public sealed class ImageFileExecutionOptionsSetting : ISystemSetting
{
    private const string DebuggerValue = "Debugger";

    private readonly string _imageName;
    private readonly string _stubPath;
    private readonly IfeoPaths _paths;

    public ImageFileExecutionOptionsSetting(string imageName, string stubPath)
    {
        if (CriticalProcesses.IsProtected(imageName))
            throw new ArgumentException(
                $"'{imageName}' is a critical Windows process; an IFEO redirection would make the machine unusable.",
                nameof(imageName));

        _imageName = ImageNameMatcher.Canonical(imageName);
        _stubPath = stubPath;
        _paths = IfeoPaths.For(imageName);
    }

    public string Key => $"ifeo:{_imageName}";

    /// <summary>
    /// Every image name whose IFEO Debugger currently points at <paramref name="stubPath"/> —
    /// i.e. every redirect Wolfstare owns. The reconcile loop uses this to remove redirects that
    /// no active block calls for, so an orphaned key can't strand an app as blocked with no
    /// session to unlock. Reads both registry views.
    /// </summary>
    public static IReadOnlyList<string> RedirectedTo(string stubPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var basePath in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
                 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var parent = root.OpenSubKey(basePath, writable: false);
            if (parent is null) continue;

            foreach (var name in parent.GetSubKeyNames())
            {
                try
                {
                    using var sub = parent.OpenSubKey(name, writable: false);
                    if (sub?.GetValue(DebuggerValue) is string dbg
                        && string.Equals(dbg, stubPath, StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // A key we can't read isn't one we can act on; skip it.
                }
            }
        }

        return names.ToList();
    }

    public Task<string?> ReadAsync(CancellationToken ct)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(_paths.Native, writable: false);
        return Task.FromResult((string?)key?.GetValue(DebuggerValue));
    }

    public Task WriteAsync(string? value, CancellationToken ct)
    {
        // value is the desired Debugger path (the stub) when engaging, or null to remove it.
        // The desired value at apply time is always the stub path; null only ever comes from a
        // restore back to "no redirection", which is the original state for every rule we add.
        foreach (var path in new[] { _paths.Native, _paths.Wow64 })
            ApplyToView(path, value, ct);

        return Task.CompletedTask;
    }

    private void ApplyToView(string path, string? value, CancellationToken ct)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        if (value is null)
        {
            using var key = root.OpenSubKey(path, writable: true);
            if (key is null) return;

            key.DeleteValue(DebuggerValue, throwOnMissingValue: false);

            // Remove the key entirely if we left it empty, so a restored machine has no trace.
            if (key.ValueCount == 0 && key.SubKeyCount == 0)
            {
                key.Dispose();
                DeleteEmptyKey(root, path);
            }

            return;
        }

        using var target = root.CreateSubKey(path, writable: true);
        target.SetValue(DebuggerValue, _stubPath, RegistryValueKind.String);
        Harden(target);
    }

    /// <summary>
    /// Makes the key hard to delete casually: owned by SYSTEM, with Administrators denied the
    /// Delete right, so removing it in regedit requires taking ownership first (spec §9,
    /// raising the cost of a bypass without pretending to be unbreakable).
    ///
    /// Only applied when the service is actually running as LocalSystem — the intended
    /// installed state. Under a merely-elevated Administrator (a dev run), SYSTEM ownership and
    /// an Administrators deny would lock the service out of its own restore, so it is skipped.
    /// SYSTEM keeps full control, so the service can always remove the key on restore.
    /// </summary>
    private static void Harden(RegistryKey key)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) return;

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = new RegistrySecurity();
        security.SetOwner(system);
        security.AddAccessRule(new RegistryAccessRule(
            system, RegistryRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            administrators, RegistryRights.Delete, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));

        key.SetAccessControl(security);
    }

    private static void DeleteEmptyKey(RegistryKey root, string path)
    {
        var lastSlash = path.LastIndexOf('\\');
        var parentPath = path[..lastSlash];
        var leaf = path[(lastSlash + 1)..];

        using var parent = root.OpenSubKey(parentPath, writable: true);
        parent?.DeleteSubKey(leaf, throwOnMissingSubKey: false);
    }
}
