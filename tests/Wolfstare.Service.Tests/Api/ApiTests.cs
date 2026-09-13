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
/// The load-bearing rule: only a random-text lock can be created, and it can be ended only by
/// retyping its text. The weaker lock kinds are refused, and a locked list cannot be edited or
/// deleted out from under the lock.
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

    private static StartSessionRequest RandomText(int length = 5000)
        => new(null, new LockDto("randomtext", null, length));

    // ---- authentication ----

    [Fact]
    public async Task RequestWithoutATokenIsRejected()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthIsReachableWithoutAToken()
    {
        // The liveness probe returns nothing sensitive, so readiness checks need no token.
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    // ---- only random-text locks may be created ----

    [Theory]
    [InlineData("none")]
    [InlineData("timed")]
    [InlineData("password")]
    public async Task WeakerLockKindsAreRefused(string kind)
    {
        var client = Client();
        var id = await CreateList(client, $"Reject {kind}");

        var start = await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(60, new LockDto(kind, "pw")));

        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
    }

    [Fact]
    public async Task ARandomTextLockBelowTheMinimumIsRefused()
    {
        var client = Client();
        var id = await CreateList(client, "Too short");

        var start = await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText(length: 50));

        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
    }

    // ---- a locked list cannot be modified out from under the lock ----

    [Fact]
    public async Task BlockListWithARandomTextSessionCannotBeEdited()
    {
        var client = Client();
        var id = await CreateList(client, "Locked edit");
        (await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText())).EnsureSuccessStatusCode();

        var update = await client.PutAsJsonAsync($"/api/blocklists/{id}", NewList("Renamed"));

        Assert.Equal(HttpStatusCode.Locked, update.StatusCode);
    }

    [Fact]
    public async Task BlockListWithARandomTextSessionCannotBeDeleted()
    {
        var client = Client();
        var id = await CreateList(client, "Locked delete");
        (await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText())).EnsureSuccessStatusCode();

        var delete = await client.DeleteAsync($"/api/blocklists/{id}");

        Assert.Equal(HttpStatusCode.Locked, delete.StatusCode);
    }

    // ---- sessions ----

    [Fact]
    public async Task StartingASecondSessionForTheSameListConflicts()
    {
        var client = Client();
        var id = await CreateList(client, "Double start");
        (await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText())).EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task ActiveSessionAppearsInStatusAsAnIndefiniteRandomTextLock()
    {
        var client = Client();
        var id = await CreateList(client, "Status check");
        (await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText())).EnsureSuccessStatusCode();

        var status = await client.GetFromJsonAsync<StatusDto>("/api/status");

        var session = Assert.Single(status!.ActiveSessions, s => s.BlockListId == id);
        Assert.Equal("randomtext", session.LockKind);
        Assert.Equal("Status check", session.BlockListName);
        Assert.Null(session.RemainingSeconds);        // indefinite — ends only on retype
        Assert.False(session.CanBeStopped);
    }

    [Fact]
    public async Task StatusGivesTheUnlockLengthButNotTheText()
    {
        // The text must not be readable through the API — only its length, and the image. This
        // is what stops a script reading the answer and posting it back.
        var client = Client();
        var id = await CreateList(client, "Random text");
        (await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText())).EnsureSuccessStatusCode();

        var raw = await client.GetStringAsync("/api/status");
        Assert.DoesNotContain("unlockText\"", raw);            // no text field at all
        Assert.Contains("unlockTextLength", raw);

        var status = System.Text.Json.JsonSerializer.Deserialize<StatusDto>(
            raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var session = Assert.Single(status!.ActiveSessions, s => s.BlockListId == id);
        // Word-based text lands at most on, and within one word of, the requested length.
        Assert.InRange(session.UnlockTextLength!.Value, 4980, 5000);
    }

    [Fact]
    public async Task TheUnlockTextIsServedOnlyAsAnImage()
    {
        var client = Client();
        var id = await CreateList(client, "Image challenge");
        (await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText())).EnsureSuccessStatusCode();

        var image = await client.GetAsync($"/api/blocklists/{id}/unlock-image");

        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        var bytes = await image.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);
        // PNG magic number.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }

    [Fact]
    public async Task WrongRetypeIsRefused()
    {
        // The correct-text path is covered exhaustively by the Core StopPolicy tests; the API
        // deliberately has no way to hand a test (or a script) the correct text.
        var client = Client();
        var id = await CreateList(client, "Wrong retype");
        (await client.PostAsJsonAsync($"/api/blocklists/{id}/start", RandomText())).EnsureSuccessStatusCode();

        var wrong = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, new string('x', 5000)));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
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
