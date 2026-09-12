using System.Runtime.InteropServices;
using Microsoft.Win32;
using Wolfstare.Core.Enforcement;

namespace Wolfstare.Enforcement.Machine;

/// <summary>
/// The interactive user's WinINET proxy configuration (spec §7.3), written under their hive
/// because the service runs as LocalSystem and the setting is per-user. After writing, WinINET
/// must be told to reload or open browsers keep the old settings until restarted.
///
/// The value encodes all three fields — enabled flag, server, bypass list — as one string, so
/// the whole configuration is restored atomically. Not unit-tested (spec §13).
/// </summary>
public sealed class SystemProxySetting(string userSid, string proxyServer) : ISystemSetting
{
    private const string SubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public string Key => $"proxy:{userSid}";

    public Task<string?> ReadAsync(CancellationToken ct)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        using var key = root.OpenSubKey($@"{userSid}\{SubKey}", writable: false);
        if (key is null) return Task.FromResult<string?>(null);

        var enable = (int?)key.GetValue("ProxyEnable") ?? 0;
        var server = (string?)key.GetValue("ProxyServer") ?? string.Empty;
        var bypass = (string?)key.GetValue("ProxyOverride") ?? string.Empty;

        return Task.FromResult<string?>($"{enable}\n{server}\n{bypass}");
    }

    public Task WriteAsync(string? value, CancellationToken ct)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        using var key = root.CreateSubKey($@"{userSid}\{SubKey}", writable: true);

        if (value is null)
        {
            // Restoring to "never configured": disable the proxy rather than deleting the
            // values, which is what WinINET treats as the default.
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }
        else
        {
            var parts = value.Split('\n');
            key.SetValue("ProxyEnable", int.Parse(parts[0]), RegistryValueKind.DWord);
            key.SetValue("ProxyServer", parts.ElementAtOrDefault(1) ?? string.Empty, RegistryValueKind.String);
            key.SetValue("ProxyOverride", parts.ElementAtOrDefault(2) ?? "<local>", RegistryValueKind.String);
        }

        NotifyWinInet();
        return Task.CompletedTask;
    }

    /// <summary>The value to apply while blocking: proxy on, pointed at our listener, loopback bypassed.</summary>
    public string DesiredValue => $"1\n{proxyServer}\n<-loopback>";

    private static void NotifyWinInet()
    {
        // Without these two broadcasts, already-running browsers keep using the previous proxy
        // configuration until they are restarted.
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);
}
