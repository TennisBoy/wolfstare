using System.Security.Cryptography;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Tests.Time;
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Sessions;

/// <summary>
/// The HMAC over active lock state (spec §6). It detects a hand-edit of the database — someone
/// shortening a timed lock's duration, say — and detection fails closed: a bad or missing MAC
/// is treated as tampering, never as permission.
/// </summary>
public class SessionIntegrityTests
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly FakeClock _clock = new();

    private BlockSession Session(long? duration = 3600, SessionLock? lockSpec = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), lockSpec ?? new TimedLock(), SessionTiming.Start(_clock), duration);

    [Fact]
    public void AGenuineSignatureVerifies()
    {
        var session = Session();
        var mac = SessionIntegrity.Sign(session, _key);

        Assert.True(SessionIntegrity.Verify(session, mac, _key));
    }

    [Fact]
    public void ShorteningTheDurationBreaksTheSignature()
    {
        // The attack the HMAC exists to catch: editing a timed lock's duration to end it sooner.
        var session = Session(duration: 3600);
        var mac = SessionIntegrity.Sign(session, _key);

        var shortened = session with { DurationSeconds = 1 };

        Assert.False(SessionIntegrity.Verify(shortened, mac, _key));
    }

    [Fact]
    public void ChangingTheLockKindBreaksTheSignature()
    {
        var timed = Session(lockSpec: new TimedLock());
        var mac = SessionIntegrity.Sign(timed, _key);

        var downgraded = timed with { Lock = new NoLock() };

        Assert.False(SessionIntegrity.Verify(downgraded, mac, _key));
    }

    [Fact]
    public void ChangingTheIdentityBreaksTheSignature()
    {
        var session = Session();
        var mac = SessionIntegrity.Sign(session, _key);

        Assert.False(SessionIntegrity.Verify(session with { Id = Guid.NewGuid() }, mac, _key));
        Assert.False(SessionIntegrity.Verify(session with { BlockListId = Guid.NewGuid() }, mac, _key));
    }

    [Fact]
    public void ADifferentKeyDoesNotVerify()
    {
        var session = Session();
        var mac = SessionIntegrity.Sign(session, _key);

        Assert.False(SessionIntegrity.Verify(session, mac, RandomNumberGenerator.GetBytes(32)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")] // valid base64, wrong length
    public void AGarbageMacFailsClosed(string mac)
        => Assert.False(SessionIntegrity.Verify(Session(), mac, _key));

    [Fact]
    public void ForgingElapsedTimeBreaksTheSignature()
    {
        // The other way to defeat a timed lock: expiry is elapsed >= duration, so inflating
        // elapsed in the database would make the lock look finished. Elapsed is signed too, so
        // that edit is caught. The MAC is re-signed on every checkpoint write, so signing a
        // constantly-changing field costs nothing.
        var session = Session(duration: 3600);
        var mac = SessionIntegrity.Sign(session, _key);

        var forced = session with { Timing = session.Timing with { ElapsedSeconds = 999_999 } };

        Assert.False(SessionIntegrity.Verify(forced, mac, _key));
    }

    [Fact]
    public void ForgingThePasswordHashBreaksTheSignature()
    {
        // Swapping in a password whose hash the attacker knows would let them unlock. The hash
        // is part of the signed state.
        var hasher = new Pbkdf2PasswordHasher();
        var session = Session(duration: null, lockSpec: new PasswordLock(hasher.Create("real")));
        var mac = SessionIntegrity.Sign(session, _key);

        var swapped = session with { Lock = new PasswordLock(hasher.Create("attacker-knows-this")) };

        Assert.False(SessionIntegrity.Verify(swapped, mac, _key));
    }
}
