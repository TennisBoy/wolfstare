using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolfstare.Contracts;
using Wolfstare.Service;

namespace Wolfstare.Service.Tests.Api;

/// <summary>
/// End-to-end tests over the real HTTP surface.
///
/// The load-bearing cases are the 423s: a timed session must be immune to <c>stop</c>,
/// <c>unlock</c>, <c>PUT</c>, and <c>DELETE</c> alike. If any one of those ends it, the
/// application's only guarantee is gone.
/// </summary>
public sealed class ApiTests : IClassFixture<WolfstareFactory>
{
    private readonly WolfstareFactory _factory;

    public ApiTests(WolfstareFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Token);
        return client;
    }

    private static CreateBlockListRequest NewList(string name = "Focus")
        => new(name, [new RuleDto("domain", "reddit.com")], null);

    private async Task<Guid> CreateList(HttpClient client, string name = "Focus")
    {
        var response = await client.PostAsJsonAsync("/api/blocklists", NewList(name));
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<BlockListDto>();
        return created!.Id;
    }

    // ---- authentication ----

    [Fact]
    public async Task RequestWithoutATokenIsRejected()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequestWithAWrongTokenIsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task CrossOriginRequestIsRejected()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task LoopbackOriginIsAccepted()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }

    // ---- block lists ----

    [Fact]
    public async Task StatusIsEmptyWithNoActiveSessions()
    {
        var status = await Client().GetFromJsonAsync<StatusDto>("/api/status");

        Assert.NotNull(status);
        Assert.Equal("ok", status.Health);
    }

    [Fact]
    public async Task CreatingAListWithAnInvalidRuleReturnsTheValidatorMessage()
    {
        var request = new CreateBlockListRequest(
            "Bad", [new RuleDto("app.imageName", "explorer.exe")], null);

        var response = await Client().PostAsJsonAsync("/api/blocklists", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Contains("explorer.exe", error!.Message);
    }

    [Fact]
    public async Task CreatingAListWithAnUnknownRuleKindIsRejected()
    {
        var request = new CreateBlockListRequest("Bad", [new RuleDto("telepathy", "x")], null);

        var response = await Client().PostAsJsonAsync("/api/blocklists", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatedListCanBeReadBack()
    {
        var client = Client();
        var id = await CreateList(client, "Readback");

        var list = await client.GetFromJsonAsync<BlockListDto>($"/api/blocklists/{id}");

        Assert.Equal("Readback", list!.Name);
        Assert.Equal("domain", Assert.Single(list.Rules).Kind);
    }

    // ---- the 423 surface ----

    [Fact]
    public async Task TimedSessionCannotBeStopped()
    {
        var client = Client();
        var id = await CreateList(client, "Timed stop");

        var start = await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto("timed", null)));
        start.EnsureSuccessStatusCode();

        var stop = await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/stop", new StopSessionRequest(null));

        Assert.Equal(HttpStatusCode.Locked, stop.StatusCode);
    }

    [Fact]
    public async Task TimedSessionCannotBeUnlockedWithAPassword()
    {
        var client = Client();
        var id = await CreateList(client, "Timed unlock");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto("timed", null)));

        var unlock = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, "please"));

        Assert.Equal(HttpStatusCode.Locked, unlock.StatusCode);
    }

    [Fact]
    public async Task BlockListWithATimedSessionCannotBeEdited()
    {
        var client = Client();
        var id = await CreateList(client, "Timed edit");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto("timed", null)));

        var update = await client.PutAsJsonAsync($"/api/blocklists/{id}", NewList("Renamed"));

        Assert.Equal(HttpStatusCode.Locked, update.StatusCode);
    }

    [Fact]
    public async Task BlockListWithATimedSessionCannotBeDeleted()
    {
        var client = Client();
        var id = await CreateList(client, "Timed delete");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto("timed", null)));

        var delete = await client.DeleteAsync($"/api/blocklists/{id}");

        Assert.Equal(HttpStatusCode.Locked, delete.StatusCode);
    }

    [Fact]
    public async Task TimedLockWithoutADurationIsRefused()
    {
        var client = Client();
        var id = await CreateList(client, "No duration");

        var start = await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(null, new LockDto("timed", null)));

        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
    }

    // ---- password locks ----

    [Fact]
    public async Task PasswordSessionRejectsTheWrongPassword()
    {
        var client = Client();
        var id = await CreateList(client, "Password wrong");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(null, new LockDto("password", "hunter2")));

        var unlock = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, unlock.StatusCode);
    }

    [Fact]
    public async Task PasswordSessionAcceptsTheCorrectPassword()
    {
        var client = Client();
        var id = await CreateList(client, "Password right");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(null, new LockDto("password", "hunter2")));

        var unlock = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, "hunter2"));

        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
    }

    [Fact]
    public async Task PasswordLockWithoutAPasswordIsRefused()
    {
        var client = Client();
        var id = await CreateList(client, "Password missing");

        var start = await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(null, new LockDto("password", null)));

        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
    }

    // ---- unlocked sessions ----

    [Fact]
    public async Task UnlockedSessionCanBeStoppedAndTheListEdited()
    {
        var client = Client();
        var id = await CreateList(client, "Unlocked");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto("none", null)));

        var stop = await client.PostAsJsonAsync($"/api/blocklists/{id}/stop", new StopSessionRequest(null));
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        var update = await client.PutAsJsonAsync($"/api/blocklists/{id}", NewList("Renamed"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    [Fact]
    public async Task StartingASecondSessionForTheSameListConflicts()
    {
        var client = Client();
        var id = await CreateList(client, "Double start");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto("none", null)));

        var second = await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto("none", null)));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task ActiveSessionAppearsInStatusWithRemainingTime()
    {
        var client = Client();
        var id = await CreateList(client, "Status check");
        await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(45, new LockDto("timed", null)));

        var status = await client.GetFromJsonAsync<StatusDto>("/api/status");

        var session = Assert.Single(status!.ActiveSessions, s => s.BlockListId == id);
        Assert.Equal("timed", session.LockKind);
        Assert.Equal("Status check", session.BlockListName);
        Assert.False(session.CanBeStopped);
        Assert.InRange(session.RemainingSeconds!.Value, 2699, 2700);
    }

    [Fact]
    public async Task UnknownBlockListReturnsNotFound()
        => Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client().GetAsync($"/api/blocklists/{Guid.NewGuid()}")).StatusCode);
}

/// <summary>Hosts the real service against a throwaway database directory.</summary>
public sealed class WolfstareFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"wolfstare-api-{Guid.NewGuid():N}");

    public string Token => File.ReadAllText(Path.Combine(_dataDirectory, "api.token"));

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);

        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Wolfstare:DataDirectory"] = _dataDirectory,

                // Bind enforcement listeners to ephemeral ports so the test host never fights
                // for 53/80/443 — those may be taken (as port 80 is on CI runners), which would
                // degrade health and is unrelated to what these tests check.
                ["Wolfstare:Enforcement:DnsPort"] = "0",
                ["Wolfstare:Enforcement:ProxyPort"] = "0",
                ["Wolfstare:Enforcement:TransparentHttpPort"] = "0",
                ["Wolfstare:Enforcement:TransparentTlsPort"] = "0",
            }));

        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
