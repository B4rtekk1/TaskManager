using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskManagerUI.Monitoring
{
    partial class ProcessCollector
    {
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

                    var (Received, Sent) = EtwNetworkMonitor.GetProcessStats(process.Id);

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

                            netReceivedPerSec = (long)((Received - previous.NetReadBytes) / timeDelta);
                            netSentPerSec = (long)((Sent - previous.NetWriteBytes) / timeDelta);
                        }
                    }

                    previousProcesses[process.Id] = new PreviousProcess
                    {
                        Time = now,
                        KernelTime = kernelTime,
                        UserTime = userTime,
                        ReadBytes = ioCounters.ReadTransferCount,
                        WriteBytes = ioCounters.WriteTransferCount,
                        NetReadBytes = Received,
                        NetWriteBytes = Sent
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
                    PROCESSENTRY32 pe32 = new()
                    {
                        dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
                    };
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
    }
}
