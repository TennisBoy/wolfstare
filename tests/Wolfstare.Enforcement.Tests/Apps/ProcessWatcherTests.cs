using Microsoft.Extensions.Logging.Abstractions;
using Wolfstare.Core.Rules;
using Wolfstare.Enforcement;
using Wolfstare.Enforcement.Apps;

namespace Wolfstare.Enforcement.Tests.Apps;

public class ProcessStartDecisionTests
{
    private static RuleSet Set(BlockRule[] rules, BlockRule[]? allow = null)
        => new(rules, allow ?? []);

    [Fact]
    public void BlockedImageNameTerminates()
    {
        var set = Set([new AppRule(new ImageNameMatcher("steam.exe"))]);

        Assert.True(ProcessStartDecision.ShouldTerminate(set, new("steam.exe", null, null)));
    }

    [Fact]
    public void BlockedPublisherOnARenamedImageTerminates()
    {
        // The rename case: the exe was renamed to dodge the IFEO key, but the signer gives it
        // away. This is the whole reason the ETW watcher exists alongside IFEO.
        var set = Set([new AppRule(new PublisherMatcher("Open Media LLC"))]);
        var renamed = new ProcessIdentity("totally-not-a-downloader.exe", "CN=Open Media LLC", null);

        Assert.True(ProcessStartDecision.ShouldTerminate(set, renamed));
    }

    [Fact]
    public void PermittedProcessIsLeftAlone()
    {
        var set = Set([new AppRule(new ImageNameMatcher("steam.exe"))]);

        Assert.False(ProcessStartDecision.ShouldTerminate(set, new("notepad.exe", null, null)));
    }

    [Fact]
    public void AllowlistedProcessIsLeftAlone()
    {
        var set = Set(
            [new AppRule(new PublisherMatcher("Valve"))],
            [new AppRule(new ImageNameMatcher("steam-cleanup.exe"))]);

        Assert.False(ProcessStartDecision.ShouldTerminate(
            set, new("steam-cleanup.exe", "CN=Valve Corp", null)));
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("lsass.exe")]
    [InlineData("csrss.exe")]
    [InlineData("wolfstare.service.exe")]
    public void CriticalProcessIsNeverTerminatedEvenIfARuleMatches(string imageName)
    {
        // Defence in depth. Even if a rule somehow matches a critical process — a broad
        // publisher rule, say — killing it could make the machine unbootable, so the guard
        // overrides the rule at the enforcement boundary.
        var set = Set([new AppRule(new PublisherMatcher("Microsoft"))]);
        var identity = new ProcessIdentity(imageName, "CN=Microsoft Corporation", null);

        Assert.False(ProcessStartDecision.ShouldTerminate(set, identity));
    }

    [Fact]
    public void EmptyRuleSetTerminatesNothing()
        => Assert.False(ProcessStartDecision.ShouldTerminate(RuleSet.Empty, new("steam.exe", null, null)));
}

public class ProcessWatcherTests
{
    private readonly RuleSetCache _cache = new();
    private readonly FakeTerminator _terminator = new();

    private ProcessWatcher NewWatcher(Dictionary<int, ProcessIdentity> table)
        => new(_cache, _terminator, pid => table.GetValueOrDefault(pid), NullLogger.Instance);

    [Fact]
    public void TerminatesAMatchingProcessAndRaisesTerminated()
    {
        _cache.Update(new RuleSet([new AppRule(new ImageNameMatcher("steam.exe"))], []));
        var watcher = NewWatcher(new() { [42] = new("steam.exe", null, null) });

        ProcessIdentity? raised = null;
        watcher.Terminated += id => raised = id;

        watcher.HandleProcessStart(42);

        Assert.Contains(42, _terminator.Killed);
        Assert.Equal("steam.exe", raised?.ImageName);
    }

    [Fact]
    public void LeavesAPermittedProcessRunning()
    {
        _cache.Update(new RuleSet([new AppRule(new ImageNameMatcher("steam.exe"))], []));
        var watcher = NewWatcher(new() { [42] = new("notepad.exe", null, null) });

        watcher.HandleProcessStart(42);

        Assert.Empty(_terminator.Killed);
    }

    [Fact]
    public void NeverTerminatesACriticalProcess()
    {
        _cache.Update(new RuleSet([new AppRule(new PublisherMatcher("Microsoft"))], []));
        var watcher = NewWatcher(new() { [4] = new("lsass.exe", "CN=Microsoft Corporation", null) });

        watcher.HandleProcessStart(4);

        Assert.Empty(_terminator.Killed);
    }

    [Fact]
    public void AnUnidentifiableProcessIsIgnored()
    {
        // The process exited before we could resolve it, or we lacked access. Do nothing rather
        // than throw on the hot path.
        _cache.Update(new RuleSet([new AppRule(new ImageNameMatcher("steam.exe"))], []));
        var watcher = NewWatcher([]);

        watcher.HandleProcessStart(999);

        Assert.Empty(_terminator.Killed);
    }

    [Fact]
    public void ATerminatorThatThrowsDoesNotBringDownTheWatcher()
    {
        _cache.Update(new RuleSet([new AppRule(new ImageNameMatcher("steam.exe"))], []));
        _terminator.Throw = true;
        var watcher = NewWatcher(new() { [42] = new("steam.exe", null, null) });

        // A process we cannot kill (access denied, already gone) must not stop us handling the next.
        var exception = Record.Exception(() => watcher.HandleProcessStart(42));

        Assert.Null(exception);
    }

    private sealed class FakeTerminator : IProcessTerminator
    {
        public List<int> Killed { get; } = [];

        public bool Throw { get; set; }

        public void Terminate(int pid)
        {
            if (Throw) throw new UnauthorizedAccessException("access denied");
            Killed.Add(pid);
        }
    }
}
