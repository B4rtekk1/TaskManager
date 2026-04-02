using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskManagerUI.Monitoring;
using Windows.Storage;
using System.IO;
using System.Runtime.InteropServices;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.VisualElements;
using LiveChartsCore.Defaults;
using SkiaSharp;
using TaskManagerUI.Charts;

namespace TaskManagerUI
{
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        readonly ObservableCollection<ProcessRowViewModel> _processes = [];
        readonly ObservableCollection<ProcessRowViewModel> _filteredProcesses = [];
        readonly ObservableCollection<GpuProcessViewModel> _gpuProcesses = [];
        readonly ObservableCollection<GpuProcessViewModel> _integratedGpuProcesses = [];
        readonly Dictionary<int, ProcessRowViewModel> _viewModelsByPid = [];
        readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer _lerpTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        readonly Dictionary<int, int> _missingRefreshesByPid = [];
        bool _isRefreshing;
        IReadOnlyList<ProcessCollector.ProcessSnapshot> _lastSnapshot = [];
        (ulong idle, ulong kernel, ulong user) _previousSystemCpuTimes;
        static readonly string localFolder = GetAppDataFolderPath();
        static readonly string DbPath = Path.Combine(localFolder, "PerformanceData.db");
        const int MissingRefreshesBeforeRemove = 3;
        

        string _sortColumn = "Memory";
        bool _sortAscending = false;
        string _searchText = string.Empty;
        string _systemCpuText = "CPU --";
        string _memoryUsageText = "Memory --";
        string _networkUsageText = "Network --";
        string _processCountSummary = "0 shown / 0 total";
        string _lastUpdatedText = "Not updated yet";
        string _gpuStatusText = "Loading GPU data...";
        string _integratedGpuStatusText = "Integrated GPU Engine Instances";
        Visibility _noResultsVisibility = Visibility.Collapsed;
        Visibility _noGpuResultsVisibility = Visibility.Collapsed;
        Visibility _noIntegratedGpuResultsVisibility = Visibility.Collapsed;
        (long BytesReceived, long BytesSent) _previousNetworkUsage;

        public ObservableCollection<MetricChartViewModel> ChartsList { get; } = [];
        readonly MetricChartViewModel _cpuChart = new("CPU Usage (%)", SKColors.CornflowerBlue);
        readonly MetricChartViewModel _memChart = new("Memory Usage (GB)", SKColors.MediumPurple);
        readonly MetricChartViewModel _gpuUtilChart = new("GPU Utilization (%)", SKColors.MediumSeaGreen, 0);
        readonly MetricChartViewModel _gpuMemChart = new("GPU Memory Used (MB)", SKColors.LightSeaGreen, 0);
        readonly MetricChartViewModel _gpuTempChart = new("GPU Temperature (°C)", SKColors.OrangeRed, 0);
        readonly MetricChartViewModel _procChart = new("Total Processes", SKColors.SlateGray);

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public string SortColumn
        {
            get => _sortColumn;
            set
            {
                if (_sortColumn != value)
                {
                    _sortColumn = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool SortAscending
        {
            get => _sortAscending;
            set
            {
                if (_sortAscending != value)
                {
                    _sortAscending = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SystemCpuText
        {
            get => _systemCpuText;
            set
            {
                if (_systemCpuText != value)
                {
                    _systemCpuText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string MemoryUsageText
        {
            get => _memoryUsageText;
            set
            {
                if (_memoryUsageText != value)
                {
                    _memoryUsageText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string NetworkUsageText
        {
            get => _networkUsageText;
            set
            {
                if (_networkUsageText != value)
                {
                    _networkUsageText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessCountSummary
        {
            get => _processCountSummary;
            set
            {
                if (_processCountSummary != value)
                {
                    _processCountSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastUpdatedText
        {
            get => _lastUpdatedText;
            set
            {
                if (_lastUpdatedText != value)
                {
                    _lastUpdatedText = value;
                    OnPropertyChanged();
                }
            }
        }

        public Visibility NoResultsVisibility
        {
            get => _noResultsVisibility;
            set
            {
                if (_noResultsVisibility != value)
                {
                    _noResultsVisibility = value;
                    OnPropertyChanged();
                }
            }
        }

        public Visibility NoGpuResultsVisibility
        {
            get => _noGpuResultsVisibility;
            set
            {
                if (_noGpuResultsVisibility != value)
                {
                    _noGpuResultsVisibility = value;
                    OnPropertyChanged();
                }
            }
        }

        public Visibility NoIntegratedGpuResultsVisibility
        {
            get => _noIntegratedGpuResultsVisibility;
            set
            {
                if (_noIntegratedGpuResultsVisibility != value)
                {
                    _noIntegratedGpuResultsVisibility = value;
                    OnPropertyChanged();
                }
            }
        }

        public string GpuStatusText
        {
            get => _gpuStatusText;
            set
            {
                if (_gpuStatusText != value)
                {
                    _gpuStatusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string IntegratedGpuStatusText
        {
            get => _integratedGpuStatusText;
            set
            {
                if (_integratedGpuStatusText != value)
                {
                    _integratedGpuStatusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string GetSortIcon(string targetColumn, string currentColumn, bool isAscending)
        {
            if (targetColumn != currentColumn)
                return string.Empty;

            // Unicode for Up arrow (xE74A) and Down arrow (xE74B)
            return isAscending ? "\xE74A" : "\xE74B";
        }


        public MainWindow()
        {
            InitializeComponent();
            TryEnableSystemBackdrop();
            
            ChartsList.Add(_cpuChart);
            ChartsList.Add(_memChart);
            ChartsList.Add(_gpuUtilChart);
            ChartsList.Add(_gpuMemChart);
            ChartsList.Add(_gpuTempChart);
            ChartsList.Add(_procChart);

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DbPath)!);
            Charts.Databse.CreateDatabase(DbPath);
            _previousSystemCpuTimes = ProcessCollector.GetSystemCPUTimes();
            _previousNetworkUsage = ProcessCollector.GetNetworkUsage();

            ProcessesListView.ItemsSource = _processes;
            DedicatedGpuProcessesListView.ItemsSource = _gpuProcesses;
            IntegratedGpuProcessesListView.ItemsSource = _integratedGpuProcesses;

            SearchBox.TextChanged += SearchBox_TextChanged;

            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();

            _lerpTimer.Tick += LerpTimer_Tick;
            _lerpTimer.Start();

            this.Closed += MainWindow_Closed;

            _ = RefreshProcessesAsync();
        }

        void TryEnableSystemBackdrop()
        {
            try
            {
                SystemBackdrop = new MicaBackdrop();
            }
            catch (COMException)
            {
                SystemBackdrop = null;
            }
            catch (InvalidOperationException)
            {
                SystemBackdrop = null;
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            Charts.Databse.DeleteDatabase(DbPath);
        }

        void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
        }

        void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                var tag = item.Tag?.ToString();
                
                ProcessesView.Visibility = Visibility.Collapsed;
                ChartsView.Visibility = Visibility.Collapsed;
                GpuView.Visibility = Visibility.Collapsed;

                if (tag == "Charts")
                {
                    ChartsView.Visibility = Visibility.Visible;
                    UpdateCharts();
                }
                else if (tag == "GPU")
                {
                    GpuView.Visibility = Visibility.Visible;
                }
                else
                {
                    ProcessesView.Visibility = Visibility.Visible;
                }
            }
        }

        void UpdateCharts()
        {
            var data = Charts.Databse.GetSystemPerformanceData(DbPath, 60);
            
            _cpuChart.UpdateData(data.Select(x => new ObservablePoint(x.Timestamp.Ticks, x.TotalCpuUsage)));
            _memChart.UpdateData(data.Select(x => new ObservablePoint(x.Timestamp.Ticks, (x.TotalMemory - x.AvailableMemory) / 1024.0)));
            _gpuUtilChart.UpdateData(data.Where(x => x.GpuUtilization.HasValue).Select(x => new ObservablePoint(x.Timestamp.Ticks, x.GpuUtilization!.Value)));
            _gpuMemChart.UpdateData(data.Where(x => x.GpuMemoryUsed.HasValue).Select(x => new ObservablePoint(x.Timestamp.Ticks, x.GpuMemoryUsed!.Value)));
            _gpuTempChart.UpdateData(data.Where(x => x.GpuTemperature.HasValue).Select(x => new ObservablePoint(x.Timestamp.Ticks, x.GpuTemperature!.Value)));
            _procChart.UpdateData(data.Select(x => new ObservablePoint(x.Timestamp.Ticks, x.TotalProcesses)));
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
        {
            _searchText = SearchBox.Text.Trim();
            ApplySearchFilter();
        }

        void ProcessesListView_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Delete)
            {
                var selectedItems = ProcessesListView.SelectedItems.Cast<ProcessRowViewModel>().ToList();
                foreach (var item in selectedItems)
                {
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(item.Pid);
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore exceptions if process already exited or access denied
                    }
                }
            }
        }

        void GpuProcessesListView_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Delete)
            {
                if (sender is ListView listView)
                {
                    var selectedItems = listView.SelectedItems.Cast<GpuProcessViewModel>().ToList();
                    foreach (var item in selectedItems)
                    {
                        if (int.TryParse(item.PidText, out int pid))
                        {
                            try
                            {
                                var process = System.Diagnostics.Process.GetProcessById(pid);
                                process.Kill();
                            }
                            catch
                            {
                                // Ignore exceptions
                            }
                        }
                    }
                }
            }
        }

        void LerpTimer_Tick(object? sender, object? e)
        {
            foreach (var p in _processes)
            {
                p.UpdateInterpolation();
            }
        }

        async void RefreshTimer_Tick(object? sender, object? e)
        {
            await RefreshProcessesAsync();
        }

        public RelayCommand<string> SortCommand => new(SortProcesses);

        void RebuildVisibleListFromSnapshot()
        {
            if (_lastSnapshot.Count == 0)
                return;

            var desiredList = new List<ProcessRowViewModel>();

            IEnumerable<ProcessCollector.ProcessSnapshot> SortTree(IEnumerable<ProcessCollector.ProcessSnapshot> nodes)
            {
                var sorted = SortColumn switch
                {
                    "PID" => SortAscending ? nodes.OrderBy(p => p.Pid) : nodes.OrderByDescending(p => p.Pid),
                    "Name" => SortAscending ? nodes.OrderBy(p => p.Name) : nodes.OrderByDescending(p => p.Name),
                    "CPU" => SortAscending ? nodes.OrderBy(p => p.CpuUsage) : nodes.OrderByDescending(p => p.CpuUsage),
                    "Memory" => SortAscending ? nodes.OrderBy(p => p.MemoryUsage) : nodes.OrderByDescending(p => p.MemoryUsage),
                    "DiskRead" => SortAscending ? nodes.OrderBy(p => p.DiskReadBytesPerSec) : nodes.OrderByDescending(p => p.DiskReadBytesPerSec),
                    "DiskWrite" => SortAscending ? nodes.OrderBy(p => p.DiskWriteBytesPerSec) : nodes.OrderByDescending(p => p.DiskWriteBytesPerSec),
                    "NetRecv" => SortAscending ? nodes.OrderBy(p => p.NetReceivedBytesPerSec) : nodes.OrderByDescending(p => p.NetReceivedBytesPerSec),
                    "NetSent" => SortAscending ? nodes.OrderBy(p => p.NetSentBytesPerSec) : nodes.OrderByDescending(p => p.NetSentBytesPerSec),
                    _ => nodes
                };
                return sorted;
            }

            void ProcessItem(ProcessCollector.ProcessSnapshot item, int indent, bool isParentExpanded)
            {
                if (!_viewModelsByPid.TryGetValue(item.Pid, out var vm))
                    return;

                vm.Indent = indent;
                vm.HasChildren = item.Children.Count > 0;

                if (isParentExpanded)
                {
                    desiredList.Add(vm);
                }

                bool childExpanded = isParentExpanded && vm.IsExpanded;
                foreach (var child in SortTree(item.Children))
                {
                    ProcessItem(child, indent + 1, childExpanded);
                }
            }

            foreach (var item in SortTree(_lastSnapshot))
            {
                ProcessItem(item, 0, true);
            }

            SyncVisibleList(desiredList);
            ApplySearchFilter();
            UpdateProcessCountDisplay();
        }

        async Task RefreshProcessesAsync(bool useCachedSnapshot = false)
        {
            if (_isRefreshing)
                return;

            _isRefreshing = true;
            try
            {
                IReadOnlyList<ProcessCollector.ProcessSnapshot> snapshot;

                if (!useCachedSnapshot || _lastSnapshot.Count == 0)
                {
                    var currentSystemCpuTimes = ProcessCollector.GetSystemCPUTimes();
                    double totalCpuUsage = ProcessCollector.CalculateTotalCpuPercent(_previousSystemCpuTimes, currentSystemCpuTimes);
                    _previousSystemCpuTimes = currentSystemCpuTimes;
                    
                    uint cpuFreq = ProcessCollector.GetCurrentCpuFrequencyMhz();

                    var (totalMem, availMem) = ProcessCollector.GetMemoryUsage();
                    var currentNetworkUsage = ProcessCollector.GetNetworkUsage();
                    long networkReceivedPerSec = Math.Max(0, currentNetworkUsage.BytesReceived - _previousNetworkUsage.BytesReceived);
                    long networkSentPerSec = Math.Max(0, currentNetworkUsage.BytesSent - _previousNetworkUsage.BytesSent);
                    _previousNetworkUsage = currentNetworkUsage;

                    SystemCpuText = $"CPU {totalCpuUsage:F1}% @ {cpuFreq / 1000.0:F2} GHz";
                    MemoryUsageText = $"Memory {FormatBytes((ulong)Math.Max(0, totalMem - availMem) * 1024 * 1024)} / {FormatBytes((ulong)totalMem * 1024 * 1024)}";
                    NetworkUsageText = $"Network \u2193{FormatBytes((ulong)networkReceivedPerSec)}/s \u2191{FormatBytes((ulong)networkSentPerSec)}/s";
                    LastUpdatedText = $"Updated {DateTime.Now:HH:mm:ss}";

                    snapshot = await Task.Run(() => ProcessCollector.GetTopProcesses(0));
                    _lastSnapshot = snapshot;
                    
                    var gpuInfo = await Task.Run(() => ProcessCollector.TryQueryGpuInfo());

                    UpdateGpuView(gpuInfo, snapshot);

                    await Task.Run(() => Charts.Databse.SaveData(DbPath, totalCpuUsage, totalMem, availMem, gpuInfo, snapshot));
                }
                else
                {
                    snapshot = _lastSnapshot;
                }

                if (snapshot.Count == 0 && _processes.Count > 0)
                    return;

                var presentPids = new HashSet<int>();
                var desiredList = new List<ProcessRowViewModel>();

                IEnumerable<ProcessCollector.ProcessSnapshot> SortTree(IEnumerable<ProcessCollector.ProcessSnapshot> nodes)
                {
                    var sorted = SortColumn switch
                    {
                        "PID" => SortAscending ? nodes.OrderBy(p => p.Pid) : nodes.OrderByDescending(p => p.Pid),
                        "Name" => SortAscending ? nodes.OrderBy(p => p.Name) : nodes.OrderByDescending(p => p.Name),
                        "CPU" => SortAscending ? nodes.OrderBy(p => p.CpuUsage) : nodes.OrderByDescending(p => p.CpuUsage),
                        "Memory" => SortAscending ? nodes.OrderBy(p => p.MemoryUsage) : nodes.OrderByDescending(p => p.MemoryUsage),
                        "DiskRead" => SortAscending ? nodes.OrderBy(p => p.DiskReadBytesPerSec) : nodes.OrderByDescending(p => p.DiskReadBytesPerSec),
                        "DiskWrite" => SortAscending ? nodes.OrderBy(p => p.DiskWriteBytesPerSec) : nodes.OrderByDescending(p => p.DiskWriteBytesPerSec),
                        "NetRecv" => SortAscending ? nodes.OrderBy(p => p.NetReceivedBytesPerSec) : nodes.OrderByDescending(p => p.NetReceivedBytesPerSec),
                        "NetSent" => SortAscending ? nodes.OrderBy(p => p.NetSentBytesPerSec) : nodes.OrderByDescending(p => p.NetSentBytesPerSec),
                        _ => nodes
                    };
                    return sorted.ToList();
                }

                void ProcessItem(ProcessCollector.ProcessSnapshot item, int indent, bool isParentExpanded)
                {
                    presentPids.Add(item.Pid);

                    ProcessRowViewModel vm;
                    if (!_viewModelsByPid.TryGetValue(item.Pid, out var existing))
                    {
                        vm = ProcessRowViewModel.FromSnapshot(item, indent);
                        vm.OnExpandToggled = RebuildVisibleListFromSnapshot;
                        _viewModelsByPid[item.Pid] = vm;
                    }
                    else
                    {
                        vm = existing;
                        if (!useCachedSnapshot)
                        {
                            vm.UpdateFromSnapshot(item);
                        }
                        vm.Indent = indent;
                    }

                    vm.HasChildren = item.Children.Count > 0;

                    if (isParentExpanded)
                    {
                        desiredList.Add(vm);
                    }

                    bool childExpanded = isParentExpanded && vm.IsExpanded;

                    foreach (var child in SortTree(item.Children))
                    {
                        ProcessItem(child, indent + 1, childExpanded);
                    }
                }

                foreach (var item in SortTree(snapshot))
                {
                    ProcessItem(item, 0, true);
                }

                var staleTracker = _missingRefreshesByPid.Keys.Where(k => !presentPids.Contains(k)).ToList();
                foreach (var st in staleTracker)
                {
                    int misses = _missingRefreshesByPid[st] + 1;
                    if (misses >= MissingRefreshesBeforeRemove)
                    {
                        _missingRefreshesByPid.Remove(st);
                        _viewModelsByPid.Remove(st);
                    }
                    else
                        _missingRefreshesByPid[st] = misses;
                }

                SyncVisibleList(desiredList);

                ApplySearchFilter();
                UpdateProcessCountDisplay();

                if (ChartsView.Visibility == Visibility.Visible)
                {
                    UpdateCharts();
                }
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        void SortProcesses(string? column)
        {
            if (column == null)
                return;

            if (SortColumn == column)
            {
                SortAscending = !SortAscending;
            }
            else
            {
                SortColumn = column;
                SortAscending = false;
            }

            _ = RefreshProcessesAsync(true);
        }

        void UpdateGpuView(ProcessCollector.GPUData? gpuInfo, IReadOnlyList<ProcessCollector.ProcessSnapshot> snapshot)
        {
            if (gpuInfo == null)
            {
                GpuStatusText = "No compatible GPU found or NVIDIA SMI unavailable.";
                IntegratedGpuStatusText = "No integrated GPU detected.";
                _gpuProcesses.Clear();
                _integratedGpuProcesses.Clear();
                NoGpuResultsVisibility = Visibility.Visible;
                NoIntegratedGpuResultsVisibility = Visibility.Visible;
                return;
            }

            if (string.IsNullOrEmpty(gpuInfo.Name))
            {
                GpuStatusText = "No NVIDIA GPU detected.";
                NoGpuResultsVisibility = Visibility.Visible;
                _gpuProcesses.Clear();
            }
            else
            {
                GpuStatusText = $"GPU: {gpuInfo.Name} | GPU Temp: {gpuInfo.TemperatureC:F0} °C";
                var presentGpuPids = gpuInfo.ProcessIDs;
                if (presentGpuPids.Count == 0)
                {
                    _gpuProcesses.Clear();
                    NoGpuResultsVisibility = Visibility.Visible;
                }
                else
                {
                    NoGpuResultsVisibility = Visibility.Collapsed;
                    var newGpuProcesses = new List<GpuProcessViewModel>();
                    var allProcessesGpu = new Dictionary<int, string>();
                    
                    void GatherGpu(IReadOnlyList<ProcessCollector.ProcessSnapshot> nodes)
                    {
                        foreach (var n in nodes)
                        {
                            allProcessesGpu[n.Pid] = n.Name;
                            GatherGpu(n.Children);
                        }
                    }
                    GatherGpu(snapshot);

                    foreach (var pid in presentGpuPids)
                    {
                        string pName = allProcessesGpu.TryGetValue(pid, out var name) ? name : "Unknown Process";
                        newGpuProcesses.Add(new GpuProcessViewModel
                        {
                            Name = pName,
                            PidText = pid.ToString(),
                            TemperatureText = $"{gpuInfo.TemperatureC:F0} °C"
                        });
                    }

                    _gpuProcesses.Clear();
                    foreach (var p in newGpuProcesses)
                    {
                        _gpuProcesses.Add(p);
                    }
                }
            }

            var integratedPids = gpuInfo.IntegratedProcessIDs;
            if (integratedPids.Count == 0)
            {
                _integratedGpuProcesses.Clear();
                NoIntegratedGpuResultsVisibility = Visibility.Visible;
                IntegratedGpuStatusText = string.IsNullOrEmpty(gpuInfo.IntegratedGpuName) ? "No integrated GPU processes running." : $"GPU: {gpuInfo.IntegratedGpuName}";
            }
            else
            {
                NoIntegratedGpuResultsVisibility = Visibility.Collapsed;
                IntegratedGpuStatusText = string.IsNullOrEmpty(gpuInfo.IntegratedGpuName) ? "Integrated GPU Engine Instances" : $"GPU: {gpuInfo.IntegratedGpuName}";

                var newIntegratedProcs = new List<GpuProcessViewModel>();
                var allProcessesIntegrated = new Dictionary<int, string>();

                void GatherInt(IReadOnlyList<ProcessCollector.ProcessSnapshot> nodes)
                {
                    foreach (var n in nodes)
                    {
                        allProcessesIntegrated[n.Pid] = n.Name;
                        GatherInt(n.Children);
                    }
                }
                GatherInt(snapshot);

                foreach (var pid in integratedPids)
                {
                    string pName = allProcessesIntegrated.TryGetValue(pid, out var name) ? name : "Unknown Process";
                    newIntegratedProcs.Add(new GpuProcessViewModel
                    {
                        Name = pName,
                        PidText = pid.ToString(),
                        TemperatureText = "N/A"
                    });
                }

                _integratedGpuProcesses.Clear();
                foreach (var p in newIntegratedProcs)
                {
                    _integratedGpuProcesses.Add(p);
                }
            }
        }

        void ApplySearchFilter()
        {
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                if (!ReferenceEquals(ProcessesListView.ItemsSource, _processes))
                    ProcessesListView.ItemsSource = _processes;
                UpdateProcessCountDisplay();
                return;
            }

            _filteredProcesses.Clear();

            foreach (var process in _processes)
            {
                if (process.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                    process.PidText.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                {
                    _filteredProcesses.Add(process);
                }
            }

            if (!ReferenceEquals(ProcessesListView.ItemsSource, _filteredProcesses))
                ProcessesListView.ItemsSource = _filteredProcesses;

            UpdateProcessCountDisplay();
        }

        static void ApplyAlternatingRows(IEnumerable<ProcessRowViewModel> rows)
        {
            bool isAlt = false;
            foreach (var process in rows)
            {
                process.IsAlternate = isAlt;
                isAlt = !isAlt;
            }
        }

        void SyncVisibleList(IReadOnlyList<ProcessRowViewModel> desiredList)
        {
            int minCount = Math.Min(_processes.Count, desiredList.Count);

            for (int i = 0; i < minCount; i++)
            {
                if (!ReferenceEquals(_processes[i], desiredList[i]))
                {
                    _processes[i] = desiredList[i];
                }
            }

            while (_processes.Count > desiredList.Count)
            {
                _processes.RemoveAt(_processes.Count - 1);
            }

            for (int i = _processes.Count; i < desiredList.Count; i++)
            {
                _processes.Add(desiredList[i]);
            }
        }

        void UpdateProcessCountDisplay()
        {
            int shown = ReferenceEquals(ProcessesListView.ItemsSource, _filteredProcesses) ? _filteredProcesses.Count : _processes.Count;

            int total = 0;
            void CountNodes(IReadOnlyList<ProcessCollector.ProcessSnapshot> nodes)
            {
                total += nodes.Count;
                foreach (var node in nodes)
                {
                    CountNodes(node.Children);
                }
            }
            CountNodes(_lastSnapshot);

            ProcessCountSummary = $"{shown} shown / {total} total";
            NoResultsVisibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static string FormatBytes(ulong bytes) => bytes switch
        {
            >= 1_000_000_000_000 => $"{bytes / 1_000_000_000_000.0:F2} TB",
            >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F2} GB",
            >= 1_000_000 => $"{bytes / 1_000_000.0:F2} MB",
            >= 1_000 => $"{bytes / 1_000.0:F2} KB",
            _ => $"{bytes} B"
        };

        static string GetAppDataFolderPath()
        {
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception ex) when (
                ex is COMException ||
                ex is InvalidOperationException ||
                ex is UnauthorizedAccessException)
            {
                var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskManagerUI");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }

    public sealed class ProcessRowViewModel : INotifyPropertyChanged
    {
        int _pid;
        string _name = string.Empty;
        string _pidText = string.Empty;
        string _cpuText = string.Empty;
        string _memoryText = string.Empty;
        string _diskReadText = string.Empty;
        string _diskWriteText = string.Empty;
        string _netRecvText = string.Empty;
        string _netSentText = string.Empty;
        int _indent;

        double _targetCpuUsage = -1;
        double _currentCpuUsage = 0;
        ulong _targetMemoryUsage = ulong.MaxValue;
        double _currentMemoryUsage = 0;
        ulong _targetDiskRead = ulong.MaxValue;
        double _currentDiskRead = 0;
        ulong _targetDiskWrite = ulong.MaxValue;
        double _currentDiskWrite = 0;
        ulong _targetNetRecv = ulong.MaxValue;
        double _currentNetRecv = 0;
        ulong _targetNetSent = ulong.MaxValue;
        double _currentNetSent = 0;

        double _cpuPercentage = 0;
        double _memoryPercentage = 0;
        bool _isAlternate;

        bool _isExpanded = false;
        bool _hasChildren;

        public bool IsExpanded 
        { 
            get => _isExpanded; 
            set 
            { 
                if (SetField(ref _isExpanded, value)) 
                    OnPropertyChanged(nameof(ExpandIcon)); 
            } 
        }
        
        public bool HasChildren 
        { 
            get => _hasChildren; 
            set 
            { 
                if (SetField(ref _hasChildren, value)) 
                {
                    OnPropertyChanged(nameof(HasChildrenVisibility)); 
                    OnPropertyChanged(nameof(NameMargin));
                }
            } 
        }

        public string ExpandIcon => IsExpanded ? "\xE70D" : "\xE76C";
        public Visibility HasChildrenVisibility => HasChildren ? Visibility.Visible : Visibility.Collapsed;
        
        public ICommand ToggleExpandCommand { get; }
        public ICommand KillCommand { get; }
        public Action? OnExpandToggled { get; set; }

        public ProcessRowViewModel()
        {
            ToggleExpandCommand = new RelayCommand<object>(_ =>
            {
                IsExpanded = !IsExpanded;
                OnExpandToggled?.Invoke();
            });

            KillCommand = new RelayCommand<object>(_ =>
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(Pid);
                    process.Kill();
                }
                catch
                {
                    // Ignore exceptions if process already exited or access denied
                }
            });
        }

        public bool IsAlternate
        {
            get => _isAlternate;
            set
            {
                if (_isAlternate != value)
                {
                    _isAlternate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CellOpacity1));
                    OnPropertyChanged(nameof(CellOpacity2));
                }
            }
        }

        public double CellOpacity1 => _isAlternate ? 0.0 : 0.03;
        public double CellOpacity2 => _isAlternate ? 0.0 : 0.03;

        public int Pid { get => _pid; private set => SetField(ref _pid, value); }
        public string Name { get => _name; private set => SetField(ref _name, value); }
        public int Indent { get => _indent; set => SetField(ref _indent, value, nameof(NameMargin)); }
        public Thickness NameMargin => new Thickness(Indent * 48 + (HasChildren ? 0 : 28), 0, 0, 0);

        public string PidText { get => _pidText; private set => SetField(ref _pidText, value); }
        public string CpuText { get => _cpuText; private set => SetField(ref _cpuText, value); }
        public string MemoryText { get => _memoryText; private set => SetField(ref _memoryText, value); }
        public string DiskReadText { get => _diskReadText; private set => SetField(ref _diskReadText, value); }
        public string DiskWriteText { get => _diskWriteText; private set => SetField(ref _diskWriteText, value); }
        public string NetRecvText { get => _netRecvText; private set => SetField(ref _netRecvText, value); }
        public string NetSentText { get => _netSentText; private set => SetField(ref _netSentText, value); }

        public double CpuPercentage { get => _cpuPercentage; private set => SetField(ref _cpuPercentage, value); }
        public double MemoryPercentage { get => _memoryPercentage; private set => SetField(ref _memoryPercentage, value); }

        public double CpuUsageValue => _currentCpuUsage;
        public ulong MemoryUsageValue => (ulong)_currentMemoryUsage;
        public ulong DiskReadValue => (ulong)_currentDiskRead;
        public ulong DiskWriteValue => (ulong)_currentDiskWrite;
        public ulong NetRecvValue => (ulong)_currentNetRecv;
        public ulong NetSentValue => (ulong)_currentNetSent;

        public event PropertyChangedEventHandler? PropertyChanged;

        internal static ProcessRowViewModel FromSnapshot(ProcessCollector.ProcessSnapshot value, int indent = 0)
        {
            var vm = new ProcessRowViewModel();
            vm.Indent = indent;
            vm._currentCpuUsage = value.CpuUsage;
            vm._currentMemoryUsage = value.MemoryUsage;
            vm._currentDiskRead = (ulong)Math.Max(0, value.DiskReadBytesPerSec);
            vm._currentDiskWrite = (ulong)Math.Max(0, value.DiskWriteBytesPerSec);
            vm._currentNetRecv = (ulong)Math.Max(0, value.NetReceivedBytesPerSec);
            vm._currentNetSent = (ulong)Math.Max(0, value.NetSentBytesPerSec);
            vm.UpdateFromSnapshot(value);
            
            vm.CpuText = $"{vm._currentCpuUsage:F1}";
            vm.MemoryText = FormatBytes((ulong)vm._currentMemoryUsage);
            vm.DiskReadText = FormatBytes((ulong)vm._currentDiskRead);
            vm.DiskWriteText = FormatBytes((ulong)vm._currentDiskWrite);
            vm.NetRecvText = FormatBytes((ulong)vm._currentNetRecv);
            vm.NetSentText = FormatBytes((ulong)vm._currentNetSent);
            vm.CpuPercentage = Math.Min(vm._currentCpuUsage, 100);
            vm.MemoryPercentage = Math.Min(vm._currentMemoryUsage / 100.0, 100);

            return vm;
        }

        public void UpdateInterpolation()
        {
            const double alpha = 0.2; // Smoothing factor
            
            if (_targetCpuUsage >= 0 && Math.Abs(_targetCpuUsage - _currentCpuUsage) > 0.05)
            {
                _currentCpuUsage += (_targetCpuUsage - _currentCpuUsage) * alpha;
                CpuText = $"{_currentCpuUsage:F1}";
                CpuPercentage = Math.Min(_currentCpuUsage, 100);
            }

            if (_targetMemoryUsage != ulong.MaxValue && Math.Abs(_targetMemoryUsage - _currentMemoryUsage) > 1024)
            {
                _currentMemoryUsage += (_targetMemoryUsage - _currentMemoryUsage) * alpha;
                MemoryText = FormatBytes((ulong)_currentMemoryUsage);
                MemoryPercentage = Math.Min(_currentMemoryUsage / 100.0, 100);
            }

            if (_targetDiskRead != ulong.MaxValue && Math.Abs(_targetDiskRead - _currentDiskRead) > 1024)
            {
                _currentDiskRead += (_targetDiskRead - _currentDiskRead) * alpha;
                DiskReadText = FormatBytes((ulong)_currentDiskRead);
            }

            if (_targetDiskWrite != ulong.MaxValue && Math.Abs(_targetDiskWrite - _currentDiskWrite) > 1024)
            {
                _currentDiskWrite += (_targetDiskWrite - _currentDiskWrite) * alpha;
                DiskWriteText = FormatBytes((ulong)_currentDiskWrite);
            }

            if (_targetNetRecv != ulong.MaxValue && Math.Abs(_targetNetRecv - _currentNetRecv) > 1024)
            {
                _currentNetRecv += (_targetNetRecv - _currentNetRecv) * alpha;
                NetRecvText = FormatBytes((ulong)_currentNetRecv);
            }

            if (_targetNetSent != ulong.MaxValue && Math.Abs(_targetNetSent - _currentNetSent) > 1024)
            {
                _currentNetSent += (_targetNetSent - _currentNetSent) * alpha;
                NetSentText = FormatBytes((ulong)_currentNetSent);
            }
        }

        internal void UpdateFromSnapshot(ProcessCollector.ProcessSnapshot value)
        {
            if (Pid != value.Pid)
            {
                Pid = value.Pid;
                PidText = value.Pid.ToString();
            }

            Name = value.Name;

            _targetCpuUsage = value.CpuUsage;
            _targetMemoryUsage = value.MemoryUsage;
            _targetDiskRead = (ulong)Math.Max(0, value.DiskReadBytesPerSec);
            _targetDiskWrite = (ulong)Math.Max(0, value.DiskWriteBytesPerSec);
            _targetNetRecv = (ulong)Math.Max(0, value.NetReceivedBytesPerSec);
            _targetNetSent = (ulong)Math.Max(0, value.NetSentBytesPerSec);

            CpuPercentage = Math.Min(value.CpuUsage, 100);
            MemoryPercentage = Math.Min(value.MemoryUsage / 100.0, 100);
        }

        void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
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

    public sealed class RelayCommand<T> : ICommand
    {
        readonly Action<T?> _execute;

        public event EventHandler? CanExecuteChanged;

        public RelayCommand(Action<T?> execute)
        {
            _execute = execute;
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            _execute((T?)parameter);
        }
    }

    public sealed class GpuProcessViewModel
    {
        public string Name { get; init; } = string.Empty;
        public string PidText { get; init; } = string.Empty;
        public string TemperatureText { get; init; } = string.Empty;
    }

    public sealed class MetricChartViewModel : INotifyPropertyChanged
    {
        public string Title { get; }
        public ISeries[] Series { get; }
        public LiveChartsCore.Kernel.Sketches.ICartesianAxis[] XAxes { get; }
        public LiveChartsCore.Kernel.Sketches.ICartesianAxis[] YAxes { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MetricChartViewModel(string title, SKColor color, double? minY = null)
        {
            Title = title;
            Series = [
                new LineSeries<ObservablePoint> { 
                    Values = [], 
                    Fill = new SolidColorPaint(color.WithAlpha(50)),
                    Stroke = new SolidColorPaint(color) { StrokeThickness = 2 },
                    GeometrySize = 0, 
                    LineSmoothness = 0 
                }
            ];

            XAxes = [
                new Axis 
                { 
                    Labeler = value => new DateTime((long)value).ToString("HH:mm:ss"),
                    LabelsRotation = 15
                }
            ];

            YAxes = [
                new Axis { MinLimit = minY }
            ];
        }

        public void UpdateData(IEnumerable<ObservablePoint> data)
        {
            var lineSeries = (LineSeries<ObservablePoint>)Series[0];
            lineSeries.Values = data.ToArray();
        }
    }
}
