using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Wolfstare.Contracts;
using Wolfstare.Core.Sessions;

namespace Wolfstare.Service.Tests.Api;

/// <summary>
/// The UI calls this when the user leaves the unlock page. It must work under a lock (it only
/// makes the lock harder), keep the length, and leave the session's integrity MAC valid — a
/// stale MAC would degrade health on every reset.
/// </summary>
public sealed class ChallengeResetApiTests : IClassFixture<WolfstareFactory>
{
    private readonly WolfstareFactory _factory;

    public ChallengeResetApiTests(WolfstareFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token);
        return client;
    }

    private async Task<string> RequiredText(Guid id)
    {
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var session = Assert.Single(await manager.GetActiveAsync(), s => s.BlockListId == id);
        return Assert.IsType<RandomTextLock>(session.Lock).RequiredText;
    }

    [Fact]
    public async Task ResettingALockedChallengeReplacesItAndKeepsTheLength()
    {
        var client = Client();
        var created = await (await client.PostAsJsonAsync(
            "/api/blocklists", new CreateBlockListRequest("Reset", [new RuleDto("domain", "reddit.com")], null)))
            .Content.ReadFromJsonAsync<BlockListDto>();
        var id = created!.Id;
        (await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(null, new LockDto("randomtext", null, 3000))))
            .EnsureSuccessStatusCode();
        var before = await RequiredText(id);

        var reset = await client.PostAsync($"/api/blocklists/{id}/challenge/reset", null);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var after = await RequiredText(id);
        Assert.NotEqual(before, after);
        Assert.Equal(before.Length, after.Length);

        var status = await client.GetFromJsonAsync<StatusDto>("/api/status");
        Assert.Equal("ok", status!.Health);
        Assert.Equal(before.Length, Assert.Single(status.ActiveSessions, s => s.BlockListId == id).UnlockTextLength);

        // The old text is void; the new one unlocks.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, before))).StatusCode);
    }

    [Fact]
    public async Task ResettingAListWithNoRandomTextSessionIsNotFound()
        => Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client().PostAsync($"/api/blocklists/{Guid.NewGuid()}/challenge/reset", null)).StatusCode);
}
