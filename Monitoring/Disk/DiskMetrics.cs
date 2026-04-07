using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace TaskManagerUI.Monitoring.Disk
{
    internal class DiskMetrics
    {
        public string Name { get; private set; }
        public string Index { get; private set; } = string.Empty;
        public string DiskType { get; private set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public long TotalSize { get; private set; }
        public long FreeSpace { get; private set; }
        public float ReadSpeed { get; private set; }
        public float WriteSpeed { get; private set; }
        public float UsagePercentage { get; private set; }

        readonly private PerformanceCounter? _readCounter;
        readonly private PerformanceCounter? _writeCounter;
        readonly private PerformanceCounter? _timeCounter;

        public DiskMetrics(string driveName, string instanceBaseName)
        {
            Name = driveName;
            try
            {
                var driveInfo = new DriveInfo(driveName);
                if (driveInfo.IsReady)
                {
                    TotalSize = driveInfo.TotalSize;
                    FreeSpace = driveInfo.AvailableFreeSpace;
                    DiskType = driveInfo.DriveType.ToString();
                }

                _readCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instanceBaseName);
                _writeCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instanceBaseName);
                _timeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", instanceBaseName);

                _readCounter.NextValue();
                _writeCounter.NextValue();
                _timeCounter.NextValue();
            }
            catch
            {
            }
        }

        public void Update()
        {
            try
            {
                var driveInfo = new DriveInfo(Name);
                if (driveInfo.IsReady)
                {
                    TotalSize = driveInfo.TotalSize;
                    FreeSpace = driveInfo.AvailableFreeSpace;
                }

                if (_readCounter != null) ReadSpeed = _readCounter.NextValue();
                if (_writeCounter != null) WriteSpeed = _writeCounter.NextValue();
                if (_timeCounter != null) UsagePercentage = Math.Min(100f, _timeCounter.NextValue());
            }
            catch
            {
            }
        }

        public static List<DiskMetrics> GetAllDisks()
        {
            var disks = new List<DiskMetrics>();
            var diskModels = new Dictionary<string, string>();
            
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
                foreach (var item in searcher.Get())
                {
                    var index = item[nameof(Index)]?.ToString();
                    var model = item[nameof(Model)]?.ToString();
                    if (index != null && model != null)
                    {
                        diskModels[index] = model;
                    }
                }
            }
            catch
            {
            }

            try
            {
                var category = new PerformanceCounterCategory("PhysicalDisk");
                string[] instances = category.GetInstanceNames();

                foreach (string instance in instances)
                {
                    if (instance == "_Total") continue;

                    string diskIndex = instance.Split(' ').FirstOrDefault() ?? "";
                    string driveName = instance.Split(' ').LastOrDefault(s => s.Contains(':')) ?? "";
                    if (!string.IsNullOrEmpty(driveName))
                    {
                        var dm = new DiskMetrics(driveName + "\\", instance)
                        {
                            Index = diskIndex
                        };
                        if (diskModels.TryGetValue(diskIndex, out string? modelName))
                        {
                            dm.Model = modelName;
                        }
                        disks.Add(dm);
                    }
                }
            }
            catch
            {
            }
            return disks;
        }
    }
}

