using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Enforcement;
using Wolfstare.Enforcement.Dns;
using Wolfstare.Enforcement.Proxy;

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
public sealed class WebsiteEnforcer : BackgroundService
{
    private readonly SessionManager _manager;
    private readonly RuleSetCache _cache;
    private readonly ISystemEnforcement _system;
    private readonly ILogger<WebsiteEnforcer> _logger;
    private readonly EnforcementOptions? _options;
    private readonly EnforcementHealth? _health;

    private DnsSinkhole? _sinkhole;
    private BlockProxy? _proxy;
    private bool _engaged;

    /// <summary>Test constructor: collaborators only. Servers and refresh are driven explicitly.</summary>
    public WebsiteEnforcer(
        SessionManager manager,
        RuleSetCache cache,
        ISystemEnforcement system,
        ILogger<WebsiteEnforcer> logger)
    {
        _manager = manager;
        _cache = cache;
        _system = system;
        _logger = logger;
    }

    /// <summary>Hosted constructor: DI also supplies options and the shared health object.</summary>
    public WebsiteEnforcer(
        SessionManager manager,
        RuleSetCache cache,
        ISystemEnforcement system,
        ILogger<WebsiteEnforcer> logger,
        IOptions<EnforcementOptions> options,
        EnforcementHealth health)
        : this(manager, cache, system, logger)
    {
        _options = options.Value;
        _health = health;
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup restore failed; continuing.");
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

    /// <summary>
    /// Reconciles enforcement with the current sessions: refreshes the cache the servers read,
    /// then engages or disengages the machine-level settings when that boundary is crossed.
    /// </summary>
    public async Task RefreshOnceAsync(CancellationToken ct)
    {
        var rules = await _manager.GetEffectiveRuleSetAsync(ct);
        _cache.Update(rules);

        // Website enforcement only has something to do when a domain rule is in play. An
        // app-only session must not take over the machine's DNS.
        var shouldEngage = rules.Rules.Concat(rules.Allowlist).Any(r => r is DomainRule);

        if (shouldEngage && !_engaged)
        {
            await _system.ApplyAsync(rules, ct);
            _engaged = true;
        }
        else if (!shouldEngage && _engaged)
        {
            await _system.RestoreAsync(ct);
            _engaged = false;
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
