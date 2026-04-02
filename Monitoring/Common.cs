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

        static ProcessCollector()
        {
            EtwNetworkMonitor.Start();
        }

        internal static IReadOnlyList<ProcessSnapshot> GetTopProcesses(int maxCount = 50)
        {
            Process[] processes = Process.GetProcesses();
            var currentData = new List<ProcessSnapshot>(processes.Length);
            var now = DateTime.UtcNow;
            var activePids = new HashSet<int>(processes.Length);
            var ppidMap = GetParentPidMap();

            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;

                    activePids.Add(process.Id);

                    IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                    if (handle == IntPtr.Zero)
                        continue;

                    ulong kernelTime;
                    ulong userTime;
                    IO_COUNTERS ioCounters;

                    try
                    {
                        if (!GetProcessKernelUserTime(handle, out kernelTime, out userTime))
                            continue;

                        if (!GetProcessIoCounters(handle, out ioCounters))
                            continue;
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }

                    double cpuUsage = 0;
                    long readPerSec = 0;
                    long writePerSec = 0;
                    long netReceivedPerSec = 0;
                    long netSentPerSec = 0;

                    var netStats = EtwNetworkMonitor.GetProcessStats(process.Id);

                    if (previousProcesses.TryGetValue(process.Id, out PreviousProcess? previous))
                    {
                        var timeDelta = (now - previous.Time).TotalSeconds;
                        if (timeDelta > 0)
                        {
                            var processDelta = (kernelTime + userTime) - (previous.KernelTime + previous.UserTime);
                            cpuUsage = processDelta / (timeDelta * 10_000_000.0 * Environment.ProcessorCount) * 100.0;
                            cpuUsage = Math.Max(0, Math.Min(100, cpuUsage));

                            readPerSec = (long)((ioCounters.ReadTransferCount - previous.ReadBytes) / timeDelta);
                            writePerSec = (long)((ioCounters.WriteTransferCount - previous.WriteBytes) / timeDelta);

                            netReceivedPerSec = (long)((netStats.Received - previous.NetReadBytes) / timeDelta);
                            netSentPerSec = (long)((netStats.Sent - previous.NetWriteBytes) / timeDelta);
                        }
                    }

                    previousProcesses[process.Id] = new PreviousProcess
                    {
                        Time = now,
                        KernelTime = kernelTime,
                        UserTime = userTime,
                        ReadBytes = ioCounters.ReadTransferCount,
                        WriteBytes = ioCounters.WriteTransferCount,
                        NetReadBytes = netStats.Received,
                        NetWriteBytes = netStats.Sent
                    };

                    currentData.Add(new ProcessSnapshot
                    {
                        Name = process.ProcessName,
                        Pid = process.Id,
                        CpuUsage = cpuUsage,
                        MemoryUsage = (ulong)process.PrivateMemorySize64,
                        DiskReadBytesPerSec = readPerSec,
                        DiskWriteBytesPerSec = writePerSec,
                        NetReceivedBytesPerSec = Math.Max(0, netReceivedPerSec),
                        NetSentBytesPerSec = Math.Max(0, netSentPerSec)
                    });
                }
                catch
                {
                    // Process might have exited or access denied — skip it
                }
                finally
                {
                    process.Dispose();
                }
            }

            stalePidBuffer.Clear();
            foreach (var pid in previousProcesses.Keys)
            {
                if (!activePids.Contains(pid))
                    stalePidBuffer.Add(pid);
            }

            foreach (var pid in stalePidBuffer)
                previousProcesses.Remove(pid);

            var explorerPids = new HashSet<int>();
            foreach (var snapshot in currentData)
            {
                if (string.Equals(snapshot.Name, "explorer", StringComparison.OrdinalIgnoreCase))
                    explorerPids.Add(snapshot.Pid);
            }

            var groupedMap = new Dictionary<int, ProcessGroupAccumulator>(currentData.Count);
            foreach (var snapshot in currentData)
            {
                int rootPid = ResolveRootPid(snapshot.Pid, ppidMap, activePids);

                if (explorerPids.Contains(rootPid))
                {
                    rootPid = ResolveBranchRootUnderExplorer(snapshot.Pid, rootPid, ppidMap, activePids);
                }

                if (!groupedMap.TryGetValue(rootPid, out var group))
                {
                    group = new ProcessGroupAccumulator();
                    groupedMap[rootPid] = group;
                }

                group.CpuUsage += snapshot.CpuUsage;
                group.MemoryUsage += (long)snapshot.MemoryUsage;
                group.DiskReadBytesPerSec += snapshot.DiskReadBytesPerSec;
                group.DiskWriteBytesPerSec += snapshot.DiskWriteBytesPerSec;
                group.NetReceivedBytesPerSec += snapshot.NetReceivedBytesPerSec;
                group.NetSentBytesPerSec += snapshot.NetSentBytesPerSec;
                group.Members.Add(snapshot);
            }

            var groupedData = new List<ProcessSnapshot>(groupedMap.Count);
            foreach (var kvp in groupedMap)
            {
                int rootPid = kvp.Key;
                var group = kvp.Value;

                ProcessSnapshot root = group.Members[0];
                for (int i = 0; i < group.Members.Count; i++)
                {
                    if (group.Members[i].Pid == rootPid)
                    {
                        root = group.Members[i];
                        break;
                    }
                }

                var children = new List<ProcessSnapshot>(Math.Max(0, group.Members.Count - 1));
                for (int i = 0; i < group.Members.Count; i++)
                {
                    var member = group.Members[i];
                    if (member.Pid != root.Pid)
                        children.Add(member);
                }

                groupedData.Add(new ProcessSnapshot
                {
                    Name = root.Name,
                    Pid = root.Pid,
                    CpuUsage = group.CpuUsage,
                    MemoryUsage = (ulong)group.MemoryUsage,
                    DiskReadBytesPerSec = group.DiskReadBytesPerSec,
                    DiskWriteBytesPerSec = group.DiskWriteBytesPerSec,
                    NetReceivedBytesPerSec = group.NetReceivedBytesPerSec,
                    NetSentBytesPerSec = group.NetSentBytesPerSec,
                    Children = children
                });
            }

            groupedData.Sort((a, b) => b.MemoryUsage.CompareTo(a.MemoryUsage));

            if (maxCount > 0 && groupedData.Count > maxCount)
            {
                return groupedData.GetRange(0, maxCount);
            }

            return groupedData;
        }

        public static async Task RunAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("Collecting process data...");
            Console.CursorVisible = false;
            Console.Clear();

            var previousSystemCpuTimes = GetSystemCPUTimes();
            var previousNetworkUsage = GetNetworkUsage();
            var frameLines = new List<string>(64);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var currentData = await Task.Run(() => GetTopProcesses(50), cancellationToken);

                    var currentSystemCpuTimes = GetSystemCPUTimes();
                    double totalCpuUsage = CalculateTotalCpuPercent(previousSystemCpuTimes, currentSystemCpuTimes);
                    previousSystemCpuTimes = currentSystemCpuTimes;

                    var currentNetworkUsage = GetNetworkUsage();
                    long networkReceivedPerSec = currentNetworkUsage.BytesReceived - previousNetworkUsage.BytesReceived;
                    long networkSentPerSec = currentNetworkUsage.BytesSent - previousNetworkUsage.BytesSent;
                    previousNetworkUsage = currentNetworkUsage;

                    var (totalMem, availMem) = GetMemoryUsage();

                    frameLines.Clear();
                    frameLines.Add($"Total CPU Usage: {totalCpuUsage:F1}%   Total Memory: {FormatBytes((ulong)totalMem * 1024 * 1024)}   Available Memory: {FormatBytes((ulong)availMem * 1024 * 1024)}");
                    frameLines.Add($"Network Download: {FormatBytes((ulong)Math.Max(0, networkReceivedPerSec))}/s   Network Upload: {FormatBytes((ulong)Math.Max(0, networkSentPerSec))}/s");

                    var gpuInfo = TryQueryGpuInfo();
                    if (gpuInfo is not null)
                    {
                        frameLines.Add(FormatGpuSummary(gpuInfo));
                    }

                    frameLines.Add($"{"PID",6} {"Name",-25} {"CPU %",6} {"Memory",10} {"Disk Read/s",12} {"Disk Write/s",13} {"Net RX MB/s",11} {"Net TX MB/s",11}");

                    foreach (var p in currentData)
                     {
                        frameLines.Add($"{p.Pid,6} {p.Name,-25} {p.CpuUsage,6:F1} {FormatBytes(p.MemoryUsage),10} {FormatBytes((ulong)p.DiskReadBytesPerSec),12} {FormatBytes((ulong)p.DiskWriteBytesPerSec),13} {p.NetReceivedBytesPerSec / 1048576.0,11:F2} {p.NetSentBytesPerSec / 1048576.0,11:F2}");
                     }

                     RenderFrame(frameLines);

                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Clean shutdown— restore cursor and exit gracefully
                Console.CursorVisible = true;
            }
        }

        static void RenderFrame(IReadOnlyList<string> lines)
        {
            lock (Console.Out)
            {
                int width = Math.Max(1, Console.WindowWidth);
                int height = Math.Max(1, Console.WindowHeight);
                int lineCount = Math.Min(lines.Count, height);
                for (int i = 0; i < lineCount; i++)
                {
                    string line = lines[i];
                    if (line.Length > width)
                        line = line[..width];

                    string renderedLine = line.PadRight(width);
                    if (i >= lastRenderedLines.Count || !string.Equals(lastRenderedLines[i], renderedLine, StringComparison.Ordinal))
                    {
                        Console.SetCursorPosition(0, i);
                        Console.Write(renderedLine);
                    }

                    if (i < lastRenderedLines.Count)
                        lastRenderedLines[i] = renderedLine;
                    else
                        lastRenderedLines.Add(renderedLine);
                }

                int clearFrom = lineCount;
                int clearTo = Math.Min(lastRenderedLines.Count, height);
                for (int i = clearFrom; i < clearTo; i++)
                {
                    Console.SetCursorPosition(0, i);
                    Console.Write(new string(' ', width));
                }

                if (lastRenderedLines.Count > lineCount)
                {
                    lastRenderedLines.RemoveRange(lineCount, lastRenderedLines.Count - lineCount);
                }
            }
        }

        static Dictionary<int, int> GetParentPidMap()
        {
            var now = DateTime.UtcNow;

            lock (ppidMapLock)
            {
                if ((now - lastPpidRefreshUtc) < ppidRefreshInterval && cachedPpidMap.Count > 0)
                {
                    return cachedPpidMap;
                }

                var ppidMap = new Dictionary<int, int>();
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot != IntPtr.Zero && snapshot != (IntPtr)(-1))
                {
                    PROCESSENTRY32 pe32 = new PROCESSENTRY32();
                    pe32.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                    if (Process32First(snapshot, ref pe32))
                    {
                        do
                        {
                            ppidMap[(int)pe32.th32ProcessID] = (int)pe32.th32ParentProcessID;
                        } while (Process32Next(snapshot, ref pe32));
                    }

                    CloseHandle(snapshot);
                }

                cachedPpidMap = ppidMap;
                lastPpidRefreshUtc = now;
                return cachedPpidMap;
            }
        }

        static int ResolveRootPid(int pid, Dictionary<int, int> ppidMap, HashSet<int> activePids)
        {
            int current = pid;
            int hopCount = 0;

            while (
                hopCount++ < 64 &&
                ppidMap.TryGetValue(current, out int parentPid) &&
                activePids.Contains(parentPid) &&
                parentPid != current)
            {
                current = parentPid;
            }

            return current;
        }

        static int ResolveBranchRootUnderExplorer(int pid, int explorerPid, Dictionary<int, int> ppidMap, HashSet<int> activePids)
        {
            int current = pid;
            int hopCount = 0;

            while (
                hopCount++ < 64 &&
                ppidMap.TryGetValue(current, out int parentPid) &&
                activePids.Contains(parentPid) &&
                parentPid != current)
            {
                if (parentPid == explorerPid)
                    return current;

                current = parentPid;
            }

            return pid;
        }

        internal static (ulong idleTime, ulong kernelTime, ulong userTime) GetSystemCPUTimes()
        {
            GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);
            return (FileTimeToUInt64(idleTime), FileTimeToUInt64(kernelTime), FileTimeToUInt64(userTime));
        }

        static ulong FileTimeToUInt64(FILETIME fileTime)
        {
            ulong v = (ulong)fileTime.dwHighDateTime << 32;
            return v | fileTime.dwLowDateTime;
        }

        internal static double CalculateTotalCpuPercent(
            (ulong idle, ulong kernel, ulong user) previous,
            (ulong idle, ulong kernel, ulong user) current)
        {
            ulong idleDelta = current.idle - previous.idle;
            ulong totalDelta = (current.kernel - previous.kernel) + (current.user - previous.user);

            if (totalDelta == 0) return 0;
            return Math.Round(100.0 - (idleDelta / (double)totalDelta * 100.0), 1);
        }

        static bool GetProcessKernelUserTime(IntPtr handle, out ulong kernelTime, out ulong userTime)
        {
            kernelTime = 0;
            userTime = 0;

            if (!GetProcessTimes(handle, out _, out _, out FILETIME kernelTimeFt, out FILETIME userTimeFt))
                return false;

            kernelTime = FileTimeToUInt64(kernelTimeFt);
            userTime = FileTimeToUInt64(userTimeFt);
            return true;
        }

        internal static (long TotalMB, long AvailableMB) GetMemoryUsage()
        {
            MEMORYSTATUSEX memStatus = new();
            memStatus.Init();
            GlobalMemoryStatusEx(ref memStatus);
            return ((long)(memStatus.ullTotalPhys / (1024 * 1024)), (long)(memStatus.ullAvailPhys / (1024 * 1024)));
        }

        static PerformanceCounter? cpuPerformanceCounter;
        static uint? baseCpuFrequency;

        static uint GetBaseCpuFrequency()
        {
            if (baseCpuFrequency.HasValue) return baseCpuFrequency.Value;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("~MHz") is int mhz)
                {
                    baseCpuFrequency = (uint)mhz;
                    return baseCpuFrequency.Value;
                }
            }
            catch { }
            
            baseCpuFrequency = 0;
            return 0;
        }

        internal static uint GetCurrentCpuFrequencyMhz()
        {
            try
            {
                uint baseFreq = GetBaseCpuFrequency();
                if (baseFreq == 0) return 0;

                cpuPerformanceCounter ??= new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                float performancePercent = cpuPerformanceCounter.NextValue();
                
                return (uint)(baseFreq * (performancePercent / 100f));
            }
            catch
            {
                return 0;
            }
        }

        internal static (long BytesReceived, long BytesSent) GetNetworkUsage()
        {
            long bytesReceived = 0;
            long bytesSent = 0;

            if (NetworkInterface.GetIsNetworkAvailable())
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        var stats = ni.GetIPStatistics();
                        bytesReceived += stats.BytesReceived;
                        bytesSent += stats.BytesSent;
                    }
                }
            }

            return (bytesReceived, bytesSent);
        }

        static string FormatBytes(ulong bytes) => bytes switch
        {
            >= 1_000_000_000_000 => $"{bytes / 1_000_000_000_000.0:F2} TB",
            >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F2} GB",
            >= 1_000_000 => $"{bytes / 1_000_000.0:F2} MB",
            >= 1_000 => $"{bytes / 1_000.0:F2} KB",
            _ => $"{bytes} B"
        };
    }
}