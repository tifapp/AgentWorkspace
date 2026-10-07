using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentOS.Core;

// The only live handle is owned by the runtime, never inherited by the host.
// Closing it on cancellation or runtime death kills ordinary descendants.
internal sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    public WindowsJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception();
        var info = new ExtendedLimit { BasicLimit = new BasicLimit { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception();
    }
    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain the Codex process tree. Host execution was not released.");
    }
    public void Stop() { if (!_handle.IsClosed) TerminateJobObject(_handle, 1); }
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperation, WriteOperation, OtherOperation, ReadTransfer, WriteTransfer, OtherTransfer; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit BasicLimit;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
