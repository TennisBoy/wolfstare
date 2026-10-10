using Wolfstare.Core.Sessions;
using Wolfstare.Core.Tests.Time;
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Sessions;

/// <summary>
/// The random-text lock ("type this to unlock"). The required text is not a
/// secret — it is shown to the user — so this is friction, not cryptography. Stopping requires
/// retyping it exactly.
/// </summary>
public class RandomTextLockTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly FakeClock _clock = new();

    private BlockSession Session(string text, long? duration = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), new RandomTextLock(text), SessionTiming.Start(_clock), duration);

    [Fact]
    public void GeneratesTextUpToButNotOverTheRequestedLength()
    {
        // Words rarely land exactly on a target, so the result is at most the target and within
        // one word of it.
        var text = RandomText.Generate(5000);

        Assert.True(text.Length <= 5000);
        Assert.True(text.Length >= 5000 - 12, $"expected close to 5000, got {text.Length}");
    }

    [Fact]
    public void GeneratedTextIsLowercaseWordsSeparatedBySpaces()
    {
        var text = RandomText.Generate(500);

        Assert.All(text, c => Assert.True(c is (>= 'a' and <= 'z') or ' '));
        Assert.Contains(' ', text);                       // more than one word
        Assert.DoesNotContain("  ", text);                // single spaces, no doubles
        Assert.False(text.StartsWith(' ') || text.EndsWith(' '));
    }

    [Fact]
    public void TwoGenerationsDiffer()
        => Assert.NotEqual(RandomText.Generate(200), RandomText.Generate(200));

    [Fact]
    public void CorrectTextUnlocks()
    {
        var text = RandomText.Generate(64);
        var session = Session(text);

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, text, _hasher, _clock));
    }

    [Fact]
    public void WrongTextIsRejected()
    {
        var text = RandomText.Generate(64);
        var session = Session(text);

        Assert.Equal(StopOutcome.PasswordIncorrect, StopPolicy.CanStop(session, text + "x", _hasher, _clock));
        Assert.Equal(StopOutcome.PasswordIncorrect, StopPolicy.CanStop(session, text.ToUpperInvariant(), _hasher, _clock));
    }

    [Fact]
    public void NoTextSuppliedAsksForIt()
    {
        var session = Session(RandomText.Generate(64));

        Assert.Equal(StopOutcome.PasswordRequired, StopPolicy.CanStop(session, null, _hasher, _clock));
    }

    [Fact]
    public void AnIndefiniteRandomTextLockNeverExpiresOnItsOwn()
    {
        var text = RandomText.Generate(64);
        var session = Session(text, duration: null);

        _clock.Advance(TimeSpan.FromDays(3650));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, _clock) };

        Assert.False(session.IsExpired());
        // A decade later, still nothing but the exact text ends it.
        Assert.Equal(StopOutcome.PasswordRequired, StopPolicy.CanStop(session, null, _hasher, _clock));
        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, text, _hasher, _clock));
    }
}
