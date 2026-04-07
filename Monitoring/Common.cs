using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManagerUI.Monitoring
{
    partial class ProcessCollector
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS ioCounters);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(IntPtr hProcess, out FILETIME lpCreationTime, out FILETIME lpExitTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public void Init()
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public sealed record ProcessSnapshot
        {
            public required string Name { get; init; }
            public required int Pid { get; init; }
            public required double CpuUsage { get; init; }
            public required ulong MemoryUsage { get; init; }
            public required long DiskReadBytesPerSec { get; init; }
            public required long DiskWriteBytesPerSec { get; init; }
            public required long NetReceivedBytesPerSec { get; init; }
            public required long NetSentBytesPerSec { get; init; }
            public IReadOnlyList<ProcessSnapshot> Children { get; init; } = Array.Empty<ProcessSnapshot>();
        }

        public sealed record CpuCoreSnapshot
        {
            public required int CoreIndex { get; init; }
            public required string CoreName { get; init; }
            public required double UsagePercent { get; init; }
            public required uint FrequencyMhz { get; init; }
        }

        class PreviousProcess
        {
            public DateTime Time;
            public ulong KernelTime;
            public ulong UserTime;
            public ulong ReadBytes;
            public ulong WriteBytes;
            public long NetReadBytes;
            public long NetWriteBytes;
        }

        sealed class ProcessGroupAccumulator
        {
            public double CpuUsage;
            public long MemoryUsage;
            public long DiskReadBytesPerSec;
            public long DiskWriteBytesPerSec;
            public long NetReceivedBytesPerSec;
            public long NetSentBytesPerSec;
            public readonly List<ProcessSnapshot> Members = [];
        }

        const int HeaderLineCount = 2;

        static readonly Dictionary<int, PreviousProcess> previousProcesses = [];
        static readonly object ppidMapLock = new();
        static readonly TimeSpan ppidRefreshInterval = TimeSpan.FromSeconds(5);
        static Dictionary<int, int> cachedPpidMap = [];
        static DateTime lastPpidRefreshUtc = DateTime.MinValue;
        static readonly List<string> lastRenderedLines = [];
        static readonly List<int> stalePidBuffer = [];
        static readonly object perCoreCountersLock = new();
        static readonly TimeSpan perCoreCounterRefreshInterval = TimeSpan.FromSeconds(10);
        static PerformanceCounter[] perCoreUsageCounters = [];
        static PerformanceCounter[] perCoreFrequencyCounters = [];
        static PerformanceCounter[] perCoreParkingCounters = [];
        static string[] perCoreCounterInstances = [];
        static DateTime lastPerCoreCounterRefreshUtc = DateTime.MinValue;

        static ProcessCollector()
        {
            EtwNetworkMonitor.Start();
        }

    }
}