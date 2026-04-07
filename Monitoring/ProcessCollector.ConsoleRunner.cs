using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManagerUI.Monitoring
{
    partial class ProcessCollector
    {
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
                    var cpuTemp = TryGetCpuTemperatureC();
                    var perCoreStats = GetPerCoreCpuStats();
                    string cpuTempText = cpuTemp.HasValue ? $"{cpuTemp.Value:F1}°C" : "N/A";

                    frameLines.Clear();
                    frameLines.Add($"Total CPU Usage: {totalCpuUsage:F1}%   Total Memory: {FormatBytes((ulong)totalMem * 1024 * 1024)}   Available Memory: {FormatBytes((ulong)availMem * 1024 * 1024)}");
                    if (perCoreStats.Count > 0)
                        frameLines.Add(FormatPerCoreCpuLine(perCoreStats));
                    frameLines.Add($"CPU Temperature: {cpuTempText}");
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
                Console.CursorVisible = true;
            }
        }

        static void RenderFrame(List<string> lines)
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
    }
}
