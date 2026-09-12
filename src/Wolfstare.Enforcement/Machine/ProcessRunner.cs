using System.Diagnostics;
using System.Text;

namespace Wolfstare.Enforcement.Machine;

/// <summary>The exit code and captured output of an external command.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a console tool (netsh) and captures its output. The abstraction exists so the settings
/// that depend on it stay swappable, even though they are not themselves unit-tested (spec §13).
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct);
}

/// <summary>Runs the real process, without a shell so arguments are not re-interpreted.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
