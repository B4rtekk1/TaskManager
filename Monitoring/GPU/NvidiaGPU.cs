using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;

namespace TaskManagerUI.Monitoring
{
    partial class ProcessCollector
    {
        internal static GPUData? TryQueryGpuInfo(uint gpuIndex = 0)
        {
            var (integratedPids, integratedGpuName, integratedGpuTemperatureC) = QueryIntegratedGPUProcesses();

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
                    return new GPUData
                    {
                        IntegratedProcessIDs = integratedPids,
                        IntegratedGpuName = integratedGpuName,
                        IntegratedTemperatureC = integratedGpuTemperatureC
                    };

                string line = process.StandardOutput.ReadLine() ?? string.Empty;
                process.WaitForExit();

                var parts = line.Split(',');
                if (parts.Length < 7)
                    return new GPUData
                    {
                        IntegratedProcessIDs = integratedPids,
                        IntegratedGpuName = integratedGpuName,
                        IntegratedTemperatureC = integratedGpuTemperatureC
                    };

                var pids = QueryGPUProcesses(gpuIndex);
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
                    IntegratedGpuName = integratedGpuName,
                    IntegratedTemperatureC = integratedGpuTemperatureC
                };
            }
            catch
            {
                return new GPUData
                {
                    IntegratedProcessIDs = integratedPids,
                    IntegratedGpuName = integratedGpuName,
                    IntegratedTemperatureC = integratedGpuTemperatureC
                };
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
            }

            return pids;
        }

        internal static (List<int>, string, float?) QueryIntegratedGPUProcesses()
        {
            var pids = new List<int>();
            string integratedGpuName = TryGetIntegratedGpuName();
            float? integratedGpuTemperatureC = TryGetIntegratedGpuTemperatureC();

            string[] instanceNames = Array.Empty<string>();
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                instanceNames = category.GetInstanceNames();
            }
            catch
            {
            }

            try
            {
                var pidSet = new HashSet<int>();
                var tempCounters = new List<(int pid, PerformanceCounter counter)>();

                foreach (var instance in instanceNames)
                {
                    if (!instance.Contains("pid_", StringComparison.OrdinalIgnoreCase))
                        continue;

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
                                catch
                                {
                                }
                            }

                            break;
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
                        catch
                        {
                        }
                        finally
                        {
                            tc.counter.Dispose();
                        }
                    }
                }
            }
            catch
            {
            }

            return (pids, integratedGpuName, integratedGpuTemperatureC);
        }

        static string? _cachedIntegratedGpuName = null;

        static string TryGetIntegratedGpuName()
        {
            if (_cachedIntegratedGpuName != null)
                return _cachedIntegratedGpuName;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name,AdapterDACType,VideoProcessor,PNPDeviceID FROM Win32_VideoController");

                foreach (ManagementObject gpu in searcher.Get())
                {
                    string name = (gpu["Name"]?.ToString() ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    string pnpId = (gpu["PNPDeviceID"]?.ToString() ?? string.Empty).ToUpperInvariant();
                    string adapterDacType = (gpu["AdapterDACType"]?.ToString() ?? string.Empty).ToLowerInvariant();
                    string videoProcessor = (gpu["VideoProcessor"]?.ToString() ?? string.Empty).ToLowerInvariant();
                    string lname = name.ToLowerInvariant();

                    bool likelyIntegrated =
                        pnpId.Contains("VEN_8086") ||
                        lname.Contains("intel") ||
                        lname.Contains("radeon(tm) graphics") ||
                        lname.Contains("vega") ||
                        adapterDacType.Contains("internal") ||
                        videoProcessor.Contains("integrated");

                    bool likelyDedicated =
                        pnpId.Contains("VEN_10DE") ||
                        lname.Contains("nvidia") ||
                        (pnpId.Contains("VEN_1002") && !lname.Contains("radeon(tm) graphics"));

                    if (likelyIntegrated && !likelyDedicated)
                    {
                        _cachedIntegratedGpuName = name;
                        return name;
                    }
                }
            }
            catch
            {
            }

            _cachedIntegratedGpuName = "Integrated GPU";
            return _cachedIntegratedGpuName;
        }

        static int _temperatureMethod = 0;

        static float? TryGetIntegratedGpuTemperatureC()
        {
            if (_temperatureMethod == -1) return null;

            if (_temperatureMethod == 0 || _temperatureMethod == 1)
            {
                float? fromLibre = TryGetGpuTemperatureFromNamespace("root\\LibreHardwareMonitor");
                if (fromLibre.HasValue)
                {
                    _temperatureMethod = 1;
                    return fromLibre;
                }
            }

            if (_temperatureMethod == 0 || _temperatureMethod == 2)
            {
                float? fromOpen = TryGetGpuTemperatureFromNamespace("root\\OpenHardwareMonitor");
                if (fromOpen.HasValue)
                {
                    _temperatureMethod = 2;
                    return fromOpen;
                }
            }

            if (_temperatureMethod == 0 || _temperatureMethod == 3)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        float raw = Convert.ToSingle(obj["Temperature"]);
                        // Temperatury zwykle rzucane są w absolutnym Kelwinie lub 1/10 stopnia Kelwina
                        float temp = raw > 500 ? (raw / 10.0f) - 273.15f : raw - 273.15f;
                        if (temp > 0 && temp < 150)
                        {
                            _temperatureMethod = 3;
                            return temp;
                        }
                    }
                }
                catch
                {
                }
            }

            if (_temperatureMethod == 0 || _temperatureMethod == 4)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        float temp = (Convert.ToSingle(obj["CurrentTemperature"]) - 2732.0f) / 10.0f;
                        if (temp > 0 && temp < 150)
                        {
                            _temperatureMethod = 4;
                            return temp;
                        }
                    }
                }
                catch
                {
                }
            }

            if (_temperatureMethod == 0)
                _temperatureMethod = -1;

            return null;
        }

        static float? TryGetGpuTemperatureFromNamespace(string scope)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, "SELECT Name,SensorType,Value FROM Sensor");
                var candidates = new List<(string Name, float Value)>();

                foreach (ManagementObject sensor in searcher.Get())
                {
                    string sensorType = sensor["SensorType"]?.ToString() ?? string.Empty;
                    if (!string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = sensor["Name"]?.ToString() ?? string.Empty;
                    string lname = name.ToLowerInvariant();
                    if (!(lname.Contains("gpu") || lname.Contains("graphics") || lname.Contains("igpu")))
                        continue;

                    if (sensor["Value"] is float f)
                    {
                        candidates.Add((name, f));
                    }
                    else if (float.TryParse(sensor["Value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        candidates.Add((name, parsed));
                    }
                }

                var preferred = candidates.FirstOrDefault(c =>
                    c.Name.Contains("intel", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Contains("igpu", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Contains("graphics", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(preferred.Name))
                    return preferred.Value;

                if (candidates.Count > 0)
                    return candidates[0].Value;
            }
            catch
            {
            }

            return null;
        }

        internal sealed record GPUData
        {
            public string Name { get; init; } = string.Empty;
            public int MemoryTotalMB { get; init; }
            public int MemoryUsedMB { get;   init; }
            public int MemoryFreeMB { get; init; }
            public float UtilizationGPU { get; init; }
            public float UtilizationMemory { get; init; }
            public float TemperatureC { get; init; }
            public List<int> ProcessIDs { get; init; } = new();
            public List<int> IntegratedProcessIDs { get; init; } = new();
            public string IntegratedGpuName { get; init; } = string.Empty;
            public float? IntegratedTemperatureC { get; init; }
        }
    }
}