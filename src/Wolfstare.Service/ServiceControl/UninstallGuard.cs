using Wolfstare.Core.Sessions;

namespace Wolfstare.Service.ServiceControl;

/// <summary>
/// Decides whether the uninstaller may run (spec §9). It refuses while any session is locked, so
/// a timed block cannot be escaped by uninstalling Wolfstare.
///
/// It deliberately does not — and cannot — stop an administrator's `sc delete`, an offline
/// registry edit, or safe mode. Raising the cost of a bypass above the cost of the impulse is
/// the goal; defeating a determined administrator is a non-goal (spec §2).
/// </summary>
public static class UninstallGuard
{
    public static bool CanUninstall(IReadOnlyCollection<BlockSession> activeSessions, out string? reason)
    {
        var locked = activeSessions.Count(s => s.Lock is not NoLock);
        if (locked == 0)
        {
            reason = null;
            return true;
        }

        reason =
            $"{locked} block session(s) are locked and still active. Wolfstare cannot be uninstalled "
            + "until they end. This is the point of a lock — wait for the timer, or unlock a "
            + "password-protected block first.";
        return false;
    }
}
