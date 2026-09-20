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
using Wolfstare.Service;
using Wolfstare.Service.Enforcement;

namespace Wolfstare.Service.Tests.Api;

/// <summary>
/// A correct unlock removes the session in the domain before enforcement is reconciled. If that
/// post-unlock reconcile throws (as it did when tearing down a hardened IFEO key raised
/// UnauthorizedAccessException), the unlock must still be reported as the success it was — the
/// periodic reconcile is eventually-consistent and will finish the teardown. Returning 500 here
/// would tell the user their unlock failed after it had actually happened, stranding them: the
/// session is already gone, so a retype can't be resubmitted.
/// </summary>
public sealed class UnlockResilienceTests : IClassFixture<UnlockResilienceTests.ThrowingRefreshFactory>
{
    private readonly ThrowingRefreshFactory _factory;

    public UnlockResilienceTests(ThrowingRefreshFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token);
        return client;
    }

    [Fact]
    public async Task ACorrectUnlockSucceedsEvenWhenThePostUnlockRefreshThrows()
    {
        var client = Client();

        var created = await (await client.PostAsJsonAsync(
            "/api/blocklists",
            new CreateBlockListRequest("Resilience", [new RuleDto("domain", "reddit.com")], null)))
            .Content.ReadFromJsonAsync<BlockListDto>();
        var id = created!.Id;

        // Start also refreshes, so let that one succeed; arm the failure only for the unlock.
        _factory.Refresh.Armed = false;
        (await client.PostAsJsonAsync(
            $"/api/blocklists/{id}/start", new StartSessionRequest(null, new LockDto("randomtext", null, 200))))
            .EnsureSuccessStatusCode();

        // White-box: read the required text in-process (the API deliberately never exposes it).
        var manager = _factory.Services.GetRequiredService<SessionManager>();
        var session = Assert.Single(await manager.GetActiveAsync(), s => s.BlockListId == id);
        var requiredText = Assert.IsType<RandomTextLock>(session.Lock).RequiredText;

        _factory.Refresh.Armed = true;
        var unlock = await client.PostAsJsonAsync("/api/unlock", new UnlockRequest(id, requiredText));

        // The refresh stub throws, but the unlock happened: expect success, not 500.
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var status = await client.GetFromJsonAsync<StatusDto>("/api/status");
        Assert.DoesNotContain(status!.ActiveSessions, s => s.BlockListId == id);
    }

    /// <summary>Throws from the immediate refresh when armed, standing in for a reconcile failure.</summary>
    public sealed class ThrowingRefresh : IEnforcementRefresh
    {
        public bool Armed { get; set; }

        public Task RefreshNowAsync(CancellationToken ct)
            => Armed ? throw new UnauthorizedAccessException("simulated reconcile failure") : Task.CompletedTask;
    }

    public sealed class ThrowingRefreshFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _dataDirectory =
            Path.Combine(Path.GetTempPath(), $"wolfstare-resilience-{Guid.NewGuid():N}");

        public string Token => File.ReadAllText(Path.Combine(_dataDirectory, "api.token"));

        public ThrowingRefresh Refresh { get; } = new();

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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.ConfigureTestServices(services =>
                services.AddSingleton<IEnforcementRefresh>(Refresh));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
