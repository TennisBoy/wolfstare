using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Enforcement;
using Wolfstare.Enforcement.Apps;
using Wolfstare.Enforcement.Dns;
using Wolfstare.Enforcement.Proxy;
using Wolfstare.Service.ServiceControl;

namespace Wolfstare.Service.Enforcement;

/// <summary>
/// Ties active sessions to actual blocking. It keeps the <see cref="RuleSetCache"/> the servers
/// read in step with the session manager, runs the DNS sinkhole and proxy, and — only when
/// <c>ModifySystem</c> is set — applies and restores the machine configuration that makes the
/// block bite.
///
/// The refresh is idempotent and cheap, so it runs on a short timer as well as being callable
/// directly (which is what the tests use for determinism). System settings are applied once
/// when website enforcement first engages and restored once when it disengages, not on every
/// tick.
/// </summary>
public sealed class WebsiteEnforcer : BackgroundService, IEnforcementRefresh
{
    private readonly SessionManager _manager;
    private readonly RuleSetCache _cache;
    private readonly ISystemEnforcement _system;
    private readonly IAppEnforcement _app;
    private readonly IServiceHardening _hardening;
    private readonly ILogger<WebsiteEnforcer> _logger;
    private readonly EnforcementOptions? _options;
    private readonly EnforcementHealth? _health;
    private readonly ProcessWatcher? _watcher;

    private DnsSinkhole? _sinkhole;
    private BlockProxy? _proxy;
    private bool _websiteEngaged;
    private bool _appEngaged;
    private bool _stopDenied;

    /// <summary>Test constructor: collaborators only. Servers and refresh are driven explicitly.</summary>
    public WebsiteEnforcer(
        SessionManager manager,
        RuleSetCache cache,
        ISystemEnforcement system,
        ILogger<WebsiteEnforcer> logger,
        IAppEnforcement? app = null,
        IServiceHardening? hardening = null)
    {
        _manager = manager;
        _cache = cache;
        _system = system;
        _app = app ?? new NullAppEnforcement();
        _hardening = hardening ?? new NullServiceHardening();
        _logger = logger;
    }

    /// <summary>Hosted constructor: DI also supplies options, health, app enforcement, and the watcher.</summary>
    public WebsiteEnforcer(
        SessionManager manager,
        RuleSetCache cache,
        ISystemEnforcement system,
        IAppEnforcement app,
        IServiceHardening hardening,
        ILogger<WebsiteEnforcer> logger,
        IOptions<EnforcementOptions> options,
        EnforcementHealth health,
        ProcessWatcher? watcher = null)
        : this(manager, cache, system, logger, app, hardening)
    {
        _options = options.Value;
        _health = health;
        _watcher = watcher;
    }

    /// <summary>The sinkhole's bound endpoint once started; null before <see cref="StartServersAsync"/>.</summary>
    public IPEndPoint? DnsEndpoint => _sinkhole?.LocalEndpoint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options ?? new EnforcementOptions();

        // Startup restore first: if a previous run crashed mid-enforcement, put the machine
        // back before we capture "originals" that are really our own leftover values (spec §7.5).
        if (options.ModifySystem)
        {
            try
            {
                await _system.RestoreAsync(stoppingToken);
                await _app.RestoreAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup restore failed; continuing.");
            }

            // The watcher needs an elevated ETW session; failing to start it degrades rather
            // than crashes, since IFEO still blocks the common case.
            try
            {
                _watcher?.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not start the process watcher; the rename backstop is unavailable.");
                _health?.Degrade("The process watcher could not start; a renamed blocked app may run until relaunch.");
            }
        }

        await StartServersAsync(options, stoppingToken, _health);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.RefreshSeconds)));
        do
        {
            try
            {
                await RefreshOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enforcement refresh failed; will retry.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        await StopServersAsync();
    }

    /// <summary>The immediate-refresh hook (spec: no perceptible latency on a session change).</summary>
    public Task RefreshNowAsync(CancellationToken ct) => RefreshOnceAsync(ct);

    /// <summary>
    /// Reconciles enforcement with the current sessions: refreshes the cache the servers read,
    /// then engages or disengages the machine-level settings when that boundary is crossed.
    /// </summary>
    public async Task RefreshOnceAsync(CancellationToken ct)
    {
        var rules = await _manager.GetEffectiveRuleSetAsync(ct);
        _cache.Update(rules);

        // The two halves engage independently. Website takeover (DNS/proxy) only makes sense
        // when a domain rule is in play; IFEO only when an image-name rule is. An app-only
        // session must not take over DNS, and a site-only session must not write IFEO keys.
        // (The ETW watcher, when running, reads the cache continuously and needs no engagement.)
        var shouldEngageWebsite = rules.Rules.Concat(rules.Allowlist).Any(r => r is DomainRule);
        var shouldEngageApp = rules.Rules.OfType<AppRule>().Any(r => r.Matcher is ImageNameMatcher);

        if (shouldEngageWebsite && !_websiteEngaged)
        {
            await _system.ApplyAsync(rules, ct);
            _websiteEngaged = true;
        }
        else if (!shouldEngageWebsite && _websiteEngaged)
        {
            await _system.RestoreAsync(ct);
            _websiteEngaged = false;
        }

        // Deny the interactive user the right to stop the service while any block is active, so
        // the enforcement process cannot be killed off from under a lock (spec §9). No-op unless
        // Wolfstare is installed as a service.
        var anyActive = (await _manager.GetActiveAsync(ct)).Count > 0;
        if (anyActive && !_stopDenied)
        {
            await _hardening.DenyStopAsync(ct);
            _stopDenied = true;
        }
        else if (!anyActive && _stopDenied)
        {
            await _hardening.AllowStopAsync(ct);
            _stopDenied = false;
        }

        // App enforcement self-heals: ApplyAsync runs every tick while engaged, not just on the
        // transition. ApplyAsync is idempotent (the mutator skips a key already at its value), so
        // the only time it writes is when a key is missing — which is exactly the case when
        // someone has just deleted one in regedit. It reappears within a refresh interval. This
        // also means a rule added to a running session takes effect on the next tick.
        if (shouldEngageApp)
        {
            await _app.ApplyAsync(rules, ct);
            _appEngaged = true;
        }
        else if (_appEngaged)
        {
            await _app.RestoreAsync(ct);
            _appEngaged = false;
        }
    }

    /// <summary>
    /// Binds the sinkhole and proxy. A port that cannot be bound degrades health and logs; it
    /// never throws out of here and never fails open (spec §7.4).
    /// </summary>
    public async Task StartServersAsync(EnforcementOptions options, CancellationToken ct, EnforcementHealth? health = null)
    {
        var upstreams = ParseUpstreams(options.Upstreams);

        _sinkhole = new DnsSinkhole(_cache, upstreams, _logger);
        try
        {
            await _sinkhole.StartAsync(new IPEndPoint(IPAddress.Loopback, options.DnsPort), ct);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not bind the DNS sinkhole on port {Port}.", options.DnsPort);
            health?.Degrade(
                $"The DNS resolver could not start on port {options.DnsPort} — another program may be using it. "
                + "Website blocking is limited until this is resolved.");
            await _sinkhole.DisposeAsync();
            _sinkhole = null;
        }

        _proxy = new BlockProxy(_cache, _logger, new NullCertificateProvider());
        try
        {
            await _proxy.StartExplicitAsync(new IPEndPoint(IPAddress.Loopback, options.ProxyPort), ct);
            await _proxy.StartTransparentHttpAsync(new IPEndPoint(IPAddress.Loopback, options.TransparentHttpPort), ct);
            await _proxy.StartTransparentTlsAsync(new IPEndPoint(IPAddress.Loopback, options.TransparentTlsPort), ct);
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Could not bind a proxy listener.");
            health?.Degrade("A proxy listener could not start; the block page may be unavailable.");
        }
    }

    public async Task StopServersAsync()
    {
        _watcher?.Dispose();
        if (_proxy is not null) await _proxy.DisposeAsync();
        if (_sinkhole is not null) await _sinkhole.DisposeAsync();
        _proxy = null;
        _sinkhole = null;
    }

    private static IReadOnlyList<IPEndPoint> ParseUpstreams(IReadOnlyList<string> upstreams)
    {
        var endpoints = new List<IPEndPoint>();
        foreach (var entry in upstreams)
        {
            var parts = entry.Split(':', 2);
            if (!IPAddress.TryParse(parts[0], out var address)) continue;
            var port = parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : 53;
            endpoints.Add(new IPEndPoint(address, port));
        }

        return endpoints.Count > 0 ? endpoints : [new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53)];
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
