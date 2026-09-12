using Wolfstare.Service.ServiceControl;

namespace Wolfstare.Service.Tests.ServiceControl;

/// <summary>
/// While a lock is active, the interactive user must not be able to stop the service (spec §9).
/// That is a change to the service's security descriptor; the transform is pure string work and
/// tested here, while applying it via `sc sdset` is scripted and checklisted.
/// </summary>
public class ServiceSddlTests
{
    // A representative default service SDDL: interactive users (IU) have generic-execute rights,
    // which include stopping (WP = SERVICE_STOP within the service-specific bits).
    private const string DefaultSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    [Fact]
    public void DenyRemovesStopRightsFromInteractiveAndSystemUsers()
    {
        var locked = ServiceSddl.DenyStop(DefaultSddl);

        // A deny ACE for the stop right (WP) targeting interactive users must be present.
        Assert.Contains("(D;;WP;;;IU)", locked);
        Assert.Contains("(D;;WP;;;SU)", locked);
    }

    [Fact]
    public void DenyIsIdempotent()
    {
        var once = ServiceSddl.DenyStop(DefaultSddl);
        var twice = ServiceSddl.DenyStop(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void AllowRemovesTheDenyAcesAgain()
    {
        var locked = ServiceSddl.DenyStop(DefaultSddl);

        var unlocked = ServiceSddl.AllowStop(locked);

        Assert.DoesNotContain("(D;;WP;;;IU)", unlocked);
        Assert.DoesNotContain("(D;;WP;;;SU)", unlocked);
    }

    [Fact]
    public void DenyThenAllowReturnsToTheOriginal()
        => Assert.Equal(DefaultSddl, ServiceSddl.AllowStop(ServiceSddl.DenyStop(DefaultSddl)));

    [Fact]
    public void DenyAcesPrecedeAllowAcesSoTheyTakeEffect()
    {
        // In a DACL, a deny ACE only wins if it appears before any allow ACE granting the same
        // right. The transform must insert the denies at the front of the DACL.
        var locked = ServiceSddl.DenyStop(DefaultSddl);

        var firstDeny = locked.IndexOf("(D;;WP;;;IU)", StringComparison.Ordinal);
        var firstAllow = locked.IndexOf("(A;;", StringComparison.Ordinal);

        Assert.True(firstDeny < firstAllow, "deny ACEs must come before allow ACEs in the DACL");
    }
}
