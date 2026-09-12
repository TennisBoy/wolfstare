using Wolfstare.Core.Sessions;
using Wolfstare.Core.Storage;
using Wolfstare.Core.Time;
using Wolfstare.Enforcement;
using Wolfstare.Enforcement.Machine;
using Wolfstare.Service;
using Wolfstare.Service.Api;
using Wolfstare.Service.Enforcement;
using Wolfstare.Service.Storage;

var builder = WebApplication.CreateBuilder(args);

var paths = WolfstarePaths.Resolve(builder.Configuration);
var token = ApiToken.CreateAndPersist(paths);

builder.WebHost.UseUrls($"http://127.0.0.1:{paths.Port}");

builder.Services.AddSingleton(new SqliteConnectionFactory(paths.DatabasePath));
builder.Services.AddSingleton<IBlockListRepository, SqliteBlockListRepository>();
builder.Services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
builder.Services.AddSingleton<IMutationJournal, SqliteMutationJournal>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton(new LocalApiGuard(token));
builder.Services.AddHostedService<SessionTicker>();

// Website enforcement (spec §7).
builder.Services.Configure<EnforcementOptions>(builder.Configuration.GetSection(EnforcementOptions.Section));
builder.Services.AddSingleton<RuleSetCache>();
builder.Services.AddSingleton<EnforcementHealth>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton(sp => SystemEnforcementFactory.Create(sp, paths));
builder.Services.AddSingleton<WebsiteEnforcer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebsiteEnforcer>());

var app = builder.Build();

SchemaInitialiser.Initialise(app.Services.GetRequiredService<SqliteConnectionFactory>());

app.UseMiddleware<LocalApiGuard>();
app.MapWolfstareApi();

app.Logger.LogInformation("Wolfstare API listening on http://127.0.0.1:{Port}", paths.Port);
app.Logger.LogInformation("API token written to {TokenPath}", paths.TokenPath);

app.Run();

/// <summary>Exposed so the test host can reference this assembly's entry point.</summary>
public partial class Program;
