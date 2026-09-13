using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Storage;
using Wolfstare.Core.Time;
using Wolfstare.Enforcement;
using Wolfstare.Enforcement.Machine;
using Wolfstare.Service;
using Wolfstare.Service.Api;
using Wolfstare.Service.Enforcement;
using Wolfstare.Service.ServiceControl;
using Wolfstare.Service.Storage;

// `install` / `uninstall` are handled before any host is built — they are one-shot admin
// commands, not the running service. Everything else ("run", or no verb) starts the service.
if (args is [var verb, ..] && verb is "install" or "uninstall")
    return ServiceInstaller.Run(verb, args);

var builder = WebApplication.CreateBuilder(args);

// Runs as a Windows Service when launched by the SCM, and as a plain console app otherwise —
// so `dotnet run` needs no service install for development (spec §12).
builder.Host.UseWindowsService(options => options.ServiceName = "Wolfstare");

var paths = WolfstarePaths.Resolve(builder.Configuration);

// When installed as a service, lock the data directory to SYSTEM + Administrators so a standard
// user cannot tamper with the block database, integrity key, or token. Skipped for a dev
// console run so it never locks a non-elevated developer out of their own data.
if (WindowsServiceHelpers.IsWindowsService())
{
    try { DataDirectoryHardening.Harden(paths.RootDirectory); }
    catch (Exception ex) { Console.Error.WriteLine($"Could not harden the data directory: {ex.Message}"); }
}

var token = ApiToken.CreateAndPersist(paths);

builder.WebHost.UseUrls($"http://127.0.0.1:{paths.Port}");

builder.Services.AddSingleton(new SqliteConnectionFactory(paths.DatabasePath));
builder.Services.AddSingleton(new IntegrityKeyProvider(paths));
builder.Services.AddSingleton<IBlockListRepository, SqliteBlockListRepository>();
builder.Services.AddSingleton<ISessionRepository>(sp => new SqliteSessionRepository(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<IntegrityKeyProvider>().Key,
    tamperedId =>
    {
        sp.GetRequiredService<EnforcementHealth>().Degrade(
            $"Session {tamperedId} failed its integrity check; the database may have been edited. "
            + "Blocks remain in force.");
        sp.GetRequiredService<ILogger<Program>>().LogWarning(
            "Integrity check failed for session {SessionId}; keeping it active (fail closed).", tamperedId);
    }));
builder.Services.AddSingleton<IMutationJournal, SqliteMutationJournal>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton(new LocalApiGuard(token));
builder.Services.AddHostedService<SessionTicker>();

// Enforcement (spec §7 website, §8 apps).
builder.Services.Configure<EnforcementOptions>(builder.Configuration.GetSection(EnforcementOptions.Section));
builder.Services.AddSingleton<RuleSetCache>();
builder.Services.AddSingleton<EnforcementHealth>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton(sp => EnforcementFactory.CreateSystem(sp, paths));
builder.Services.AddSingleton(sp => EnforcementFactory.CreateApp(sp, paths));
builder.Services.AddSingleton(sp => EnforcementFactory.CreateWatcher(sp));
builder.Services.AddSingleton<IServiceHardening>(sp =>
{
    // Harden only when installed as a service and enforcing; otherwise the descriptor edits
    // are meaningless (dev console run) and the null implementation keeps it a no-op.
    var opts = sp.GetRequiredService<IOptions<EnforcementOptions>>().Value;
    return opts.ModifySystem && WindowsServiceHelpers.IsWindowsService()
        ? new WindowsServiceHardening(
            sp.GetRequiredService<IProcessRunner>(),
            "Wolfstare",
            sp.GetRequiredService<ILogger<WindowsServiceHardening>>())
        : new NullServiceHardening();
});
builder.Services.AddSingleton<WebsiteEnforcer>();
builder.Services.AddSingleton<IEnforcementRefresh>(sp => sp.GetRequiredService<WebsiteEnforcer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebsiteEnforcer>());

var app = builder.Build();

SchemaInitialiser.Initialise(app.Services.GetRequiredService<SqliteConnectionFactory>());

app.UseMiddleware<LocalApiGuard>();
app.MapWolfstareApi();

// Serve the built SPA from wwwroot, if present. index.html is served with the API token
// injected, so the same-origin page can call the API; a cross-origin page still can't, because
// it cannot read this page's DOM and the LocalApiGuard rejects its Origin.
app.UseStaticFiles();
app.MapWolfstareSpa(token);

app.Logger.LogInformation("Wolfstare API listening on http://127.0.0.1:{Port}", paths.Port);
app.Logger.LogInformation("API token written to {TokenPath}", paths.TokenPath);

app.Run();
return 0;

/// <summary>Exposed so the test host can reference this assembly's entry point.</summary>
public partial class Program;
