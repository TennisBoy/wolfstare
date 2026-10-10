using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolfstare.Contracts;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Time;
using Wolfstare.Service;

namespace Wolfstare.Service.Tests.Api;

/// <summary>
/// A wrong retype tells the user where it first went wrong, and then refuses further attempts
/// for a cooldown. The cooldown is what keeps the mismatch index from being a cheap oracle: a
/// refused-for-cooldown attempt is not evaluated at all, so it reveals nothing — not even
/// whether the text was right.
/// </summary>
public sealed class UnlockFeedbackTests : IClassFixture<UnlockFeedbackTests.ThrottleClockFactory>
{
    private readonly ThrottleClockFactory _factory;

    public UnlockFeedbackTests(ThrottleClockFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token);
        return client;
    }

    /// <summary>Starts a random-text session and returns its id and (white-box) required text.</summary>
    private async Task<(Guid Id, string Text)> StartLocked(HttpClient client, string name)
    {
        var created = await (await client.PostAsJsonAsync(
            "/api/blocklists", new CreateBlockListRequest(name, [new RuleDto("domain", "reddit.com")], null)))
            .Content.ReadFromJsonAsync<BlockListDto>();
        var id = created!.Id;

        (await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(null, new LockDto("randomtext", null, 200))))
            .EnsureSuccessStatusCode();

        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var session = Assert.Single(await manager.GetActiveAsync(), s => s.BlockListId == id);
        return (id, Assert.IsType<RandomTextLock>(session.Lock).RequiredText);
    }

    private static string WrongAt(string text, int index)
        => text[..index] + (text[index] == 'x' ? 'y' : 'x') + text[(index + 1)..];

    [Fact]
    public async Task AWrongRetypeReportsTheFirstWrongPositionAndTheCooldown()
    {
        var client = Client();
        var (id, text) = await StartLocked(client, "Mismatch index");

        var wrong = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, WrongAt(text, 37)));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var body = await wrong.Content.ReadFromJsonAsync<UnlockRefusedDto>();
        Assert.Equal(37, body!.MismatchIndex);
        Assert.Equal(5, body.RetryAfterSeconds);
    }

    [Fact]
    public async Task ACorrectPartialEntryIsReportedAsWrongWhereItEnds()
    {
        // The UI lets you submit before reaching the full length to check progress so far.
        var client = Client();
        var (id, text) = await StartLocked(client, "Partial entry");

        var partial = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, text[..50]));

        Assert.Equal(HttpStatusCode.Unauthorized, partial.StatusCode);
        Assert.Equal(50, (await partial.Content.ReadFromJsonAsync<UnlockRefusedDto>())!.MismatchIndex);
    }

    [Fact]
    public async Task AnAttemptDuringTheCooldownIsRefusedWithoutBeingEvaluated()
    {
        var client = Client();
        var (id, text) = await StartLocked(client, "Cooldown");
        await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, WrongAt(text, 0)));

        // Even the correct text is refused — evaluating it would make the cooldown pointless.
        var retry = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, text));

        Assert.Equal(HttpStatusCode.TooManyRequests, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<UnlockRefusedDto>();
        Assert.Null(body!.MismatchIndex);
        Assert.InRange(body.RetryAfterSeconds!.Value, 1, 5);
        var status = await client.GetFromJsonAsync<StatusDto>("/api/status");
        Assert.Contains(status!.ActiveSessions, s => s.BlockListId == id);
    }

    [Fact]
    public async Task TheCorrectTextUnlocksOnceTheCooldownHasPassed()
    {
        var client = Client();
        var (id, text) = await StartLocked(client, "After cooldown");
        await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, WrongAt(text, 0)));

        _factory.Clock.Advance(TimeSpan.FromSeconds(5));
        var unlock = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, text));

        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
    }

    [Fact]
    public async Task TheStopEndpointIsThrottledToo()
    {
        // /stop takes the same text; otherwise it would be an unthrottled side door to the oracle.
        var client = Client();
        var (id, text) = await StartLocked(client, "Stop side door");
        var wrong = await client.PostAsJsonAsync($"/api/blocklists/{id}/stop", new StopSessionRequest(WrongAt(text, 12)));
        Assert.Equal(12, (await wrong.Content.ReadFromJsonAsync<UnlockRefusedDto>())!.MismatchIndex);

        var retry = await client.PostAsJsonAsync($"/api/blocklists/{id}/stop", new StopSessionRequest(text));

        Assert.Equal(HttpStatusCode.TooManyRequests, retry.StatusCode);
    }

    /// <summary>A monotonic clock the test moves by hand; wall time is irrelevant to the throttle.</summary>
    public sealed class ManualClock : IClock
    {
        private long _ms;

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public long MonotonicMs => Interlocked.Read(ref _ms);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ms, (long)amount.TotalMilliseconds);
    }

    public sealed class ThrottleClockFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _dataDirectory =
            Path.Combine(Path.GetTempPath(), $"wolfstare-feedback-{Guid.NewGuid():N}");

        public string Token => File.ReadAllText(Path.Combine(_dataDirectory, "api.token"));

        public ManualClock Clock { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            Directory.CreateDirectory(_dataDirectory);
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Wolfstare:DataDirectory"] = _dataDirectory,
                    ["Wolfstare:Enforcement:DnsPort"] = "0",
                    ["Wolfstare:Enforcement:ProxyPort"] = "0",
                    ["Wolfstare:Enforcement:TransparentHttpPort"] = "0",
                    ["Wolfstare:Enforcement:TransparentTlsPort"] = "0",
                }));
            return base.CreateHost(builder);
        }

        // Only the throttle gets the manual clock; sessions keep the real one.
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.ConfigureTestServices(services =>
                services.AddSingleton(new UnlockThrottle(Clock, TimeSpan.FromSeconds(5))));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
