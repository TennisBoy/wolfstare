using Wolfstare.Core.Sessions;
using Wolfstare.Core.Time;
using Wolfstare.Service.ServiceControl;

namespace Wolfstare.Service.Tests.ServiceControl;

/// <summary>
/// The uninstaller refuses to run while a lock is active (spec §9), so a timed block cannot be
/// escaped by uninstalling Wolfstare. It does not — and must not — block an administrator's
/// `sc delete` or safe mode; that is an accepted limit (spec §2).
/// </summary>
public class UninstallGuardTests
{
    private static readonly IClock Clock = new SystemClock();

    private static BlockSession Session(SessionLock lockSpec, long? duration)
        => new(Guid.NewGuid(), Guid.NewGuid(), lockSpec, SessionTiming.Start(Clock), duration);

    [Fact]
    public void NoActiveSessionsAllowsUninstall()
        => Assert.True(UninstallGuard.CanUninstall([], out _));

    [Fact]
    public void AnUnlockedSessionAllowsUninstall()
    {
        var allowed = UninstallGuard.CanUninstall([Session(new NoLock(), 3600)], out var reason);

        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public void ATimedLockBlocksUninstallWithAReason()
    {
        var blocked = UninstallGuard.CanUninstall([Session(new TimedLock(), 3600)], out var reason);

        Assert.False(blocked);
        Assert.NotNull(reason);
        Assert.Contains("locked", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APasswordLockBlocksUninstall()
        => Assert.False(UninstallGuard.CanUninstall(
            [Session(new PasswordLock(new Pbkdf2PasswordHasher().Create("x")), null)], out _));

    [Fact]
    public void OneLockedSessionAmongUnlockedOnesStillBlocks()
    {
        var sessions = new[]
        {
            Session(new NoLock(), 3600),
            Session(new TimedLock(), 600),
            Session(new NoLock(), 60),
        };

        Assert.False(UninstallGuard.CanUninstall(sessions, out _));
    }
}
