# TaskManagerUI

A modern **Windows Task Manager-style desktop app** built with **WinUI 3** and **.NET 8**.

It provides a clean UI for real-time monitoring of:
- Processes (CPU, memory, disk, network)
- System overview metrics
- GPU activity (dedicated + integrated)
- Live performance charts

## Features

- **Processes view**
  - Hierarchical process list
  - Sort by name, PID, CPU, memory, disk and network metrics
  - Search by process name or PID
  - Kill selected process(es) from UI or with `Delete`
- **Charts view**
  - Live charts for CPU, RAM, GPU utilization, GPU memory, GPU temperature, and process count
  - Lightweight local SQLite storage for short history
- **GPU view**
  - NVIDIA GPU data via `nvidia-smi` (if available)
  - Integrated GPU process detection via Windows performance counters
- **Modern Windows UI**
  - WinUI 3 styling with cards, compact navigation, and Mica backdrop support

## Tech Stack

- **.NET 8**
- **WinUI 3 / Windows App SDK**
- **LiveChartsCore + SkiaSharp** for charts
- **Microsoft.Data.Sqlite** for local telemetry snapshots
- **ETW (TraceEvent)** for process-level network activity

## Requirements

- Windows 10/11 (target framework: `net8.0-windows10.0.19041.0`)
- .NET 8 SDK
- Visual Studio 2022 with Windows development workload

Optional:
- `nvidia-smi` in `PATH` for dedicated NVIDIA GPU metrics
- Elevated privileges may improve ETW/network visibility on some systems
- For full GPU metrics, run the app as Administrator

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run
```

You can also open `TaskManagerUI.csproj` in Visual Studio and run it directly.

## Project Structure

- `MainWindow.xaml` / `MainWindow.xaml.cs` - main UI and interaction logic
- `Monitoring/Common.cs` - process/system/memory/network collection
- `Monitoring/EtwNetworkMonitor.cs` - ETW network monitoring per process
- `Monitoring/GPU/NvidiaGPU.cs` - dedicated + integrated GPU data
- `Charts/Databse.cs` - SQLite persistence for chart history

## Notes

- The app stores chart data in a temporary local database while running.
- GPU features degrade gracefully if hardware/counters/tools are unavailable.
- GPU monitoring may require running the app as Administrator.

---

If you like the project, feel free to fork it and extend it with additional monitoring tabs.
``
