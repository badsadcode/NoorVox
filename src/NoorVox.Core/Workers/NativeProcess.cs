using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NoorVox.Core.Workers;

/// <summary>
/// Win32 process launch for workers. <see cref="System.Diagnostics.Process.Start()"/> is not used because it creates
/// the child with handle inheritance enabled: a long-lived worker would then keep the caller's stdout pipe open, and
/// an agent running <c>noorvox workers start --json</c> through a pipe would never see EOF (hang).
/// Here: <c>bInheritHandles = FALSE</c>, hidden console, own process group, token passed via environment block.
/// </summary>
internal static class NativeProcess
{
    const uint CREATE_SUSPENDED = 0x00000004;
    const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const int ERROR_ACCESS_DENIED = 5;

    /// <param name="breakawayFromJob">Try to leave the caller's job (e.g. an agent sandbox that kills its job on exit) so a persistent worker survives. Falls back silently when not permitted.</param>
    /// <param name="job">If set, the worker is assigned to this job before it runs a single instruction.</param>
    public static (int Pid, bool BrokeAway) Start(string exePath, string arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> extraEnvironment, bool breakawayFromJob, JobObject? job)
    {
        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");
        var env = BuildEnvironmentBlock(extraEnvironment);
        uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | (job is not null ? CREATE_SUSPENDED : 0);

        bool brokeAway = breakawayFromJob;
        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags | (brokeAway ? CREATE_BREAKAWAY_FROM_JOB : 0),
                env, workingDirectory, ref si, out var pi))
        {
            int err = Marshal.GetLastWin32Error();
            if (!(brokeAway && err == ERROR_ACCESS_DENIED)) throw new Win32Exception(err, $"CreateProcess failed for {exePath}");
            brokeAway = false; // job does not allow breakaway; run inside it
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, env, workingDirectory, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for {exePath}");
        }

        try
        {
            if (job is not null)
            {
                if (!job.Assign(pi.hProcess))
                {
                    int err = Marshal.GetLastWin32Error();
                    TerminateProcess(pi.hProcess, 1);
                    throw new Win32Exception(err, "AssignProcessToJobObject failed");
                }
                ResumeThread(pi.hThread);
            }
            return (pi.dwProcessId, brokeAway);
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (k, v) in extra) vars[k] = v;
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, string lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr hThread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE: when the owning NoorVox process exits — even by crash or Task Manager —
/// the OS terminates every worker assigned to it. Used for workers bound to their launcher (GUI default, CLI one-shot synth).
/// </summary>
public sealed class JobObject : IDisposable
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    IntPtr _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        int size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    internal bool Assign(IntPtr processHandle) => AssignProcessToJobObject(_handle, processHandle);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { NativeProcess.CloseHandle(_handle); _handle = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
