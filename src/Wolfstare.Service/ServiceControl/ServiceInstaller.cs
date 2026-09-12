using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Wolfstare.Contracts;

namespace Wolfstare.Service.ServiceControl;

/// <summary>
/// The one-shot <c>install</c> / <c>uninstall</c> verbs. These run as an ordinary elevated
/// process, not the service, and shell out to <c>sc.exe</c>. The install-time hardening —
/// auto-restart failure actions, and the stop-denying SDDL — lives here and in the scripts.
///
/// Uninstall honours <see cref="UninstallGuard"/>: it refuses while a lock is active by asking
/// the running service, so a timed block cannot be escaped by uninstalling (spec §9).
/// </summary>
public static class ServiceInstaller
{
    private const string ServiceName = "Wolfstare";

    public static int Run(string verb, string[] args)
    {
        try
        {
            return verb switch
            {
                "install" => Install(),
                "uninstall" => Uninstall(),
                _ => 1,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{verb} failed: {ex.Message}");
            return 1;
        }
    }

    private static int Install()
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Could not determine the service executable path.");

        // start=auto so it survives reboot; the "run" arg keeps it out of the install branch.
        Sc($"create {ServiceName} binPath= \"\\\"{exe}\\\" run\" start= auto DisplayName= \"Wolfstare Blocker\"");

        // Restart on the first, second, and subsequent failures — tamper resistance (spec §9):
        // killing the process just brings it back.
        Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");

        Sc($"start {ServiceName}");

        Console.WriteLine("Wolfstare installed and started.");
        Console.WriteLine("Enforcement is active only if Wolfstare:Enforcement:ModifySystem is true in configuration.");
        return 0;
    }

    private static int Uninstall()
    {
        if (!UninstallAllowed(out var reason))
        {
            Console.Error.WriteLine(reason);
            return 2;
        }

        Sc($"stop {ServiceName}", ignoreFailure: true);
        Sc($"delete {ServiceName}");
        Console.WriteLine("Wolfstare uninstalled.");
        return 0;
    }

    /// <summary>
    /// Asks the running service whether any lock is active. If the service is not reachable, the
    /// safe assumption is that uninstall is allowed — a dead service is enforcing nothing.
    /// </summary>
    private static bool UninstallAllowed(out string? reason)
    {
        reason = null;

        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var paths = WolfstarePaths.Resolve(config);
        if (!File.Exists(paths.TokenPath)) return true;

        try
        {
            var token = File.ReadAllText(paths.TokenPath);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{paths.Port}") };
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var status = client.GetFromJsonAsync<StatusDto>("/api/status").GetAwaiter().GetResult();
            if (status is null) return true;

            var lockedCount = status.ActiveSessions.Count(s => s.LockKind != "none" && !s.CanBeStopped);
            if (lockedCount == 0) return true;

            reason =
                $"{lockedCount} locked block session(s) are still active. Wolfstare will not uninstall until "
                + "they end — this is the point of a lock. Wait for the timer or unlock a password block first.";
            return false;
        }
        catch (HttpRequestException)
        {
            return true; // service not reachable → nothing is being enforced
        }
    }

    private static void Sc(string arguments, bool ignoreFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        process.WaitForExit();

        if (process.ExitCode != 0 && !ignoreFailure)
            throw new InvalidOperationException(
                $"sc {arguments} failed ({process.ExitCode}): {process.StandardError.ReadToEnd().Trim()}");
    }
}
