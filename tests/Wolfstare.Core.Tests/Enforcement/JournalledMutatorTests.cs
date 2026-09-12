using System.Collections.Concurrent;
using Wolfstare.Core.Enforcement;
using Wolfstare.Core.Storage;

namespace Wolfstare.Core.Tests.Enforcement;

/// <summary>
/// The journal is what stands between a crash mid-change and a machine left with dead DNS and
/// no record of what it used to be. Every case here is a way that could go wrong.
/// </summary>
public class JournalledMutatorTests
{
    private readonly InMemoryMutationJournal _journal = new();
    private readonly Dictionary<string, FakeSetting> _settings = new();

    private FakeSetting Setting(string key, string? initial)
    {
        var setting = new FakeSetting(key, initial);
        _settings[key] = setting;
        return setting;
    }

    private JournalledMutator Mutator() => new(_journal, key => _settings.GetValueOrDefault(key));

    [Fact]
    public async Task ApplyWritesTheDesiredValue()
    {
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");

        await Mutator().ApplyAsync(dns, "127.0.0.1", CancellationToken.None);

        Assert.Equal("127.0.0.1", dns.Value);
    }

    [Fact]
    public async Task OriginalIsJournalledBeforeTheWrite()
    {
        // If the write throws, the journal must already know what to restore.
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        dns.ThrowOnWrite = true;

        await Assert.ThrowsAsync<IOException>(
            () => Mutator().ApplyAsync(dns, "127.0.0.1", CancellationToken.None));

        var entry = await _journal.GetAsync(dns.Key);
        Assert.NotNull(entry);
        Assert.Equal("192.168.1.1", entry.Original);
    }

    [Fact]
    public async Task SecondApplyKeepsTheFirstOriginal()
    {
        // Journalling the second time would record our own value as "original" and make the
        // real one unrecoverable.
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        var mutator = Mutator();

        await mutator.ApplyAsync(dns, "127.0.0.1", CancellationToken.None);
        await mutator.ApplyAsync(dns, "::1", CancellationToken.None);

        Assert.Equal("192.168.1.1", (await _journal.GetAsync(dns.Key))!.Original);
    }

    [Fact]
    public async Task ApplyingTheCurrentValueDoesNotWrite()
    {
        // The enforcer re-applies every refresh. Unchanged settings must not shell out to netsh
        // every two seconds.
        var dns = Setting("dns:ipv4:Wi-Fi", "127.0.0.1");

        await Mutator().ApplyAsync(dns, "127.0.0.1", CancellationToken.None);

        Assert.Equal(0, dns.WriteCount);
    }

    [Fact]
    public async Task RestoreWritesOriginalsBackAndClearsTheJournal()
    {
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        var policy = Setting("registry:chrome-doh", "automatic");
        var mutator = Mutator();
        await mutator.ApplyAsync(dns, "127.0.0.1", CancellationToken.None);
        await mutator.ApplyAsync(policy, "off", CancellationToken.None);

        var report = await mutator.RestoreAllAsync(CancellationToken.None);

        Assert.Equal("192.168.1.1", dns.Value);
        Assert.Equal("automatic", policy.Value);
        Assert.True(report.IsComplete);
        Assert.Equal(2, report.Restored.Count);
        Assert.Empty(await _journal.GetAllAsync());
    }

    [Fact]
    public async Task AbsentOriginalRestoresToAbsent()
    {
        // A policy key that did not exist before must be removed, not left as some default.
        var policy = Setting("registry:chrome-doh", null);
        var mutator = Mutator();
        await mutator.ApplyAsync(policy, "off", CancellationToken.None);

        await mutator.RestoreAllAsync(CancellationToken.None);

        Assert.Null(policy.Value);
    }

    [Fact]
    public async Task RestoreAfterARestartNeedsOnlyTheJournal()
    {
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        await Mutator().ApplyAsync(dns, "127.0.0.1", CancellationToken.None);

        // A fresh mutator over the same journal stands in for the service restarting after
        // a crash, with no memory of what it changed.
        await Mutator().RestoreAllAsync(CancellationToken.None);

        Assert.Equal("192.168.1.1", dns.Value);
    }

    [Fact]
    public async Task CrashBetweenJournalAndWriteRestoresHarmlessly()
    {
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        dns.ThrowOnWrite = true;
        await Assert.ThrowsAsync<IOException>(
            () => Mutator().ApplyAsync(dns, "127.0.0.1", CancellationToken.None));
        dns.ThrowOnWrite = false;

        var report = await Mutator().RestoreAllAsync(CancellationToken.None);

        Assert.Equal("192.168.1.1", dns.Value);
        Assert.True(report.IsComplete);
        Assert.Empty(await _journal.GetAllAsync());
    }

    [Fact]
    public async Task UnresolvableEntryIsKeptAndReported()
    {
        // The network adapter this entry refers to may have been removed while the service was
        // down. Dropping the entry would lose the original for good.
        await _journal.RecordAsync(new JournalEntry("dns:ipv4:Removed Adapter", "1.1.1.1"));

        var report = await Mutator().RestoreAllAsync(CancellationToken.None);

        Assert.False(report.IsComplete);
        Assert.Contains("dns:ipv4:Removed Adapter", report.Unresolved);
        Assert.NotNull(await _journal.GetAsync("dns:ipv4:Removed Adapter"));
    }

    [Fact]
    public async Task FailedRestoreIsKeptForRetry()
    {
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        var mutator = Mutator();
        await mutator.ApplyAsync(dns, "127.0.0.1", CancellationToken.None);

        dns.ThrowOnWrite = true;
        var failed = await mutator.RestoreAllAsync(CancellationToken.None);

        Assert.False(failed.IsComplete);
        Assert.Contains(failed.Failed, f => f.Key == dns.Key && f.Error is IOException);
        Assert.NotNull(await _journal.GetAsync(dns.Key));

        dns.ThrowOnWrite = false;
        var retried = await mutator.RestoreAllAsync(CancellationToken.None);

        Assert.True(retried.IsComplete);
        Assert.Equal("192.168.1.1", dns.Value);
        Assert.Empty(await _journal.GetAllAsync());
    }

    [Fact]
    public async Task OneFailedRestoreDoesNotStopTheOthers()
    {
        var broken = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        var fine = Setting("registry:chrome-doh", "automatic");
        var mutator = Mutator();
        await mutator.ApplyAsync(broken, "127.0.0.1", CancellationToken.None);
        await mutator.ApplyAsync(fine, "off", CancellationToken.None);
        broken.ThrowOnWrite = true;

        var report = await mutator.RestoreAllAsync(CancellationToken.None);

        Assert.Equal("automatic", fine.Value);
        Assert.Contains(fine.Key, report.Restored);
        Assert.Contains(report.Failed, f => f.Key == broken.Key);
    }

    [Fact]
    public async Task RestoreWithAnEmptyJournalIsComplete()
        => Assert.True((await Mutator().RestoreAllAsync(CancellationToken.None)).IsComplete);

    [Fact]
    public async Task ReapplyingAfterARestoreJournalsAFreshOriginal()
    {
        // Between sessions the user may legitimately change their DNS. The next session must
        // restore to that, not to whatever was there before the previous session.
        var dns = Setting("dns:ipv4:Wi-Fi", "192.168.1.1");
        var mutator = Mutator();
        await mutator.ApplyAsync(dns, "127.0.0.1", CancellationToken.None);
        await mutator.RestoreAllAsync(CancellationToken.None);

        await dns.WriteAsync("9.9.9.9", CancellationToken.None);
        await mutator.ApplyAsync(dns, "127.0.0.1", CancellationToken.None);
        await mutator.RestoreAllAsync(CancellationToken.None);

        Assert.Equal("9.9.9.9", dns.Value);
    }
}

internal sealed class FakeSetting(string key, string? initial) : ISystemSetting
{
    public string Key => key;

    public string? Value { get; private set; } = initial;

    public int WriteCount { get; private set; }

    public bool ThrowOnWrite { get; set; }

    public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);

    public Task WriteAsync(string? value, CancellationToken ct)
    {
        if (ThrowOnWrite) throw new IOException("Simulated write failure.");

        Value = value;
        WriteCount++;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryMutationJournal : IMutationJournal
{
    private readonly ConcurrentDictionary<string, JournalEntry> _entries = new();

    public Task<JournalEntry?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_entries.GetValueOrDefault(key));

    public Task RecordAsync(JournalEntry entry, CancellationToken ct = default)
    {
        _entries.TryAdd(entry.Key, entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JournalEntry>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<JournalEntry>>(_entries.Values.ToList());

    public Task ForgetAsync(string key, CancellationToken ct = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
