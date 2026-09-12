using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolfstare.Core.Enforcement;
using Wolfstare.Core.Storage;
using Wolfstare.Enforcement;
using Wolfstare.Enforcement.Apps;
using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Service.Enforcement;

/// <summary>
/// Builds the enforcement backends. When <c>ModifySystem</c> is off — the default — the system
/// and app backends are the no-op implementations, so a dev or test run never changes DNS,
/// writes IFEO keys, or terminates a process. The process watcher is always constructed but
/// only started (elevated) under <c>ModifySystem</c>.
///
/// Each backend gets its own <see cref="JournalledMutator"/>, which is fine: the mutator is
/// stateless and both share the one journal. The prefix-scoped restore keeps them from
/// disturbing each other's settings.
/// </summary>
public static class EnforcementFactory
{
    public static ISystemEnforcement CreateSystem(IServiceProvider services, WolfstarePaths paths)
    {
        var options = Options(services);
        if (!options.ModifySystem) return new NullSystemEnforcement();

        return new WindowsSystemEnforcement(
            Mutator(services, paths, options),
            services.GetRequiredService<IProcessRunner>(),
            ProxyServer(options),
            ServiceExePath(),
            CurrentUserSid(),
            services.GetRequiredService<ILogger<WindowsSystemEnforcement>>());
    }

    public static IAppEnforcement CreateApp(IServiceProvider services, WolfstarePaths paths)
    {
        var options = Options(services);
        if (!options.ModifySystem) return new NullAppEnforcement();

        return new WindowsAppEnforcement(
            Mutator(services, paths, options),
            StubPath(),
            services.GetRequiredService<ILogger<WindowsAppEnforcement>>());
    }

    /// <summary>
    /// Always returns a watcher; the coordinator starts it only under ModifySystem. Its
    /// identity lookup reads the process image path from the OS and resolves publisher and
    /// description from disk.
    /// </summary>
    public static ProcessWatcher CreateWatcher(IServiceProvider services)
        => new(
            services.GetRequiredService<RuleSetCache>(),
            new WindowsProcessTerminator(services.GetRequiredService<ILogger<WindowsProcessTerminator>>()),
            pid => IdentifyProcess(pid),
            services.GetRequiredService<ILogger<ProcessWatcher>>());

    private static Wolfstare.Core.Rules.ProcessIdentity? IdentifyProcess(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return path is null ? null : ProcessInspector.FromImagePath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited, or is higher-integrity than us so we cannot read its module.
            // Either way there is nothing to act on.
            return null;
        }
    }

    private static ISystemMutator Mutator(IServiceProvider services, WolfstarePaths paths, EnforcementOptions options)
    {
        var resolver = new SystemSettingResolver(
            services.GetRequiredService<IProcessRunner>(),
            ProxyServer(options),
            StubPath(),
            ServiceExePath(),
            CurrentUserSid());

        return new JournalledMutator(services.GetRequiredService<IMutationJournal>(), resolver.Resolve);
    }

    private static EnforcementOptions Options(IServiceProvider services)
        => services.GetRequiredService<IOptions<EnforcementOptions>>().Value;

    private static string ProxyServer(EnforcementOptions options) => $"127.0.0.1:{options.ProxyPort}";

    private static string ServiceExePath() => Environment.ProcessPath ?? "Wolfstare.Service.exe";

    private static string StubPath() => Path.Combine(AppContext.BaseDirectory, "Wolfstare.BlockStub.exe");

    /// <summary>
    /// The interactive user's SID. Running as LocalSystem this is the service account, which is
    /// wrong for the per-user proxy hive — the installer supplies the real interactive SID in a
    /// later phase. Best effort for now so the service starts.
    /// </summary>
    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? identity.Name;
    }
}
