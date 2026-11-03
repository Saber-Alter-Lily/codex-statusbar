using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexStatusbar;

/// <summary>
/// Authenticate the process that owns this connected pipe, not an unrelated running Codex.
/// Unlike the window locator's compatibility fallback, IPC never trusts a process name or a path
/// containing WindowsApps. The OS-reported package family must include the known publisher ID.
/// Unpackaged/dev builds, changed package publishers and inaccessible identities fail closed;
/// the monitor can still use its read-only SQLite fallback without claiming a live IPC route.
/// </summary>
internal static class CodexIpcServerIdentity
{
    internal const string OfficialPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const uint QueryAndSynchronize = 0x00101000;
    private const int ErrorInsufficientBuffer = 122;
    private const uint WaitTimeout = 258;

    internal static bool IsAcceptedServer(uint pipeServerPid, uint inspectedPid,
        string? packageFamily, string? executablePath) =>
        pipeServerPid > 0 && pipeServerPid <= int.MaxValue && pipeServerPid == inspectedPid
        && string.Equals(packageFamily, OfficialPackageFamily, StringComparison.OrdinalIgnoreCase)
        && CodexIdentityRules.IsCodexDesktop(null, packageFamily, executablePath);

    internal static uint? ReadServerProcessId(NamedPipeClientStream pipe) =>
        pipe.IsConnected && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? pid : null;

    public static VerifiedServer Authenticate(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows()) throw Rejected();
        SafeProcessHandle? process = null;
        try
        {
            if (!pipe.IsConnected || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)
                || pid == 0 || pid > int.MaxValue) throw Rejected();
            process = OpenProcess(QueryAndSynchronize, false, pid);
            if (process.IsInvalid) throw Rejected();
            var length = 0;
            if (GetPackageFamilyName(process, ref length, null) != ErrorInsufficientBuffer
                || length <= 0 || length > 1024) throw Rejected();
            var family = new StringBuilder(length);
            if (GetPackageFamilyName(process, ref length, family) != 0
                || !IsAcceptedServer(pid, GetProcessId(process), family.ToString(), null)) throw Rejected();
            var verified = new VerifiedServer(pid, process);
            verified.EnsureStillBound(pipe);
            process = null; // VerifiedServer owns the handle until this exact connection is closed.
            return verified;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw Rejected();
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static bool CurrentSyntheticProcessHasNoPackage()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var length = 0;
        // APPMODEL_ERROR_NO_PACKAGE. Do not mask a missing export as a successful rejection test.
        return GetPackageFamilyName(process.SafeHandle, ref length, null) == 15700;
    }

    private static UnauthorizedAccessException Rejected() =>
        new("Codex IPC server identity could not be verified; live IPC routing is disabled.");

    internal sealed class VerifiedServer(uint processId, SafeProcessHandle process) : IDisposable
    {
        public void EnsureStillBound(NamedPipeClientStream pipe)
        {
            // Retain the process handle throughout the connection, so a reused PID cannot silently
            // replace the authenticated process. Check liveness and endpoint binding before writes
            // and before processing frames. Unknown/query failures are rejection, never fallback.
            if (process.IsClosed || process.IsInvalid || WaitForSingleObject(process, 0) != WaitTimeout
                || !pipe.IsConnected || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var currentPid)
                || currentPid != processId) throw Rejected();
        }

        public void Dispose() => process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref int length, StringBuilder? family);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
