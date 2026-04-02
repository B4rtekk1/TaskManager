using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace TaskManagerUI.Charts
{
    public class SystemPerformanceSnapshot
    {
        public DateTime Timestamp { get; set; }
        public double TotalCpuUsage { get; set; }
        public double TotalMemory { get; set; }
        public double AvailableMemory { get; set; }
        public double? GpuUtilization { get; set; }
        public double? GpuMemoryUsed { get; set; }
        public double? GpuTemperature { get; set; }
        public int TotalProcesses { get; set; }
    }

    internal class Databse
    {
        static internal void CreateDatabase(string filePath)
        {
            using var connection = new SqliteConnection($"Data Source={filePath}");
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText =
            @"
                CREATE TABLE IF NOT EXISTS SystemPerformanceData (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    TotalCpuUsage REAL NOT NULL,
                    TotalMemory REAL NOT NULL,
                    AvailableMemory REAL NOT NULL,
                    GpuUtilization REAL,
                    GpuMemoryUsed REAL,
                    GpuTemperature REAL,
                    TotalProcesses INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ProcessPerformanceData (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    Pid INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    CpuUsage REAL NOT NULL,
                    MemoryUsage REAL NOT NULL,
                    DiskReadBytesPerSec REAL NOT NULL,
                    DiskWriteBytesPerSec REAL NOT NULL
                );
            ";
            command.ExecuteNonQuery();
        }

        public static void SaveData(string filePath, double targetCpu, double totalMem, double availMem, Monitoring.ProcessCollector.GPUData? gpu, IEnumerable<Monitoring.ProcessCollector.ProcessSnapshot> processes)
        {
            using var connection = new SqliteConnection($"Data Source={filePath}");
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var sysCommand = connection.CreateCommand();
            sysCommand.CommandText = @"
                INSERT INTO SystemPerformanceData (TotalCpuUsage, TotalMemory, AvailableMemory, GpuUtilization, GpuMemoryUsed, GpuTemperature, TotalProcesses)
                VALUES ($Cpu, $TotalMem, $AvailMem, $GpuUtil, $GpuMem, $GpuTemp, $TotalProc);
            ";
            sysCommand.Parameters.AddWithValue("$Cpu", targetCpu);
            sysCommand.Parameters.AddWithValue("$TotalMem", totalMem);
            sysCommand.Parameters.AddWithValue("$AvailMem", availMem);
            sysCommand.Parameters.AddWithValue("$GpuUtil", gpu?.UtilizationGPU ?? (object)DBNull.Value);
            sysCommand.Parameters.AddWithValue("$GpuMem", gpu?.MemoryUsedMB ?? (object)DBNull.Value);
            sysCommand.Parameters.AddWithValue("$GpuTemp", gpu?.TemperatureC ?? (object)DBNull.Value);

            int totalProcs = 0;
            var processCommand = connection.CreateCommand();
            processCommand.CommandText = @"
                INSERT INTO ProcessPerformanceData (Pid, Name, CpuUsage, MemoryUsage, DiskReadBytesPerSec, DiskWriteBytesPerSec)
                VALUES ($Pid, $Name, $Cpu, $Mem, $DiskRead, $DiskWrite);
            ";
            
            var pPid = processCommand.Parameters.Add("$Pid", SqliteType.Integer);
            var pName = processCommand.Parameters.Add("$Name", SqliteType.Text);
            var pCpu = processCommand.Parameters.Add("$Cpu", SqliteType.Real);
            var pMem = processCommand.Parameters.Add("$Mem", SqliteType.Real);
            var pDiskRead = processCommand.Parameters.Add("$DiskRead", SqliteType.Real);
            var pDiskWrite = processCommand.Parameters.Add("$DiskWrite", SqliteType.Real);

            foreach (var p in processes)
            {
                pPid.Value = p.Pid;
                pName.Value = p.Name;
                pCpu.Value = p.CpuUsage;
                pMem.Value = p.MemoryUsage;
                pDiskRead.Value = p.DiskReadBytesPerSec;
                pDiskWrite.Value = p.DiskWriteBytesPerSec;
                processCommand.ExecuteNonQuery();
                totalProcs += 1 + (p.Children?.Count ?? 0);
            }

            sysCommand.Parameters.AddWithValue("$TotalProc", totalProcs);
            sysCommand.ExecuteNonQuery();

            transaction.Commit();
        }

        public static List<SystemPerformanceSnapshot> GetSystemPerformanceData(string filePath, int limit = 100)
        {
            var result = new List<SystemPerformanceSnapshot>();
            using var connection = new SqliteConnection($"Data Source={filePath}");
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT Timestamp, TotalCpuUsage, TotalMemory, AvailableMemory, GpuUtilization, GpuMemoryUsed, GpuTemperature, TotalProcesses
                FROM SystemPerformanceData
                ORDER BY Id DESC
                LIMIT {limit}
            ";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SystemPerformanceSnapshot
                {
                    Timestamp = reader.GetDateTime(0),
                    TotalCpuUsage = reader.GetDouble(1),
                    TotalMemory = reader.GetDouble(2),
                    AvailableMemory = reader.GetDouble(3),
                    GpuUtilization = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    GpuMemoryUsed = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    GpuTemperature = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    TotalProcesses = reader.GetInt32(7)
                });
            }
            result.Reverse();
            return result;
        }

        public static void DeleteDatabase(string filePath)
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
        }
    }
}
