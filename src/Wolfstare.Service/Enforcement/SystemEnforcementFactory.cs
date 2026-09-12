using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolfstare.Core.Storage;
using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Service.Enforcement;

/// <summary>
/// Chooses the enforcement backend. When <c>ModifySystem</c> is off — the default — the machine
/// is never touched, so a dev or test run cannot disrupt real networking.
/// </summary>
public static class SystemEnforcementFactory
{
    public static ISystemEnforcement Create(IServiceProvider services, WolfstarePaths paths)
    {
        var options = services.GetRequiredService<IOptions<EnforcementOptions>>().Value;
        if (!options.ModifySystem)
            return new NullSystemEnforcement();

        var proxyServer = $"127.0.0.1:{options.ProxyPort}";
        var exePath = Environment.ProcessPath ?? "Wolfstare.Service.exe";

        return new WindowsSystemEnforcement(
            services.GetRequiredService<IMutationJournal>(),
            services.GetRequiredService<IProcessRunner>(),
            proxyServer,
            exePath,
            CurrentUserSid(),
            services.GetRequiredService<ILogger<WindowsSystemEnforcement>>());
    }

    /// <summary>
    /// The interactive user's SID. Running as LocalSystem this is the service account, which is
    /// wrong for the per-user proxy hive — the installer supplies the real interactive SID via
    /// configuration in a later phase. For now it is a best effort so the service starts.
    /// </summary>
    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? WindowsIdentity.GetCurrent().Name;
    }
}
