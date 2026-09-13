using Wolfstare.Core.Enforcement;
using Wolfstare.Core.Rules;
using Wolfstare.Enforcement.Machine;
using Wolfstare.Service.Enforcement;

namespace Wolfstare.Service.Tests.Enforcement;

/// <summary>
/// The IFEO-engagement decision, tested with a recording mutator: which rules become IFEO keys
/// and which are left to the ETW watcher.
/// </summary>
public sealed class AppEnforcementTests
{
    private const string Stub = @"C:\Program Files\Wolfstare\Wolfstare.BlockStub.exe";
    private readonly RecordingMutator _mutator = new();

    // The enumerator of existing redirects is faked so tests never touch the real registry.
    private WindowsAppEnforcement NewEnforcement(params string[] existingRedirects)
        => new(_mutator, Stub,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsAppEnforcement>.Instance,
            () => existingRedirects);

    private static RuleSet Set(params BlockRule[] rules) => new(rules, []);

    [Fact]
    public async Task ImageNameRuleInstallsOneIfeoSetting()
    {
        await NewEnforcement().ApplyAsync(Set(new AppRule(new ImageNameMatcher("steam.exe"))), default);

        var applied = Assert.Single(_mutator.Applied);
        Assert.Equal("ifeo:steam.exe", applied.Setting.Key);
        Assert.Equal(@"C:\Program Files\Wolfstare\Wolfstare.BlockStub.exe", applied.Desired);
    }

    [Fact]
    public async Task NotYetInstalledAppStillGetsItsIfeoKey()
    {
        // The headline case: a rule for an app that is not on the machine yet. IFEO is keyed on
        // the name, so the key can exist before the exe does.
        await NewEnforcement().ApplyAsync(
            Set(new AppRule(new ImageNameMatcher("4kvideodownloaderplus.exe"))), default);

        Assert.Equal("ifeo:4kvideodownloaderplus.exe", Assert.Single(_mutator.Applied).Setting.Key);
    }

    [Fact]
    public async Task PublisherRuleInstallsNoIfeoSetting()
    {
        // A publisher matcher has no filename to key IFEO on; it is enforced by the ETW watcher
        // only. Applying it must not touch the registry.
        await NewEnforcement().ApplyAsync(Set(new AppRule(new PublisherMatcher("Open Media"))), default);

        Assert.Empty(_mutator.Applied);
    }

    [Fact]
    public async Task DomainRuleInstallsNoIfeoSetting()
    {
        await NewEnforcement().ApplyAsync(Set(new DomainRule("reddit.com")), default);

        Assert.Empty(_mutator.Applied);
    }

    [Fact]
    public async Task CriticalProcessImageNameIsSkipped()
    {
        // The guard also lives in the IFEO setting's constructor, so app enforcement filters
        // protected names first rather than letting construction throw mid-apply.
        await NewEnforcement().ApplyAsync(
            Set(
                new AppRule(new ImageNameMatcher("explorer.exe")),
                new AppRule(new ImageNameMatcher("steam.exe"))),
            default);

        Assert.Equal("ifeo:steam.exe", Assert.Single(_mutator.Applied).Setting.Key);
    }

    [Fact]
    public async Task AllowlistedImageNameIsNotIfeoBlocked()
    {
        var rules = new RuleSet(
            [new AppRule(new ImageNameMatcher("steam.exe"))],
            [new AppRule(new ImageNameMatcher("steam.exe"))]);

        await NewEnforcement().ApplyAsync(rules, default);

        Assert.Empty(_mutator.Applied);
    }

    [Fact]
    public async Task ReconcileRemovesAnOrphanedRedirectNoBlockCallsFor()
    {
        // The bug this fixes: a redirect for steam.exe exists, but no active rule wants it.
        // ApplyAsync must remove it (write the setting with a null value) so the app isn't left
        // blocked with no session to unlock.
        await NewEnforcement("steam.exe")
            .ApplyAsync(Set(new DomainRule("reddit.com")), default);

        var removed = Assert.Single(_mutator.Applied);
        Assert.Equal("ifeo:steam.exe", removed.Setting.Key);
        Assert.Null(removed.Desired);                    // null = remove the redirect
    }

    [Fact]
    public async Task ReconcileKeepsARedirectStillWanted()
    {
        // steam.exe exists AND is still blocked: it should be re-applied (kept), not removed.
        await NewEnforcement("steam.exe")
            .ApplyAsync(Set(new AppRule(new ImageNameMatcher("steam.exe"))), default);

        var applied = Assert.Single(_mutator.Applied);
        Assert.Equal("ifeo:steam.exe", applied.Setting.Key);
        Assert.Equal(Stub, applied.Desired);             // re-applied, not removed
    }

    [Fact]
    public async Task RestoreRemovesEveryOwnedRedirect()
    {
        await NewEnforcement("steam.exe", "discord.exe").RestoreAsync(default);

        Assert.Equal(2, _mutator.Applied.Count);
        Assert.All(_mutator.Applied, a => Assert.Null(a.Desired));   // all removed
    }

    private sealed class RecordingMutator : ISystemMutator
    {
        public List<(ISystemSetting Setting, string? Desired)> Applied { get; } = [];

        public bool Restored { get; private set; }

        public Task ApplyAsync(ISystemSetting setting, string? desired, CancellationToken ct)
        {
            Applied.Add((setting, desired));
            return Task.CompletedTask;
        }

        public Task<RestoreReport> RestoreAllAsync(CancellationToken ct)
        {
            Restored = true;
            return Task.FromResult(new RestoreReport([], [], []));
        }

        public Task<RestoreReport> RestoreMatchingAsync(Func<string, bool> keyFilter, CancellationToken ct)
        {
            Restored = true;
            return Task.FromResult(new RestoreReport([], [], []));
        }
    }
}
