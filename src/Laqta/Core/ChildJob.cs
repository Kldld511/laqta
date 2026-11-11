using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Laqta.Core;

/// <summary>
/// Ties child processes (FFmpeg) to this process's lifetime: if Laqta crashes or is killed,
/// Windows closes the job handle and terminates the children, so nothing keeps recording in the background.
/// </summary>
internal static class ChildJob
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong A, B, C, D, E, F; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static readonly IntPtr Job = Create();

    private static IntPtr Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return job;
        var info = new ExtendedLimits();
        info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, Marshal.SizeOf<ExtendedLimits>());
        return job;
    }

    public static void Adopt(Process process)
    {
        try { if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, process.Handle); } catch { }
    }
}
