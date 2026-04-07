using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace TaskManagerUI.Monitoring
{
    partial class ProcessCollector
    {
        static PerformanceCounter? cpuPerformanceCounter;
        static PerformanceCounter? contextSwitchesCounter;
        static PerformanceCounter? interruptsCounter;
        static PerformanceCounter? syscallsCounter;
        static PerformanceCounter? processorQueueLengthCounter;
        static PerformanceCounter? cpuUserTimeCounter;
        static PerformanceCounter? cpuPrivilegedTimeCounter;
        static PerformanceCounter? cpuDpcTimeCounter;
        static PerformanceCounter? cpuInterruptTimePercentCounter;
        static PerformanceCounter? cpuIdleTimeCounter;
        static uint? baseCpuFrequency;
        static string? processorName;
        static string? processorCacheSummary;
        static string? processorTopologySummary;

        internal static string GetProcessorName()
        {
            if (!string.IsNullOrWhiteSpace(processorName))
                return processorName;

            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    var name = obj["Name"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        processorName = name;
                        return processorName;
                    }
                }
            }
            catch
            {
            }

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    processorName = name;
                    return processorName;
                }
            }
            catch
            {
            }

            processorName = "N/A";
            return processorName;
        }

        internal static string GetProcessorCacheSummary()
        {
            if (!string.IsNullOrWhiteSpace(processorCacheSummary))
                return processorCacheSummary;

            ulong l1CacheKb = 0;
            ulong l2CacheKb = 0;
            ulong l3CacheKb = 0;

            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Level, InstalledSize, MaxCacheSize FROM Win32_CacheMemory");
                foreach (ManagementObject cache in searcher.Get().Cast<ManagementObject>())
                {
                    int level = TryGetIntValue(cache["Level"]);
                    ulong sizeKb = TryGetUlongValue(cache["InstalledSize"]);
                    if (sizeKb == 0)
                        sizeKb = TryGetUlongValue(cache["MaxCacheSize"]);

                    if (sizeKb == 0)
                        continue;

                    if (level == 3)
                        l1CacheKb += sizeKb;
                    else if (level == 4)
                        l2CacheKb += sizeKb;
                    else if (level == 5)
                        l3CacheKb += sizeKb;
                }
            }
            catch
            {
            }

            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT L2CacheSize, L3CacheSize FROM Win32_Processor");
                foreach (ManagementObject cpu in searcher.Get().Cast<ManagementObject>())
                {
                    if (l2CacheKb == 0)
                        l2CacheKb = TryGetUlongValue(cpu["L2CacheSize"]);
                    if (l3CacheKb == 0)
                        l3CacheKb = TryGetUlongValue(cpu["L3CacheSize"]);
                }
            }
            catch
            {
            }

            string l1 = l1CacheKb > 0 ? FormatCacheSize(l1CacheKb) : "N/A";
            string l2 = l2CacheKb > 0 ? FormatCacheSize(l2CacheKb) : "N/A";
            string l3 = l3CacheKb > 0 ? FormatCacheSize(l3CacheKb) : "N/A";

            processorCacheSummary = $"Cache L1: {l1} | L2: {l2} | L3: {l3}";
            return processorCacheSummary;
        }

        internal static string GetProcessorTopologySummary()
        {
            if (!string.IsNullOrWhiteSpace(processorTopologySummary))
                return processorTopologySummary;

            int sockets = 0;
            int cores = 0;
            int logicalProcessors = 0;

            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                foreach (ManagementObject cpu in searcher.Get().Cast<ManagementObject>())
                {
                    sockets++;
                    cores += TryGetIntValue(cpu["NumberOfCores"]);
                    logicalProcessors += TryGetIntValue(cpu["NumberOfLogicalProcessors"]);
                }
            }
            catch
            {
            }

            string socketsText = sockets > 0 ? sockets.ToString() : "N/A";
            string coresText = cores > 0 ? cores.ToString() : "N/A";
            string logicalProcessorsText = logicalProcessors > 0 ? logicalProcessors.ToString() : "N/A";

            processorTopologySummary = $"Sockets: {socketsText} | Cores: {coresText} | Logical processors: {logicalProcessorsText}";
            return processorTopologySummary;
        }

        static int TryGetIntValue(object? value)
        {
            if (value == null)
                return 0;

            if (value is int i)
                return i;

            return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
        }

        static ulong TryGetUlongValue(object? value)
        {
            if (value == null)
                return 0;

            if (value is ulong u)
                return u;

            if (value is uint ui)
                return ui;

            if (value is long l && l > 0)
                return (ulong)l;

            if (value is int i && i > 0)
                return (ulong)i;

            return ulong.TryParse(value.ToString(), out var parsed) ? parsed : 0;
        }

        static string FormatCacheSize(ulong sizeKb)
            => sizeKb >= 1024 ? $"{sizeKb / 1024.0:F1} MB" : $"{sizeKb} KB";

        internal static float GetSystemInterruptsPerSec()
        {
            try
            {
                interruptsCounter ??= new PerformanceCounter("Processor Information", "Interrupts/sec", "_Total");
                return interruptsCounter.NextValue();
            }
            catch
            {
                return 0;
            }
        }

        internal static float GetSystemSyscallsPerSec()
        {
            try
            {
                syscallsCounter ??= new PerformanceCounter("System", "System Calls/sec");
                return syscallsCounter.NextValue();
            }
            catch
            {
                return 0;
            }
        }

        internal static float GetSystemContextSwitchesPerSec()
        {
            try
            {
                contextSwitchesCounter ??= new PerformanceCounter("System", "Context Switches/sec");
                return contextSwitchesCounter.NextValue();
            }
            catch
            {
                return 0;
            }
        }

        internal static float GetProcessorQueueLength()
        {
            try
            {
                processorQueueLengthCounter ??= new PerformanceCounter("System", "Processor Queue Length");
                return Math.Max(0, processorQueueLengthCounter.NextValue());
            }
            catch
            {
                return 0;
            }
        }

        static PerformanceCounter CreateCpuTotalCounter(string counterName)
        {
            try
            {
                return new PerformanceCounter("Processor Information", counterName, "_Total");
            }
            catch
            {
                return new PerformanceCounter("Processor", counterName, "_Total");
            }
        }

        internal static (float userTime, float privilegedTime, float dpcTime, float interruptTime) GetCpuTimeBreakdownPercent()
        {
            try
            {
                cpuUserTimeCounter ??= CreateCpuTotalCounter("% User Time");
                cpuPrivilegedTimeCounter ??= CreateCpuTotalCounter("% Privileged Time");
                cpuDpcTimeCounter ??= CreateCpuTotalCounter("% DPC Time");
                cpuInterruptTimePercentCounter ??= CreateCpuTotalCounter("% Interrupt Time");

                float userTime = Math.Clamp(cpuUserTimeCounter.NextValue(), 0f, 100f);
                float privilegedTime = Math.Clamp(cpuPrivilegedTimeCounter.NextValue(), 0f, 100f);
                float dpcTime = Math.Clamp(cpuDpcTimeCounter.NextValue(), 0f, 100f);
                float interruptTime = Math.Clamp(cpuInterruptTimePercentCounter.NextValue(), 0f, 100f);

                return (userTime, privilegedTime, dpcTime, interruptTime);
            }
            catch
            {
                return (0, 0, 0, 0);
            }
        }

        internal static float GetCpuIdleTimePercent()
        {
            try
            {
                cpuIdleTimeCounter ??= CreateCpuTotalCounter("% Idle Time");
                return Math.Clamp(cpuIdleTimeCounter.NextValue(), 0f, 100f);
            }
            catch
            {
                return 0;
            }
        }

        internal static (int parkedLogicalProcessors, int totalLogicalProcessors) GetCpuParkingStatus()
        {
            EnsurePerCoreCountersInitialized();

            lock (perCoreCountersLock)
            {
                if (perCoreCounterInstances.Length == 0 || perCoreParkingCounters.Length != perCoreCounterInstances.Length)
                    return (0, 0);

                int parked = 0;
                int total = 0;

                for (int i = 0; i < perCoreParkingCounters.Length; i++)
                {
                    try
                    {
                        float parkingStatus = Math.Clamp(perCoreParkingCounters[i].NextValue(), 0f, 100f);
                        total++;
                        if (parkingStatus < 50f)
                            parked++;
                    }
                    catch
                    {
                    }
                }

                return (parked, total);
            }
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

        static void EnsurePerCoreCountersInitialized()
        {
            var now = DateTime.UtcNow;
            lock (perCoreCountersLock)
            {
                if (perCoreCounterInstances.Length > 0 && (now - lastPerCoreCounterRefreshUtc) < perCoreCounterRefreshInterval)
                    return;

                try
                {
                    var category = new PerformanceCounterCategory("Processor Information");
                    var instances = category.GetInstanceNames();
                    Array.Sort(instances, StringComparer.OrdinalIgnoreCase);

                    var filteredInstances = new List<string>(instances.Length);
                    foreach (var instance in instances)
                    {
                        if (!string.Equals(instance, "_Total", StringComparison.OrdinalIgnoreCase)
                            && !instance.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase))
                            filteredInstances.Add(instance);
                    }

                    if (filteredInstances.Count == 0)
                    {
                        perCoreCounterInstances = [];
                        perCoreUsageCounters = [];
                        perCoreFrequencyCounters = [];
                        perCoreParkingCounters = [];
                        lastPerCoreCounterRefreshUtc = now;
                        return;
                    }

                    string usageCounterName = category.CounterExists("% Processor Utility")
                        ? "% Processor Utility"
                        : "% Processor Time";

                    var usageCounters = new PerformanceCounter[filteredInstances.Count];
                    var frequencyCounters = new PerformanceCounter[filteredInstances.Count];
                    var parkingCounters = new PerformanceCounter[filteredInstances.Count];
                    bool hasParkingCounter = category.CounterExists("% Parking Status");
                    for (int i = 0; i < filteredInstances.Count; i++)
                    {
                        string instance = filteredInstances[i];
                        usageCounters[i] = new PerformanceCounter("Processor Information", usageCounterName, instance);
                        frequencyCounters[i] = new PerformanceCounter("Processor Information", "% Processor Performance", instance);

                        _ = usageCounters[i].NextValue();
                        _ = frequencyCounters[i].NextValue();

                        if (hasParkingCounter)
                        {
                            parkingCounters[i] = new PerformanceCounter("Processor Information", "% Parking Status", instance);
                            _ = parkingCounters[i].NextValue();
                        }
                    }

                    perCoreCounterInstances = filteredInstances.ToArray();
                    perCoreUsageCounters = usageCounters;
                    perCoreFrequencyCounters = frequencyCounters;
                    perCoreParkingCounters = hasParkingCounter ? parkingCounters : [];
                    lastPerCoreCounterRefreshUtc = now;
                }
                catch
                {
                    perCoreCounterInstances = [];
                    perCoreUsageCounters = [];
                    perCoreFrequencyCounters = [];
                    perCoreParkingCounters = [];
                    lastPerCoreCounterRefreshUtc = now;
                }
            }
        }

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

        internal static IReadOnlyList<CpuCoreSnapshot> GetPerCoreCpuStats()
        {
            EnsurePerCoreCountersInitialized();

            lock (perCoreCountersLock)
            {
                if (perCoreCounterInstances.Length == 0)
                    return Array.Empty<CpuCoreSnapshot>();

                uint baseFreq = GetBaseCpuFrequency();
                var result = new List<CpuCoreSnapshot>(perCoreCounterInstances.Length);

                for (int i = 0; i < perCoreCounterInstances.Length; i++)
                {
                    double usagePercent = 0;
                    uint frequencyMhz = 0;

                    try
                    {
                        usagePercent = Math.Clamp(perCoreUsageCounters[i].NextValue(), 0f, 100f);
                    }
                    catch
                    {
                        usagePercent = 0;
                    }

                    try
                    {
                        if (baseFreq > 0)
                        {
                            float performancePercent = perCoreFrequencyCounters[i].NextValue();
                            frequencyMhz = (uint)Math.Max(0, baseFreq * (performancePercent / 100f));
                        }
                    }
                    catch
                    {
                        frequencyMhz = 0;
                    }

                    result.Add(new CpuCoreSnapshot
                    {
                        CoreIndex = i,
                        CoreName = perCoreCounterInstances[i],
                        UsagePercent = usagePercent,
                        FrequencyMhz = frequencyMhz
                    });
                }

                return result;
            }
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

        static int _cpuTemperatureMethod = 0;

        internal static float? TryGetCpuTemperatureC()
        {
            if (_cpuTemperatureMethod == -1) return null;

            if (_cpuTemperatureMethod == 0 || _cpuTemperatureMethod == 1)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\LibreHardwareMonitor", "SELECT Name,SensorType,Value FROM Sensor");
                    foreach (ManagementObject sensor in searcher.Get().Cast<ManagementObject>())
                    {
                        string sensorType = sensor["SensorType"]?.ToString() ?? string.Empty;
                        if (!string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string name = sensor["Name"]?.ToString() ?? string.Empty;
                        string lname = name.ToLowerInvariant();
                        if (lname.Contains("cpu") || lname.Contains("core") || lname.Contains("package"))
                        {
                            if (sensor["Value"] is float f)
                            {
                                _cpuTemperatureMethod = 1;
                                return f;
                            }
                            if (float.TryParse(sensor["Value"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                            {
                                _cpuTemperatureMethod = 1;
                                return parsed;
                            }
                        }
                    }
                }
                catch { }
            }

            if (_cpuTemperatureMethod == 0 || _cpuTemperatureMethod == 2)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\OpenHardwareMonitor", "SELECT Name,SensorType,Value FROM Sensor");
                    foreach (ManagementObject sensor in searcher.Get().Cast<ManagementObject>())
                    {
                        string sensorType = sensor["SensorType"]?.ToString() ?? string.Empty;
                        if (!string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string name = sensor["Name"]?.ToString() ?? string.Empty;
                        string lname = name.ToLowerInvariant();
                        if (lname.Contains("cpu") || lname.Contains("core") || lname.Contains("package"))
                        {
                            if (sensor["Value"] is float f)
                            {
                                _cpuTemperatureMethod = 2;
                                return f;
                            }
                            if (float.TryParse(sensor["Value"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                            {
                                _cpuTemperatureMethod = 2;
                                return parsed;
                            }
                        }
                    }
                }
                catch { }
            }

            if (_cpuTemperatureMethod == 0 || _cpuTemperatureMethod == 3)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
                    foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                    {
                        float raw = Convert.ToSingle(obj["Temperature"]);
                        float temp = raw > 500 ? (raw / 10.0f) - 273.15f : raw - 273.15f;
                        if (temp > 0 && temp < 150)
                        {
                            _cpuTemperatureMethod = 3;
                            return temp;
                        }
                    }
                }
                catch { }
            }

            if (_cpuTemperatureMethod == 0 || _cpuTemperatureMethod == 4)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                    {
                        float temp = (Convert.ToSingle(obj["CurrentTemperature"]) - 2732.0f) / 10.0f;
                        if (temp > 0 && temp < 150)
                        {
                            _cpuTemperatureMethod = 4;
                            return temp;
                        }
                    }
                }
                catch { }
            }

            if (_cpuTemperatureMethod == 0)
                _cpuTemperatureMethod = -1;

            return null;
        }

        static string FormatPerCoreCpuLine(IReadOnlyList<CpuCoreSnapshot> cores)
        {
            var parts = new List<string>(cores.Count);
            for (int i = 0; i < cores.Count; i++)
            {
                var core = cores[i];
                string freq = core.FrequencyMhz > 0 ? $"{core.FrequencyMhz / 1000.0:F2}GHz" : "N/A";
                parts.Add($"C{i}:{core.UsagePercent:F0}%@{freq}");
            }

            return $"CPU per core: {string.Join(" | ", parts)}";
        }
    }
}
