using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IDVBuff.Features.Maps;

internal static partial class MapGpuTrainingSidecar
{
    private sealed class SidecarJob : IDisposable
    {
        private const uint KillOnJobClose = 0x00002000;
        private const int ExtendedLimitInformation = 9;
        private readonly SafeFileHandle _handle;

        private SidecarJob(SafeFileHandle handle)
        {
            _handle = handle;
        }

        public static SidecarJob Create()
        {
            var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "无法创建 GPU sidecar 防泄漏 Job Object。");
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = KillOnJobClose
                }
            };
            if (!NativeMethods.SetInformationJobObject(handle,
                ExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error,
                    "无法设置 GPU sidecar 退出防护。");
            }
            return new SidecarJob(handle);
        }

        public void Assign(Process process)
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle,
                process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "无法把 GPU sidecar 加入退出防护 Job Object。");
        }

        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static partial class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateJobObject(
            IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job, int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            SafeFileHandle job, SafeProcessHandle process);
    }
}
