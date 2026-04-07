using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;

namespace TaskManagerUI.Monitoring
{
    partial class ProcessCollector
    {
        internal static (long TotalMB, long AvailableMB) GetMemoryUsage()
        {
            MEMORYSTATUSEX memStatus = new();
            memStatus.Init();
            GlobalMemoryStatusEx(ref memStatus);
            return ((long)(memStatus.ullTotalPhys / (1024 * 1024)), (long)(memStatus.ullAvailPhys / (1024 * 1024)));
        }

        static uint? _ramFrequency;

        internal static uint? GetRamFrequencyMhz()
        {
            if (_ramFrequency.HasValue) return _ramFrequency;
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Speed FROM Win32_PhysicalMemory");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    if (obj["Speed"] != null && uint.TryParse(obj["Speed"].ToString(), out uint speed))
                    {
                        _ramFrequency = speed;
                        return speed;
                    }
                }
            }
            catch { }
            _ramFrequency = 0;
            return 0;
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
