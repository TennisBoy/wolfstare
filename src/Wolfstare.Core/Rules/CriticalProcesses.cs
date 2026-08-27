namespace Wolfstare.Core.Rules;

/// <summary>
/// Executables that may never be blocked, terminated, or IFEO-redirected.
///
/// An IFEO key on <c>explorer.exe</c> leaves no desktop shell; on <c>lsass.exe</c> or
/// <c>csrss.exe</c> it prevents boot. Because the uninstaller refuses to run during an active
/// lock, recovery from such a rule would mean safe mode. The guard therefore lives here, in
/// the pure domain, where it is unit-tested and rejects the rule at creation time rather than
/// at apply time (spec §8.3).
///
/// The administration entries are deliberate: the user must retain the ability to repair
/// their own machine. A blocker that can lock you out of Task Manager is a blocker that can
/// strand you.
/// </summary>
public static class CriticalProcesses
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Boot and session critical
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe",
        "ctfmon.exe", "spoolsv.exe", "audiodg.exe",

        // Shell — blocking these leaves no usable desktop
        "explorer.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "searchhost.exe", "runtimebroker.exe", "dllhost.exe", "taskhostw.exe",
        "applicationframehost.exe", "textinputhost.exe",

        // Administration — the user must be able to fix their machine
        "taskmgr.exe", "regedit.exe", "mmc.exe", "cmd.exe", "powershell.exe",
        "pwsh.exe", "conhost.exe", "windowsterminal.exe", "systemsettings.exe",
        "control.exe", "msconfig.exe", "sc.exe", "net.exe", "netsh.exe",

        // Wolfstare's own executables
        "wolfstare.service.exe", "wolfstare.blockstub.exe", "wolfstare.cli.exe",
    };

    /// <summary>
    /// True when the name refers to a protected executable. Accepts a bare name, a name
    /// without its extension, or a full path — a rule author must not be able to slip one
    /// past the guard merely by writing it differently.
    /// </summary>
    public static bool IsProtected(string imageName)
        => !string.IsNullOrWhiteSpace(imageName)
           && Names.Contains(ImageNameMatcher.Canonical(imageName));
}
