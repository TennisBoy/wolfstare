using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement.Apps;

/// <summary>Kills a process by id. Best-effort: a process we cannot touch is logged, not fatal.</summary>
public interface IProcessTerminator
{
    void Terminate(int pid);
}

/// <summary>
/// The ETW backstop to IFEO (spec §8.2). IFEO catches a blocked app at launch by filename;
/// renaming the executable defeats that, so this watches every process start, resolves the
/// image's identity, and terminates anything the rules deny — matching on publisher or
/// description, which a rename does not change.
///
/// A short race is inherent: the process runs briefly before we terminate it. IFEO covers the
/// common pre-launch case; this covers the rename. The ETW subscription is not unit-tested (it
/// needs an elevated kernel session), but <see cref="HandleProcessStart"/> — everything that
/// decides and acts — is, through an injected identity lookup.
/// </summary>
public sealed class ProcessWatcher(
    RuleSetCache rules,
    IProcessTerminator terminator,
    Func<int, ProcessIdentity?> identify,
    ILogger logger) : IDisposable
{
    private TraceEventSession? _session;

    /// <summary>Raised with the identity of each process this watcher terminates.</summary>
    public event Action<ProcessIdentity>? Terminated;

    /// <summary>
    /// Subscribes to kernel process-start events. Requires an elevated session; throws if the
    /// caller lacks the rights, which the hosting layer catches to degrade rather than crash.
    /// </summary>
    public void Start()
    {
        // A real-time kernel session named so a leaked session from a crashed run is reused
        // rather than multiplied.
        _session = new TraceEventSession("WolfstareProcessWatcher");
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
        _session.Source.Kernel.ProcessStart += OnProcessStart;

        // Process() blocks until the session stops, so it runs on its own thread.
        var thread = new Thread(() => _session.Source.Process()) { IsBackground = true, Name = "wolfstare-etw" };
        thread.Start();
    }

    private void OnProcessStart(ProcessTraceData data) => HandleProcessStart(data.ProcessID);

    /// <summary>
    /// Resolves the process, and terminates it if the rules deny it and it is not critical.
    /// Public so the tested path is exactly the path ETW drives.
    /// </summary>
    public void HandleProcessStart(int pid)
    {
        try
        {
            var identity = identify(pid);
            if (identity is null) return; // exited or inaccessible before we could inspect it

            if (!ProcessStartDecision.ShouldTerminate(rules.Current, identity)) return;

            terminator.Terminate(pid);
            RaiseTerminated(identity);
        }
        catch (Exception ex)
        {
            // One process we cannot handle must never stop us handling the next.
            logger.LogWarning(ex, "Failed to handle process start for PID {Pid}.", pid);
        }
    }

    private void RaiseTerminated(ProcessIdentity identity)
    {
        try
        {
            Terminated?.Invoke(identity);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A Terminated subscriber threw.");
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}

/// <summary>Terminates a process via the Win32 API, logging an access denial rather than throwing.</summary>
public sealed class WindowsProcessTerminator(ILogger<WindowsProcessTerminator> logger) : IProcessTerminator
{
    private const int ProcessTerminate = 0x0001;

    public void Terminate(int pid)
    {
        var handle = OpenProcess(ProcessTerminate, false, pid);
        if (handle == IntPtr.Zero)
        {
            // Already gone, or a higher-integrity process we cannot open. Logged, not fatal —
            // the spec accepts that an administrator with more rights than us always wins.
            logger.LogDebug("Could not open PID {Pid} to terminate it (error {Error}).",
                pid, Marshal.GetLastWin32Error());
            return;
        }

        try
        {
            if (!TerminateProcess(handle, 1))
                logger.LogDebug("TerminateProcess failed for PID {Pid} (error {Error}).",
                    pid, Marshal.GetLastWin32Error());
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
