using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace TaskManagerUI.Monitoring
{
    partial class ProcessCollector
    {
        internal static GPUData? TryQueryGpuInfo(uint gpuIndex = 0)
        {
            var (integratedPids, integratedGpuName) = QueryIntegratedGPUProcesses();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = $"--query-gpu=name,memory.total,memory.used,memory.free,utilization.gpu,utilization.memory,temperature.gpu --format=csv,noheader,nounits -i {gpuIndex}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process is null)
                    return new GPUData { IntegratedProcessIDs = integratedPids, IntegratedGpuName = integratedGpuName };

                string line = process.StandardOutput.ReadLine() ?? string.Empty;
                process.WaitForExit();

                var parts = line.Split(',');
                if (parts.Length < 7)
                    return new GPUData { IntegratedProcessIDs = integratedPids, IntegratedGpuName = integratedGpuName };

                var pids = QueryGPUProcesses(gpuIndex);

                // Assuming integrated pids shouldn't include dedicated pids
                integratedPids.RemoveAll(pids.Contains);

                return new GPUData
                {
                    Name = parts[0].Trim(),
                    MemoryTotalMB = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                    MemoryUsedMB = int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
                    MemoryFreeMB = int.Parse(parts[3].Trim(), CultureInfo.InvariantCulture),
                    UtilizationGPU = float.Parse(parts[4].Trim(), CultureInfo.InvariantCulture),
                    UtilizationMemory = float.Parse(parts[5].Trim(), CultureInfo.InvariantCulture),
                    TemperatureC = float.Parse(parts[6].Trim(), CultureInfo.InvariantCulture),
                    ProcessIDs = pids,
                    IntegratedProcessIDs = integratedPids,
                    IntegratedGpuName = integratedGpuName
                };
            }
            catch
            {
                return new GPUData { IntegratedProcessIDs = integratedPids, IntegratedGpuName = integratedGpuName };
            }
        }

        internal static string FormatGpuSummary(GPUData gpu)
        {
            return $"GPU: {gpu.Name} | {gpu.UtilizationGPU:F0}% / {gpu.UtilizationMemory:F0}% | {gpu.MemoryUsedMB}/{gpu.MemoryTotalMB} MB | {gpu.TemperatureC:F0} C | PIDs: {gpu.ProcessIDs.Count}, Integrated PIDs: {gpu.IntegratedProcessIDs.Count}";
        }

        static List<int> QueryGPUProcesses(uint gpuIndex = 0)
        {
            var pids = new List<int>();
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = $"--query-compute-apps=pid --format=csv,noheader,nounits -i {gpuIndex}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process is null)
                    return pids;

                string? line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                    {
                        pids.Add(pid);
                    }
                }

                process.WaitForExit();
            }
            catch
            {
                // nvidia-smi might not be available or no processes running
            }

            return pids;
        }

        internal static (List<int>, string) QueryIntegratedGPUProcesses()
        {
            var pids = new List<int>();
            string integratedGpuName = "Integrated GPU";

            string[] instanceNames = Array.Empty<string>();
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                instanceNames = category.GetInstanceNames();

                var adapterCategory = new PerformanceCounterCategory("GPU Adapter Memory");
                var adapterInstances = adapterCategory.GetInstanceNames();
                var dedicatedLuids = new HashSet<string>();
            }
            catch { }

            try
            {
                var pidSet = new HashSet<int>();

                var tempCounters = new List<(int pid, PerformanceCounter counter)>();

                foreach (var instance in instanceNames)
                {
                    if (instance.Contains("pid_"))
                    {
                        var parts = instance.Split('_');
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i] == "pid" && i + 1 < parts.Length)
                            {
                                if (int.TryParse(parts[i + 1], out int pid) && !pidSet.Contains(pid))
                                {
                                    try
                                    {
                                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance);
                                        counter.NextValue();
                                        tempCounters.Add((pid, counter));
                                        pidSet.Add(pid);
                                    }
                                    catch { }
                                }
                                break;
                            }
                        }
                    }
                }

                if (tempCounters.Count > 0)
                {
                    System.Threading.Thread.Sleep(50);
                    foreach (var tc in tempCounters)
                    {
                        try
                        {
                            if (tc.counter.NextValue() > 0)
                            {
                                pids.Add(tc.pid);
                            }
                        }
                        catch { }
                        finally
                        {
                            tc.counter.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // GPU Engine performance counters might not be available
            }

            return (pids, integratedGpuName);
        }

        internal sealed record GPUData
        {
            public string Name { get; init; } = string.Empty;
            public int MemoryTotalMB { get; init; }
            public int MemoryUsedMB { get; init; }
            public int MemoryFreeMB { get; init; }
            public float UtilizationGPU { get; init; }
            public float UtilizationMemory { get; init; }
            public float TemperatureC { get; init; }
            public List<int> ProcessIDs { get; init; } = new();
            public List<int> IntegratedProcessIDs { get; init; } = new();
            public string IntegratedGpuName { get; init; } = string.Empty;
        }
    }
}