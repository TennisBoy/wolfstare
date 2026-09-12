using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement.Apps;

/// <summary>
/// The single decision the process watcher makes: should this newly started process be killed?
///
/// It is a static function of the rule set and the process identity so it can be tested
/// exhaustively with synthetic identities. The watcher's event handler does nothing but call
/// this — the logic under test is the logic that runs.
/// </summary>
public static class ProcessStartDecision
{
    public static bool ShouldTerminate(RuleSet rules, ProcessIdentity identity)
    {
        // The critical-process guard overrides the rules unconditionally. Even a rule that
        // legitimately matches — a broad publisher rule catching a signed system process — must
        // not kill something the machine needs to run (spec §8.3). This repeats the
        // creation-time guard on purpose: the boundary must be safe even if a bad rule reaches it.
        if (CriticalProcesses.IsProtected(identity.ImageName))
            return false;

        return rules.EvaluateApp(identity) == Decision.Deny;
    }
}
