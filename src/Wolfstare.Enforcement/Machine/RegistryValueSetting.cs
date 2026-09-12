using Microsoft.Win32;
using Wolfstare.Core.Enforcement;

namespace Wolfstare.Enforcement.Machine;

/// <summary>
/// A single registry value expressed as an <see cref="ISystemSetting"/>. The backbone of the
/// browser DoH policies (spec §7.2) and the system proxy switch (spec §7.3).
///
/// The value is serialised as "<c>kind:data</c>" (e.g. "<c>dword:0</c>", "<c>string:127.0.0.1:8080</c>")
/// so a DWORD and a string that both read as "0" are never confused across a journal round trip.
/// A null value means the registry value is absent; restoring to null deletes it.
///
/// Not unit-tested — it writes the live registry (spec §13).
/// </summary>
public sealed class RegistryValueSetting(RegistryHive hive, string subKeyPath, string valueName)
    : ISystemSetting
{
    public string Key => $"registry:{hive}:{subKeyPath}:{valueName}";

    public Task<string?> ReadAsync(CancellationToken ct)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = root.OpenSubKey(subKeyPath, writable: false);

        var raw = key?.GetValue(valueName);
        if (raw is null) return Task.FromResult<string?>(null);

        var kind = key!.GetValueKind(valueName);
        return Task.FromResult<string?>(Serialise(kind, raw));
    }

    public Task WriteAsync(string? value, CancellationToken ct)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

        if (value is null)
        {
            using var key = root.OpenSubKey(subKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            return Task.CompletedTask;
        }

        using var target = root.CreateSubKey(subKeyPath, writable: true);
        var (kind, data) = Deserialise(value);
        target.SetValue(valueName, data, kind);
        return Task.CompletedTask;
    }

    private static string Serialise(RegistryValueKind kind, object raw) => kind switch
    {
        RegistryValueKind.DWord => $"dword:{(int)raw}",
        RegistryValueKind.QWord => $"qword:{(long)raw}",
        _ => $"string:{raw}",
    };

    private static (RegistryValueKind Kind, object Data) Deserialise(string value)
    {
        var colon = value.IndexOf(':');
        var kind = colon < 0 ? "string" : value[..colon];
        var data = colon < 0 ? value : value[(colon + 1)..];

        return kind switch
        {
            "dword" => (RegistryValueKind.DWord, int.Parse(data)),
            "qword" => (RegistryValueKind.QWord, long.Parse(data)),
            _ => (RegistryValueKind.String, data),
        };
    }
}
