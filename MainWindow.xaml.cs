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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TaskManagerUI.Monitoring;
using Windows.Storage;
using System.IO;
using System.Runtime.InteropServices;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Defaults;
using SkiaSharp;
using WinRT;
using WinRT.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using SystemBackdrops = Microsoft.UI.Composition.SystemBackdrops;
using Windows.System.Power;

namespace TaskManagerUI
{
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        enum BackdropMode
        {
            Acrylic,
            Mica,
            MicaAlt
        }

        readonly ObservableCollection<ProcessRowViewModel> _processes = [];
        readonly ObservableCollection<ProcessRowViewModel> _filteredProcesses = [];
        readonly ObservableCollection<GpuProcessViewModel> _gpuProcesses = [];
        readonly ObservableCollection<GpuProcessViewModel> _integratedGpuProcesses = [];
        readonly Dictionary<int, ProcessRowViewModel> _viewModelsByPid = [];
        readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer _lerpTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        readonly Dictionary<int, int> _missingRefreshesByPid = [];
        SystemBackdrops.DesktopAcrylicController? _acrylicController;
        SystemBackdrops.MicaController? _micaController;
        SystemBackdrops.SystemBackdropConfiguration? _configurationSource;
        AppWindowTitleBar? _appWindowTitleBar;
        BackdropMode _backdropMode = BackdropMode.Acrylic;
        bool _preferThinAcrylic = true;
        bool _isPowerSavingMode;
        float _acrylicTintOpacity = 0.74f;
        float _acrylicLuminosityOpacity = 0.84f;
        bool _isApplyingBackdrop;
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
        string _ramFrequencyText = "Speed: N/A";
        string _networkUsageText = "Network --";
        string _processCountSummary = "0 shown / 0 total";
        string _lastUpdatedText = "Not updated yet";
        string _contextSwitchesText = "Context Switches: N/A";
        string _interruptsText = "Interrupts: N/A";
        string _syscallsText = "System Calls: N/A";
        string _cpuQueueText = "CPU Queue: N/A";
        string _cpuTimeBreakdownText = "CPU Time: U N/A | K N/A | DPC N/A | IRQ N/A";
        string _cpuIdleTimeText = "CPU Idle: N/A";
        string _cpuParkingText = "CPU Parking: N/A";
        string _gpuStatusText = "Loading GPU data...";
        string _integratedGpuStatusText = "Integrated GPU Engine Instances";
        string _processorNameText = "Processor: N/A";
        string _processorTopologyText = "Sockets: N/A | Cores: N/A | Logical processors: N/A";
        string _processorCacheText = "Cache L1: N/A | L2: N/A | L3: N/A";
        string _cpuTemperatureText = "CPU Temp: N/A";
        string _diskActivitySummary = "Disk --";
        string _diskReadWriteText = "";
        string _diskNamesText = "";
        Visibility _noResultsVisibility = Visibility.Collapsed;
        Visibility _noGpuResultsVisibility = Visibility.Collapsed;
        Visibility _noIntegratedGpuResultsVisibility = Visibility.Collapsed;
        (long BytesReceived, long BytesSent) _previousNetworkUsage;

        readonly List<TaskManagerUI.Monitoring.Disk.DiskMetrics> _diskMetricsList = TaskManagerUI.Monitoring.Disk.DiskMetrics.GetAllDisks();

        public ObservableCollection<MetricChartViewModel> ChartsList { get; } = [];
        readonly MetricChartViewModel _cpuChart = new("CPU Usage (%)", [(SKColors.CornflowerBlue, "CPU")]);
        readonly MetricChartViewModel _memChart = new("Memory Usage (GB)", [(SKColors.MediumPurple, "Memory")]);
        readonly MetricChartViewModel _diskChart = new("Disk Read/Write (MB/s)", [(SKColors.LightCoral, "Read"), (SKColors.MediumSlateBlue, "Write")], adaptiveLogarithmicAxis: true);
        readonly MetricChartViewModel _gpuUtilChart = new("GPU Utilization (%)", [(SKColors.MediumSeaGreen, "GPU Util")]);
        readonly MetricChartViewModel _gpuMemChart = new("GPU Memory Used (MB)", [(SKColors.LightSeaGreen, "GPU Mem")]);
        readonly MetricChartViewModel _gpuTempChart = new("GPU Temperature (°C)", [(SKColors.OrangeRed, "Temp")], integerAxis: true, yLabelSuffix: "°C");
        readonly MetricChartViewModel _procChart = new("Total Processes", [(SKColors.SlateGray, "Processes")]);
        public ObservableCollection<CpuCoreMetricViewModel> CpuCoreMetrics { get; } = [];
        public ObservableCollection<DiskViewModel> DisksList { get; } = [];

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

        public string CpuIdleTimeText
        {
            get => _cpuIdleTimeText;
            set
            {
                if (_cpuIdleTimeText != value)
                {
                    _cpuIdleTimeText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CpuParkingText
        {
            get => _cpuParkingText;
            set
            {
                if (_cpuParkingText != value)
                {
                    _cpuParkingText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CpuQueueText
        {
            get => _cpuQueueText;
            set
            {
                if (_cpuQueueText != value)
                {
                    _cpuQueueText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CpuTimeBreakdownText
        {
            get => _cpuTimeBreakdownText;
            set
            {
                if (_cpuTimeBreakdownText != value)
                {
                    _cpuTimeBreakdownText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessorTopologyText
        {
            get => _processorTopologyText;
            set
            {
                if (_processorTopologyText != value)
                {
                    _processorTopologyText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessorCacheText
        {
            get => _processorCacheText;
            set
            {
                if (_processorCacheText != value)
                {
                    _processorCacheText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessorNameText
        {
            get => _processorNameText;
            set
            {
                if (_processorNameText != value)
                {
                    _processorNameText = value;
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

        public string RamFrequencyText
        {
            get => _ramFrequencyText;
            set
            {
                if (_ramFrequencyText != value)
                {
                    _ramFrequencyText = value;
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

        public string ContextSwitchesText
        {
            get => _contextSwitchesText;
            set
            {
                if (_contextSwitchesText != value)
                {
                    _contextSwitchesText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string InterruptsText
        {
            get => _interruptsText;
            set
            {
                if (_interruptsText != value)
                {
                    _interruptsText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SyscallsText
        {
            get => _syscallsText;
            set
            {
                if (_syscallsText != value)
                {
                    _syscallsText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CpuTemperatureText
        {
            get => _cpuTemperatureText;
            set
            {
                if (_cpuTemperatureText != value)
                {
                    _cpuTemperatureText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DiskActivitySummary
        {
            get => _diskActivitySummary;
            set
            {
                if (_diskActivitySummary != value)
                {
                    _diskActivitySummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DiskReadWriteText
        {
            get => _diskReadWriteText;
            set
            {
                if (_diskReadWriteText != value)
                {
                    _diskReadWriteText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DiskNamesText
        {
            get => _diskNamesText;
            set
            {
                if (_diskNamesText != value)
                {
                    _diskNamesText = value;
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
            ConfigureTitleBarForAcrylic();
            _isPowerSavingMode = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            TrySetAcrylicBackdrop(useAcrylicThin: true);
            PowerManager.EnergySaverStatusChanged += PowerManager_EnergySaverStatusChanged;
            
            ChartsList.Add(_cpuChart);
            ChartsList.Add(_memChart);
            ChartsList.Add(_diskChart);
            ChartsList.Add(_gpuUtilChart);
            ChartsList.Add(_gpuMemChart);
            ChartsList.Add(_gpuTempChart);
            ChartsList.Add(_procChart);

            foreach (var d in _diskMetricsList)
            {
                DisksList.Add(new DiskViewModel { 
                    Name = $"Disk {d.Index} ({d.Name.Replace('\\', ' ').Trim()})", 
                    DiskType = d.DiskType,
                    Model = string.IsNullOrEmpty(d.Model) ? "Unknown Model" : d.Model,
                    UsagePercent = 0
                });
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DbPath)!);
            Charts.Databse.CreateDatabase(DbPath);
            _previousSystemCpuTimes = ProcessCollector.GetSystemCPUTimes();
            _previousNetworkUsage = ProcessCollector.GetNetworkUsage();
            ProcessorNameText = $"Processor: {ProcessCollector.GetProcessorName()}";
            ProcessorTopologyText = ProcessCollector.GetProcessorTopologySummary();
            ProcessorCacheText = ProcessCollector.GetProcessorCacheSummary();

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

        void ConfigureTitleBarForAcrylic()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            ExtendsContentIntoTitleBar = true;

            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindowTitleBar = appWindow.TitleBar;

            ApplyTitleBarColors();

            if (Content is FrameworkElement root)
            {
                root.Margin = new Thickness(0, _appWindowTitleBar.Height, 0, 0);
            }
        }

        void ApplyTitleBarColors()
        {
            if (_appWindowTitleBar is null)
                return;

            bool isDark = ((FrameworkElement)Content).ActualTheme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
            };

            var foreground = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            var inactiveForeground = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
                : Microsoft.UI.ColorHelper.FromArgb(0x99, 0x00, 0x00, 0x00);
            var hoverBackground = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Microsoft.UI.ColorHelper.FromArgb(0x22, 0x00, 0x00, 0x00);
            var pressedBackground = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(0x55, 0xFF, 0xFF, 0xFF)
                : Microsoft.UI.ColorHelper.FromArgb(0x33, 0x00, 0x00, 0x00);

            _appWindowTitleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
            _appWindowTitleBar.ForegroundColor = foreground;
            _appWindowTitleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            _appWindowTitleBar.InactiveForegroundColor = inactiveForeground;

            _appWindowTitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            _appWindowTitleBar.ButtonForegroundColor = foreground;
            _appWindowTitleBar.ButtonHoverBackgroundColor = hoverBackground;
            _appWindowTitleBar.ButtonHoverForegroundColor = foreground;
            _appWindowTitleBar.ButtonPressedBackgroundColor = pressedBackground;
            _appWindowTitleBar.ButtonPressedForegroundColor = foreground;
            _appWindowTitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            _appWindowTitleBar.ButtonInactiveForegroundColor = inactiveForeground;
        }

        void CpuCoreListView_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateCpuCoreTileLayout();
        }

        void CpuCoreListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCpuCoreTileLayout();
        }

        void UpdateCpuCoreTileLayout()
        {
            if (CpuCoreListView is null)
                return;

            var wrapGrid = FindDescendant<ItemsWrapGrid>(CpuCoreListView);
            if (wrapGrid is null)
                return;

            const double minTileWidth = 190;
            const double spacingPerTile = 0;

            double availableWidth = Math.Max(0, CpuCoreListView.ActualWidth - 8);
            if (availableWidth <= 0)
                return;

            int columns = Math.Max(1, (int)Math.Floor((availableWidth + spacingPerTile) / (minTileWidth + spacingPerTile)));
            double tileWidth = Math.Floor((availableWidth - (columns * spacingPerTile)) / columns);

            wrapGrid.ItemWidth = Math.Max(minTileWidth, tileWidth);
        }

        static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typedChild)
                    return typedChild;

                var nested = FindDescendant<T>(child);
                if (nested is not null)
                    return nested;
            }

            return null;
        }

        void TryEnableSystemBackdrop()
        {
            try
            {
                SystemBackdrop = _backdropMode == BackdropMode.Acrylic
                    ? new DesktopAcrylicBackdrop()
                    : new MicaBackdrop();
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

        bool TrySetAcrylicBackdrop(bool useAcrylicThin)
        {
            _backdropMode = BackdropMode.Acrylic;
            _preferThinAcrylic = useAcrylicThin;
            return ApplyBackdropController();
        }

        bool ApplyBackdropController()
        {
            if (_isApplyingBackdrop)
                return false;

            _isApplyingBackdrop = true;
            try
            {
            DisposeBackdropControllers();

            if (_backdropMode is BackdropMode.Acrylic)
            {
                if (!SystemBackdrops.DesktopAcrylicController.IsSupported())
                {
                    TryEnableSystemBackdrop();
                    return false;
                }
            }
            else
            {
                if (!SystemBackdrops.MicaController.IsSupported())
                {
                    TryEnableSystemBackdrop();
                    return false;
                }
            }

            SystemBackdrop = null;

            if (_configurationSource is null)
            {
                if (Content is not FrameworkElement root)
                    return false;

                _configurationSource = new SystemBackdrops.SystemBackdropConfiguration
                {
                    IsInputActive = true
                };

                Activated += Window_Activated;
                root.ActualThemeChanged += Window_ThemeChanged;
                SetConfigurationSourceTheme();
            }

            if (_backdropMode == BackdropMode.Acrylic)
            {
                _acrylicController = new SystemBackdrops.DesktopAcrylicController
                {
                    Kind = _preferThinAcrylic ? SystemBackdrops.DesktopAcrylicKind.Thin : SystemBackdrops.DesktopAcrylicKind.Base
                };

                ApplyAcrylicStyle();

                _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                _acrylicController.SetSystemBackdropConfiguration(_configurationSource);
            }
            else
            {
                _micaController = new SystemBackdrops.MicaController
                {
                    Kind = _backdropMode == BackdropMode.MicaAlt ? SystemBackdrops.MicaKind.BaseAlt : SystemBackdrops.MicaKind.Base
                };
                _micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                _micaController.SetSystemBackdropConfiguration(_configurationSource);
            }

            return true;
            }
            catch (COMException)
            {
                DisposeBackdropControllers();
                TryEnableSystemBackdrop();
                return false;
            }
            catch (InvalidOperationException)
            {
                DisposeBackdropControllers();
                TryEnableSystemBackdrop();
                return false;
            }
            finally
            {
                _isApplyingBackdrop = false;
            }
        }

        void DisposeBackdropControllers()
        {
            if (_acrylicController is not null)
            {
                _acrylicController.Dispose();
                _acrylicController = null;
            }

            if (_micaController is not null)
            {
                _micaController.Dispose();
                _micaController = null;
            }
        }

        void PowerManager_EnergySaverStatusChanged(object sender, object e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isPowerSavingMode = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
                ApplyAcrylicStyle();
            });
        }

        void ApplyAcrylicStyle()
        {
            if (_backdropMode != BackdropMode.Acrylic || _acrylicController is null || Content is not FrameworkElement root)
                return;

            bool isDark = root.ActualTheme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
            };

            _acrylicController.Kind = _isPowerSavingMode
                ? SystemBackdrops.DesktopAcrylicKind.Base
                : (_preferThinAcrylic ? SystemBackdrops.DesktopAcrylicKind.Thin : SystemBackdrops.DesktopAcrylicKind.Base);

            var tint = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1E, 0x1F, 0x22)
                : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEE, 0xEF, 0xF2);

            _acrylicController.TintColor = tint;
            _acrylicController.FallbackColor = tint;
            _acrylicController.TintOpacity = _isPowerSavingMode ? Math.Min(1.0f, _acrylicTintOpacity + 0.16f) : _acrylicTintOpacity;
            _acrylicController.LuminosityOpacity = _isPowerSavingMode ? Math.Min(1.0f, _acrylicLuminosityOpacity + 0.11f) : _acrylicLuminosityOpacity;
        }

        void BackdropModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackdropModeCombo is null)
                return;

            if (BackdropModeCombo.SelectedItem is not ComboBoxItem item)
                return;

            if (AcrylicSettingsPanel is null)
                return;

            _backdropMode = item.Tag?.ToString() switch
            {
                "Mica" => BackdropMode.Mica,
                "MicaAlt" => BackdropMode.MicaAlt,
                _ => BackdropMode.Acrylic
            };

            AcrylicSettingsPanel.Visibility = _backdropMode == BackdropMode.Acrylic
                ? Visibility.Visible
                : Visibility.Collapsed;

            ApplyBackdropController();
        }

        void AcrylicThinToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AcrylicThinToggle is null)
                return;

            _preferThinAcrylic = AcrylicThinToggle.IsOn;
            ApplyAcrylicStyle();
        }

        void TintOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _acrylicTintOpacity = (float)e.NewValue;
            if (TintOpacityLabel is not null)
                TintOpacityLabel.Text = $"Tint opacity: {_acrylicTintOpacity:F2}";
            ApplyAcrylicStyle();
        }

        void LuminosityOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _acrylicLuminosityOpacity = (float)e.NewValue;
            if (LuminosityOpacityLabel is not null)
                LuminosityOpacityLabel.Text = $"Luminosity opacity: {_acrylicLuminosityOpacity:F2}";
            ApplyAcrylicStyle();
        }

        void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (_configurationSource is not null)
            {
                _configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
            }
        }

        void Window_ThemeChanged(FrameworkElement sender, object args)
        {
            if (_configurationSource is not null)
            {
                SetConfigurationSourceTheme();
            }

            ApplyAcrylicStyle();
            ApplyTitleBarColors();
        }

        void SetConfigurationSourceTheme()
        {
            if (_configurationSource is null)
                return;

            _configurationSource.Theme = ((FrameworkElement)Content).ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdrops.SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdrops.SystemBackdropTheme.Light,
                _ => SystemBackdrops.SystemBackdropTheme.Default
            };
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            DisposeBackdropControllers();

            PowerManager.EnergySaverStatusChanged -= PowerManager_EnergySaverStatusChanged;

            Activated -= Window_Activated;
            if (Content is FrameworkElement root)
            {
                root.ActualThemeChanged -= Window_ThemeChanged;
            }

            _configurationSource = null;
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
                DetailsView.Visibility = Visibility.Collapsed;
                SettingsView.Visibility = Visibility.Collapsed;

                if (tag == "Charts")
                {
                    ChartsView.Visibility = Visibility.Visible;
                    UpdateCharts();
                }
                else if (tag == "GPU")
                {
                    GpuView.Visibility = Visibility.Visible;
                }
                else if (tag == "Details")
                {
                    DetailsView.Visibility = Visibility.Visible;
                }
                else if (tag == "Settings")
                {
                    SettingsView.Visibility = Visibility.Visible;
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
            
            _cpuChart.UpdateData([data.Select(x => new ObservablePoint(x.Timestamp.Ticks, x.TotalCpuUsage))]);
            _memChart.UpdateData([data.Select(x => new ObservablePoint(x.Timestamp.Ticks, (x.TotalMemory - x.AvailableMemory) / 1024.0))]);
            _diskChart.UpdateData([
                data.Select(x => new ObservablePoint(x.Timestamp.Ticks, x.TotalDiskReadBytesPerSec / (1024.0 * 1024.0))),
                data.Select(x => new ObservablePoint(x.Timestamp.Ticks, x.TotalDiskWriteBytesPerSec / (1024.0 * 1024.0)))
            ]);
            _gpuUtilChart.UpdateData([data.Where(x => x.GpuUtilization.HasValue).Select(x => new ObservablePoint(x.Timestamp.Ticks, x.GpuUtilization!.Value))]);
            _gpuMemChart.UpdateData([data.Where(x => x.GpuMemoryUsed.HasValue).Select(x => new ObservablePoint(x.Timestamp.Ticks, x.GpuMemoryUsed!.Value))]);
            _gpuTempChart.UpdateData([data.Where(x => x.GpuTemperature.HasValue).Select(x => new ObservablePoint(x.Timestamp.Ticks, x.GpuTemperature!.Value))]);
            _procChart.UpdateData([data.Select(x => new ObservablePoint(x.Timestamp.Ticks, x.TotalProcesses))]);
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
        {
            _searchText = SearchBox.Text.Trim();
            ApplySearchFilter();
        }

        void ShowMetricTeachingTip(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement target)
                return;

            var (title, description, typical) = (target.Tag as string) switch
            {
                "Temperature" => ("CPU Temperature", "Sustained high temperature can indicate insufficient cooling, dust buildup, or heavy CPU load.", "~35-55°C idle, ~60-85°C under load (depends on CPU/cooling)."),
                "ContextSwitches" => ("Context Switches", "A high number of context switches means threads are frequently swapped, which can add overhead and reduce performance.", "Often a few thousand to tens of thousands per second."),
                "Interrupts" => ("Interrupts", "Interrupts are hardware signals. A sudden increase may suggest a driver or device issue.", "Usually hundreds to a few thousands per second."),
                "SystemCalls" => ("System Calls", "System calls are requests from applications to the OS kernel. High values are common during intensive I/O activity.", "Can range from thousands to tens of thousands per second depending on workload."),
                "CpuQueue" => ("CPU Queue", "CPU queue length shows how many tasks are waiting for CPU time. A consistently high value may indicate CPU saturation.", "Around 0-1 is healthy; sustained >2 per logical CPU can indicate pressure."),
                "CpuTimeBreakdown" => ("CPU Time Breakdown", "Shows where CPU time is spent: user mode, kernel mode, DPC, and IRQ. Useful for identifying the source of load.", "User often dominates on app load, Kernel often lower; DPC/IRQ usually low (often <5% each)."),
                "IdleTime" => ("Idle Time", "High idle time means the CPU has available capacity. Low idle time over longer periods usually indicates sustained load.", "Office/idle usage often >70%, heavy workloads can stay <20%."),
                "CpuParking" => ("CPU Parking", "CPU parking disables some logical processors to save power. Under heavier load, parked cores should typically become unparked.", "More parked cores at idle, fewer (or none) under sustained load."),
                _ => ("Metric", "This metric shows the current system state and helps identify performance bottlenecks.", "Values depend on hardware, power profile, and workload.")
            };

            DetailsMetricTeachingTip.Title = title;
            DetailsMetricTeachingTip.Subtitle = description;
            var contentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            contentText.Inlines.Add(new Run { Text = "Typical: ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            contentText.Inlines.Add(new Run { Text = typical });
            DetailsMetricTeachingTip.Content = contentText;
            DetailsMetricTeachingTip.Target = target;
            DetailsMetricTeachingTip.IsOpen = true;
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
                    var perCoreStats = ProcessCollector.GetPerCoreCpuStats();

                    var (totalMem, availMem) = ProcessCollector.GetMemoryUsage();
                    var currentNetworkUsage = ProcessCollector.GetNetworkUsage();
                    long networkReceivedPerSec = Math.Max(0, currentNetworkUsage.BytesReceived - _previousNetworkUsage.BytesReceived);
                    long networkSentPerSec = Math.Max(0, currentNetworkUsage.BytesSent - _previousNetworkUsage.BytesSent);
                    _previousNetworkUsage = currentNetworkUsage;
                    
                    var cpuTemp = ProcessCollector.TryGetCpuTemperatureC();
                    string cpuTempStr = cpuTemp.HasValue ? $"{cpuTemp.Value:F0} °C" : "N/A";
                    float ctxSwitches = ProcessCollector.GetSystemContextSwitchesPerSec();
                    float interrupts = ProcessCollector.GetSystemInterruptsPerSec();
                    float syscalls = ProcessCollector.GetSystemSyscallsPerSec();
                    float cpuQueue = ProcessCollector.GetProcessorQueueLength();
                    var (userTime, privilegedTime, dpcTime, interruptTime) = ProcessCollector.GetCpuTimeBreakdownPercent();
                    float idleTime = ProcessCollector.GetCpuIdleTimePercent();
                    var (parkedLogicalProcessors, totalLogicalProcessors) = ProcessCollector.GetCpuParkingStatus();

                    CpuTemperatureText = $"CPU Temp: {cpuTempStr}";
                    ContextSwitchesText = $"Context Switches: {(int)ctxSwitches}/s";
                    InterruptsText = $"Interrupts: {(int)interrupts}/s";
                    SyscallsText = $"System Calls: {(int)syscalls}/s";
                    CpuQueueText = $"CPU Queue: {cpuQueue:F1}";
                    CpuTimeBreakdownText = $"CPU Time: U {userTime:F1}% | K {privilegedTime:F1}% | DPC {dpcTime:F1}% | IRQ {interruptTime:F1}%";
                    CpuIdleTimeText = $"CPU Idle: {idleTime:F1}%";
                    CpuParkingText = totalLogicalProcessors > 0
                        ? $"CPU Parking: {parkedLogicalProcessors}/{totalLogicalProcessors} parked"
                        : "CPU Parking: N/A";
                    SystemCpuText = $"CPU {totalCpuUsage:F1}% @ {cpuFreq / 1000.0:F2} GHz";
                    UpdateCpuCoreMetrics(perCoreStats);

                    uint? ramFreq = ProcessCollector.GetRamFrequencyMhz();
                    RamFrequencyText = ramFreq.HasValue && ramFreq.Value > 0 ? $"Speed: {ramFreq.Value} MHz" : "Speed: N/A";
                    MemoryUsageText = $"{FormatBytes((ulong)Math.Max(0, totalMem - availMem) * 1024 * 1024)} / {FormatBytes((ulong)totalMem * 1024 * 1024)}";
                    
                    NetworkUsageText = $"\u2193{FormatBytes((ulong)networkReceivedPerSec)}/s \u2191{FormatBytes((ulong)networkSentPerSec)}/s";
                    LastUpdatedText = $"Updated {DateTime.Now:HH:mm:ss}";

                    long totalDiskRead = 0;
                    long totalDiskWrite = 0;
                    ulong totalDiskSize = 0;
                    ulong totalFreeSpace = 0;
                    for (int i = 0; i < _diskMetricsList.Count; i++)
                    {
                        var d = _diskMetricsList[i];
                        d.Update();
                        totalDiskRead += (long)Math.Max(0, d.ReadSpeed);
                        totalDiskWrite += (long)Math.Max(0, d.WriteSpeed);
                        totalDiskSize += (ulong)d.TotalSize;
                        totalFreeSpace += (ulong)d.FreeSpace;
                        if (i < DisksList.Count)
                        {
                            DisksList[i].CapacityText = $"{FormatBytes((ulong)Math.Max(0, d.TotalSize - d.FreeSpace))} / {FormatBytes((ulong)d.TotalSize)}";
                            DisksList[i].ActivityText = $"R: {FormatBytes((ulong)Math.Max(0, d.ReadSpeed))}/s  W: {FormatBytes((ulong)Math.Max(0, d.WriteSpeed))}/s ({d.UsagePercentage:F1}%)";
                            DisksList[i].UsagePercent = Math.Max(0, Math.Min(100, d.UsagePercentage));
                            DisksList[i].UpdateUsageAndActivity();
                        }
                    }
                    string diskNames = _diskMetricsList.Count > 0 ? string.Join(", ", _diskMetricsList.Select(d => d.Name)) : "Disk";
                    DiskActivitySummary = $"{FormatBytes((ulong)Math.Max(0, totalDiskSize - totalFreeSpace))} / {FormatBytes((ulong)totalDiskSize)}";
                    DiskReadWriteText = $"\u2193{FormatBytes((ulong)totalDiskRead)}/s \u2191{FormatBytes((ulong)totalDiskWrite)}/s";
                    DiskNamesText = diskNames;

                    snapshot = await Task.Run(() => ProcessCollector.GetTopProcesses(0));
                    _lastSnapshot = snapshot;
                    
                    var gpuInfo = await Task.Run(() => ProcessCollector.TryQueryGpuInfo());

                    UpdateGpuView(gpuInfo, snapshot);

                    await Task.Run(() => Charts.Databse.SaveData(DbPath, totalCpuUsage, totalMem, availMem, gpuInfo, snapshot, totalDiskRead, totalDiskWrite));
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
            string integratedLabel = string.IsNullOrEmpty(gpuInfo.IntegratedGpuName)
                ? "Integrated GPU"
                : gpuInfo.IntegratedGpuName;
            string integratedTempSuffix = gpuInfo.IntegratedTemperatureC.HasValue
                ? $" | GPU Temp: {gpuInfo.IntegratedTemperatureC.Value:F0} °C"
                : " | GPU Temp: N/A";

            if (integratedPids.Count == 0)
            {
                _integratedGpuProcesses.Clear();
                NoIntegratedGpuResultsVisibility = Visibility.Visible;
                IntegratedGpuStatusText = $"GPU: {integratedLabel}{integratedTempSuffix}";
            }
            else
            {
                NoIntegratedGpuResultsVisibility = Visibility.Collapsed;
                IntegratedGpuStatusText = $"GPU: {integratedLabel}{integratedTempSuffix}";

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
                        TemperatureText = gpuInfo.IntegratedTemperatureC.HasValue ? $"{gpuInfo.IntegratedTemperatureC.Value:F0} °C" : "N/A"
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

        void UpdateCpuCoreMetrics(IReadOnlyList<ProcessCollector.CpuCoreSnapshot> perCoreStats)
        {
            for (int i = 0; i < perCoreStats.Count; i++)
            {
                var core = perCoreStats[i];
                if (i < CpuCoreMetrics.Count)
                {
                    var vm = CpuCoreMetrics[i];
                    vm.CoreLabel = $"Core {core.CoreIndex}";
                    vm.CoreName = FormatCoreName(core.CoreName);
                    vm.UsageText = $"{core.UsagePercent:F1}%";
                    vm.UsagePercent = Math.Max(0, Math.Min(100, core.UsagePercent));
                    vm.FrequencyText = core.FrequencyMhz > 0 ? $"{core.FrequencyMhz / 1000.0:F2} GHz" : "N/A";
                }
                else
                {
                    CpuCoreMetrics.Add(new CpuCoreMetricViewModel
                    {
                        CoreLabel = $"Core {core.CoreIndex}",
                        CoreName = FormatCoreName(core.CoreName),
                        UsageText = $"{core.UsagePercent:F1}%",
                        UsagePercent = Math.Max(0, Math.Min(100, core.UsagePercent)),
                        FrequencyText = core.FrequencyMhz > 0 ? $"{core.FrequencyMhz / 1000.0:F2} GHz" : "N/A"
                    });
                }
            }

            while (CpuCoreMetrics.Count > perCoreStats.Count)
            {
                CpuCoreMetrics.RemoveAt(CpuCoreMetrics.Count - 1);
            }
        }

        static string FormatCoreName(string? coreName)
        {
            if (string.IsNullOrWhiteSpace(coreName))
                return "N/A";

            var parts = coreName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int group) && int.TryParse(parts[1], out int logicalProcessor))
            {
                return $"Group {group} • #{logicalProcessor}";
            }

            if (parts.Length == 1 && int.TryParse(parts[0], out int logicalOnly))
            {
                return $"#{logicalOnly}";
            }

            return coreName;
        }

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
            const double alpha = 0.2;
            
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

            if (_targetDiskRead != ulong.MaxValue && Math.Abs(_targetDiskRead - _currentDiskRead) > 0)
            {
                _currentDiskRead += (_targetDiskRead - _currentDiskRead) * alpha;
                DiskReadText = FormatBytes((ulong)_currentDiskRead);
            }

            if (_targetDiskWrite != ulong.MaxValue && Math.Abs(_targetDiskWrite - _currentDiskWrite) > 0)
            {
                _currentDiskWrite += (_targetDiskWrite - _currentDiskWrite) * alpha;
                DiskWriteText = FormatBytes((ulong)_currentDiskWrite);
            }

            if (_targetNetRecv != ulong.MaxValue && Math.Abs(_targetNetRecv - _currentNetRecv) > 0)
            {
                _currentNetRecv += (_targetNetRecv - _currentNetRecv) * alpha;
                NetRecvText = FormatBytes((ulong)_currentNetRecv);
            }

            if (_targetNetSent != ulong.MaxValue && Math.Abs(_targetNetSent - _currentNetSent) > 0)
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

    public sealed class CpuCoreMetricViewModel : INotifyPropertyChanged
    {
        string _coreLabel = string.Empty;
        string _coreName = string.Empty;
        string _usageText = string.Empty;
        double _usagePercent;
        string _frequencyText = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public string CoreLabel { get => _coreLabel; set => SetField(ref _coreLabel, value); }
        public string CoreName { get => _coreName; set => SetField(ref _coreName, value); }
        public string UsageText { get => _usageText; set => SetField(ref _usageText, value); }
        public double UsagePercent { get => _usagePercent; set => SetField(ref _usagePercent, value); }
        public string FrequencyText { get => _frequencyText; set => SetField(ref _frequencyText, value); }
    }

    public sealed class DiskViewModel : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string DiskType { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;

        string _capacityText = string.Empty;
        string _activityText = string.Empty;
        double _usagePercent;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void UpdateUsageAndActivity()
        {
            OnPropertyChanged(nameof(CapacityText));
            OnPropertyChanged(nameof(ActivityText));
        }

        void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public string CapacityText { get => _capacityText; set => SetField(ref _capacityText, value); }
        public string ActivityText { get => _activityText; set => SetField(ref _activityText, value); }
        public double UsagePercent { get => _usagePercent; set => SetField(ref _usagePercent, value); }
    }

    public sealed class MetricChartViewModel : INotifyPropertyChanged
    {
        public string Title { get; }
        public ISeries[] Series { get; }
        public LiveChartsCore.Kernel.Sketches.ICartesianAxis[] XAxes { get; }
        public LiveChartsCore.Kernel.Sketches.ICartesianAxis[] YAxes { get; }
        readonly Axis _yAxis;
        readonly bool _integerAxis;
        readonly bool _adaptiveLogarithmicAxis;
        readonly string _yLabelSuffix;
        bool _useLogScale;
        public SolidColorPaint LegendTextPaint { get; } = new SolidColorPaint(SKColors.White);

        public event PropertyChangedEventHandler? PropertyChanged;

        public MetricChartViewModel(string title, (SKColor color, string name)[] seriesOptions, bool integerAxis = false, string yLabelSuffix = "", bool adaptiveLogarithmicAxis = false)
        {
            Title = title;
            _integerAxis = integerAxis;
            _adaptiveLogarithmicAxis = adaptiveLogarithmicAxis;
            _yLabelSuffix = yLabelSuffix;
            
            var sList = new List<ISeries>();
            foreach (var opt in seriesOptions)
            {
                sList.Add(new LineSeries<ObservablePoint> { 
                    Name = opt.name,
                    Values = [], 
                    Fill = new SolidColorPaint(opt.color.WithAlpha(50)),
                    Stroke = new SolidColorPaint(opt.color) { StrokeThickness = 2 },
                    GeometrySize = 0, 
                    LineSmoothness = 0 
                });
            }
            Series = sList.ToArray();

            XAxes = [
                new Axis 
                { 
                    Labeler = value => new DateTime((long)value).ToString("HH:mm:ss"),
                    LabelsRotation = 15
                }
            ];

            _yAxis = new Axis
            {
                Labeler = value => $"{Math.Round(value):0}{yLabelSuffix}"
            };
            YAxes = [
                _yAxis
            ];
        }

        public void UpdateData(IEnumerable<ObservablePoint>[] data)
        {
            var allYVals = new List<double>();
            bool hasAnyData = false;

            var seriesPoints = new ObservablePoint[data.Length][];

            for (int i = 0; i < data.Length; i++)
            {
                seriesPoints[i] = data[i].ToArray();
            }

            if (_adaptiveLogarithmicAxis)
            {
                var positive = seriesPoints
                    .SelectMany(points => points)
                    .Where(p => p.Y.HasValue && p.Y.Value > 0)
                    .Select(p => p.Y!.Value)
                    .OrderBy(v => v)
                    .ToArray();

                if (positive.Length > 0)
                {
                    double median = positive[positive.Length / 2];
                    double maxPositive = positive[^1];
                    _useLogScale = median > 0 && maxPositive >= Math.Max(10, median * 20);
                }
                else
                {
                    _useLogScale = false;
                }

                _yAxis.Labeler = _useLogScale
                    ? value =>
                    {
                        double original = Math.Pow(10, value) - 1;
                        return $"{Math.Round(original):0}{_yLabelSuffix}";
                    }
                    : value => $"{Math.Round(value):0}{_yLabelSuffix}";
            }
            else
            {
                _useLogScale = false;
            }

            for (int i = 0; i < Series.Length && i < data.Length; i++)
            {
                var points = seriesPoints[i];
                if (_useLogScale)
                {
                    points = points
                        .Select(p => new ObservablePoint(
                            p.X,
                            p.Y.HasValue ? Math.Log10(Math.Max(0, p.Y.Value) + 1) : null))
                        .ToArray();
                }

                if (points.Length > 0)
                {
                    hasAnyData = true;
                    allYVals.AddRange(points.Where(p => p.Y.HasValue).Select(p => p.Y!.Value));
                }
                var lineSeries = (LineSeries<ObservablePoint>)Series[i];
                lineSeries.Values = points;
            }

            if (!hasAnyData || allYVals.Count == 0)
            {
                _yAxis.MinLimit = null;
                _yAxis.MaxLimit = null;
                return;
            }

            var yValues = allYVals.ToArray();

            double min = yValues.Min();
            double max = yValues.Max();
            double range = max - min;
            double padding = range > 0 ? range * 0.1 : Math.Max(Math.Abs(max) * 0.1, 1);

            double minLimit = min - padding;
            double maxLimit = max + padding;

            if (min >= 0)
            {
                minLimit = Math.Max(0, minLimit);
            }

            minLimit = Math.Floor(minLimit);
            maxLimit = Math.Ceiling(maxLimit);

            if (maxLimit <= minLimit)
            {
                maxLimit = minLimit + 1;
            }

            _yAxis.MinLimit = minLimit;
            _yAxis.MaxLimit = maxLimit;
        }
    }
}
