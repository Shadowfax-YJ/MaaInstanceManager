using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MaaInstanceManager;

public partial class MainWindow : INotifyPropertyChanged
{
    private const string ExecutableName = "MAA.exe";
    private const string DefaultConfigurationName = "Default";
    private const string DefaultGroupName = "默认";
    private const string AllGroupsFilter = "全部";
    private const string ConnectAddressKey = "Connect.Address";
    private const string ConnectAddressHistoryKey = "Connect.AddressHistory";
    private const string DefaultReleaseRepositoryUrl = "https://github.com/MaaAssistantArknights/MaaAssistantArknights.git";

    private readonly string _configDirectory = Path.Combine(AppContext.BaseDirectory, "config");
    private readonly string _stateFile;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private static readonly HttpClient ReleaseHttpClient = CreateReleaseHttpClient();

    private string _releaseRepositoryUrl = DefaultReleaseRepositoryUrl;
    private string _releaseCacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache", "releases");
    private string _selectedVersion = string.Empty;
    private string _releasePackagePath = string.Empty;
    private string _workspaceRoot = Path.Combine(AppContext.BaseDirectory, "managed_instances");
    private string _instanceNamePrefix = "MAA-";
    private string _targetGroup = DefaultGroupName;
    private string _selectedGroupFilter = AllGroupsFilter;
    private int _newInstanceCount = 1;
    private int _cloneCount = 1;
    private int _startAdbPort = 16384;
    private int _portStep = 32;
    private string _statusMessage = "就绪";
    private ManagedInstance? _selectedInstance;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        _stateFile = Path.Combine(_configDirectory, "instance_manager.json");
        Instances.CollectionChanged += InstancesCollectionChanged;
        LoadState();
        RefreshGroupOptions();
        ApplyGroupFilter();
        DataContext = this;
        RefreshInstances();

        _refreshTimer.Tick += (_, _) => RefreshRunningStates();
        _refreshTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ManagedInstance> Instances { get; } = [];

    public ObservableCollection<string> AvailableVersions { get; } = [];

    public ObservableCollection<string> Groups { get; } = [AllGroupsFilter];

    public string ReleaseRepositoryUrl
    {
        get => _releaseRepositoryUrl;
        set {
            if (SetField(ref _releaseRepositoryUrl, value))
            {
                SaveState();
            }
        }
    }

    public string ReleaseCacheDirectory
    {
        get => _releaseCacheDirectory;
        set {
            if (SetField(ref _releaseCacheDirectory, value))
            {
                SaveState();
            }
        }
    }

    public string SelectedVersion
    {
        get => _selectedVersion;
        set {
            if (SetField(ref _selectedVersion, value))
            {
                SaveState();
            }
        }
    }

    public string ReleasePackagePath
    {
        get => _releasePackagePath;
        set {
            if (SetField(ref _releasePackagePath, value))
            {
                SaveState();
            }
        }
    }

    public string WorkspaceRoot
    {
        get => _workspaceRoot;
        set {
            if (SetField(ref _workspaceRoot, value))
            {
                SaveState();
            }
        }
    }

    public string InstanceNamePrefix
    {
        get => _instanceNamePrefix;
        set {
            if (SetField(ref _instanceNamePrefix, value))
            {
                SaveState();
            }
        }
    }

    public string TargetGroup
    {
        get => _targetGroup;
        set {
            if (SetField(ref _targetGroup, value))
            {
                SaveState();
            }
        }
    }

    public string SelectedGroupFilter
    {
        get => _selectedGroupFilter;
        set {
            if (SetField(ref _selectedGroupFilter, string.IsNullOrWhiteSpace(value) ? AllGroupsFilter : value))
            {
                ApplyGroupFilter();
                SaveState();
            }
        }
    }

    public int NewInstanceCount
    {
        get => _newInstanceCount;
        set {
            if (SetField(ref _newInstanceCount, Math.Clamp(value, 1, 999)))
            {
                SaveState();
            }
        }
    }

    public int CloneCount
    {
        get => _cloneCount;
        set {
            if (SetField(ref _cloneCount, Math.Clamp(value, 1, 999)))
            {
                SaveState();
            }
        }
    }

    public int StartAdbPort
    {
        get => _startAdbPort;
        set {
            if (SetField(ref _startAdbPort, Math.Clamp(value, 1, 65535)))
            {
                SaveState();
            }
        }
    }

    public int PortStep
    {
        get => _portStep;
        set {
            if (SetField(ref _portStep, Math.Clamp(value, 1, 65535)))
            {
                SaveState();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public ManagedInstance? SelectedInstance
    {
        get => _selectedInstance;
        set {
            if (SetField(ref _selectedInstance, value))
            {
                NotifySummaryProperties();
            }
        }
    }

    public int TotalCount => Instances.Count;

    public int SelectedCount => Instances.Count(static instance => instance.IsSelected);

    public int RunningCount => Instances.Count(static instance => instance.IsRunning);

    private async void CreateInstancesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateReleasePackage() || !ValidateWorkspaceRoot())
        {
            return;
        }

        var count = NewInstanceCount;
        await RunBusyAsync($"正在从 release 创建 {count} 个实例...", () => Task.Run(() => {
            var plans = BuildCreationPlan(count);
            var created = new List<ManagedInstance>(plans.Count);
            foreach (var plan in plans)
            {
                try
                {
                    ZipFile.ExtractToDirectory(ReleasePackagePath, plan.DirectoryPath);
                    NormalizeExtractedReleaseRoot(plan.DirectoryPath);
                    EnsureExecutableExists(plan.DirectoryPath);
                    ApplyAdbPort(plan.DirectoryPath, plan.AdbPort);
                    created.Add(CreateInstance(plan.Name, plan.DirectoryPath, plan.AdbPort, NormalizeGroupName(TargetGroup), "已创建"));
                }
                catch
                {
                    TryDeleteDirectory(plan.DirectoryPath);
                    throw;
                }
            }

            Dispatcher.Invoke(() => {
                foreach (var instance in created)
                {
                    Instances.Add(instance);
                }

                SaveState();
                RefreshInstances();
                StatusMessage = $"已创建 {created.Count} 个实例";
            });
        }));
    }

    private async void CloneSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateWorkspaceRoot())
        {
            return;
        }

        var source = SelectedInstance;
        if (source is null)
        {
            StatusMessage = "请选择一个已经手动配置好的源实例";
            return;
        }

        RefreshRunningStates();
        if (source.IsRunning)
        {
            StatusMessage = "源实例正在运行，请先关闭后再复制";
            return;
        }

        if (!Directory.Exists(source.DirectoryPath) || !File.Exists(GetExecutablePath(source.DirectoryPath)))
        {
            StatusMessage = "源实例目录无效";
            return;
        }

        var count = CloneCount;
        await RunBusyAsync($"正在复制 {count} 个实例...", () => Task.Run(() => {
            var plans = BuildCreationPlan(count);
            var created = new List<ManagedInstance>(plans.Count);
            foreach (var plan in plans)
            {
                try
                {
                    CopyDirectory(source.DirectoryPath, plan.DirectoryPath);
                    ApplyAdbPort(plan.DirectoryPath, plan.AdbPort);
                    created.Add(CreateInstance(plan.Name, plan.DirectoryPath, plan.AdbPort, NormalizeGroupName(TargetGroup), "已复制"));
                }
                catch
                {
                    TryDeleteDirectory(plan.DirectoryPath);
                    throw;
                }
            }

            Dispatcher.Invoke(() => {
                foreach (var instance in created)
                {
                    Instances.Add(instance);
                }

                SaveState();
                RefreshInstances();
                StatusMessage = $"已复制 {created.Count} 个实例";
            });
        }));
    }

    private async void RemapPortsButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedOrAllInstances()
            .OrderBy(static instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "没有可映射的实例";
            return;
        }

        await RunBusyAsync("正在按顺序重新映射 ADB 端口...", () => Task.Run(() => {
            for (var index = 0; index < targets.Count; index++)
            {
                var port = StartAdbPort + (index * PortStep);
                ApplyAdbPort(targets[index].DirectoryPath, port);
                Dispatcher.Invoke(() => {
                    targets[index].AdbPort = port;
                    targets[index].Status = "端口已更新";
                });
            }

            Dispatcher.Invoke(() => {
                SaveState();
                RefreshInstances();
                StatusMessage = $"已映射 {targets.Count} 个实例";
            });
        }));
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstances();
        StatusMessage = "已刷新";
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var instance in Instances)
        {
            instance.IsSelected = true;
        }

        NotifySummaryProperties();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var instance in Instances)
        {
            instance.IsSelected = false;
        }

        NotifySummaryProperties();
    }

    private async void StartSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await StartInstances(GetSelectedOrFocusedInstances());
    }

    private async void StopSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await StopInstances(GetSelectedOrFocusedInstances());
    }

    private async void StartAllButton_Click(object sender, RoutedEventArgs e)
    {
        await StartInstances(Instances.ToList());
    }

    private async void StopAllButton_Click(object sender, RoutedEventArgs e)
    {
        await StopInstances(Instances.ToList());
    }

    private void SelectReleasePackageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MAA Release (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (File.Exists(ReleasePackagePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(ReleasePackagePath);
            dialog.FileName = Path.GetFileName(ReleasePackagePath);
        }

        if (dialog.ShowDialog(this) == true)
        {
            ReleasePackagePath = dialog.FileName;
        }
    }

    private async void RefreshReleaseVersionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ReleaseRepositoryUrl))
        {
            StatusMessage = "请填写本体 Git 仓库";
            return;
        }

        await RunBusyAsync("正在获取本体版本列表...", async () => {
            var versions = await FetchRepositoryVersionsAsync(ReleaseRepositoryUrl);
            Dispatcher.Invoke(() => {
                AvailableVersions.Clear();
                foreach (var version in versions)
                {
                    AvailableVersions.Add(version);
                }

                if (AvailableVersions.Count > 0 && !AvailableVersions.Contains(SelectedVersion))
                {
                    SelectedVersion = AvailableVersions[0];
                }

                StatusMessage = AvailableVersions.Count == 0 ? "未找到版本标签" : $"已获取 {AvailableVersions.Count} 个版本";
            });
        });
    }

    private async void DownloadSelectedReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedVersion))
        {
            StatusMessage = "请选择要下载的本体版本";
            return;
        }

        if (string.IsNullOrWhiteSpace(ReleaseCacheDirectory))
        {
            StatusMessage = "请选择缓存目录";
            return;
        }

        await RunBusyAsync($"正在下载 {SelectedVersion}...", async () => {
            var packagePath = await DownloadReleasePackageAsync(ReleaseRepositoryUrl, SelectedVersion, ReleaseCacheDirectory);
            Dispatcher.Invoke(() => {
                ReleasePackagePath = packagePath;
                StatusMessage = $"已缓存 {SelectedVersion}: {packagePath}";
            });
        });
    }

    private void SelectWorkspaceRootButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择实例总目录",
            InitialDirectory = Directory.Exists(WorkspaceRoot) ? WorkspaceRoot : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            WorkspaceRoot = dialog.FolderName;
            RefreshInstances();
        }
    }

    private void SelectReleaseCacheDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择本体缓存目录",
            InitialDirectory = Directory.Exists(ReleaseCacheDirectory) ? ReleaseCacheDirectory : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ReleaseCacheDirectory = dialog.FolderName;
        }
    }

    private void OpenSelectedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedInstance;
        if (target is null || !Directory.Exists(target.DirectoryPath))
        {
            StatusMessage = "请选择一个有效实例";
            return;
        }

        OpenFolder(target.DirectoryPath);
    }

    private void OpenWorkspaceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(WorkspaceRoot);
        OpenFolder(WorkspaceRoot);
    }

    private void SetSelectedGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedOrFocusedInstances();
        if (targets.Count == 0)
        {
            StatusMessage = "请选择要设置分组的实例";
            return;
        }

        var group = NormalizeGroupName(TargetGroup);
        foreach (var instance in targets)
        {
            instance.Group = group;
            instance.Status = "分组已更新";
        }

        SaveState();
        RefreshGroupOptions();
        ApplyGroupFilter();
        StatusMessage = $"已将 {targets.Count} 个实例设置为 {group}";
    }

    private async void PackageSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedOrFocusedInstances().ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "请选择要打包的实例";
            return;
        }

        RefreshRunningStates();
        var running = targets.Where(static instance => instance.IsRunning).Select(static instance => instance.Name).ToList();
        if (running.Count > 0)
        {
            StatusMessage = "请先关闭运行中的实例: " + string.Join(", ", running);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = "zip",
            OverwritePrompt = true,
            FileName = $"MAAInstances-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            InitialDirectory = Directory.Exists(WorkspaceRoot) ? WorkspaceRoot : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        if (targets.Any(instance => IsPathInsideDirectory(dialog.FileName, instance.DirectoryPath)))
        {
            StatusMessage = "压缩包不能保存到被打包的实例目录内";
            return;
        }

        await RunBusyAsync($"正在打包 {targets.Count} 个实例...", () => Task.Run(() => {
            var packagePath = dialog.FileName;
            var parent = Path.GetDirectoryName(packagePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
            foreach (var instance in targets)
            {
                AddDirectoryToArchive(archive, instance.DirectoryPath, instance.Name);
                Dispatcher.Invoke(() => instance.Status = "已打包");
            }

            Dispatcher.Invoke(() => StatusMessage = $"已打包到 {packagePath}");
        }));
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedOrFocusedInstances().ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "请选择要删除的实例";
            return;
        }

        RefreshRunningStates();
        var running = targets.Where(static instance => instance.IsRunning).Select(static instance => instance.Name).ToList();
        if (running.Count > 0)
        {
            StatusMessage = "请先关闭运行中的实例: " + string.Join(", ", running);
            return;
        }

        var preview = string.Join(", ", targets.Take(6).Select(static instance => instance.Name));
        if (targets.Count > 6)
        {
            preview += $" 等 {targets.Count} 个实例";
        }

        var result = MessageBox.Show(
            this,
            $"将从列表和磁盘删除: {preview}\n\n此操作不可恢复，是否继续？",
            "删除实例",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync($"正在删除 {targets.Count} 个实例...", () => Task.Run(() => {
            var deleted = new List<ManagedInstance>();
            foreach (var instance in targets)
            {
                if (Directory.Exists(instance.DirectoryPath))
                {
                    Directory.Delete(instance.DirectoryPath, recursive: true);
                }

                deleted.Add(instance);
            }

            Dispatcher.Invoke(() => {
                foreach (var instance in deleted)
                {
                    Instances.Remove(instance);
                }

                SaveState();
                RefreshGroupOptions();
                ApplyGroupFilter();
                StatusMessage = $"已删除 {deleted.Count} 个实例";
            });
        }));
    }

    private async Task StartInstances(IReadOnlyCollection<ManagedInstance> targets)
    {
        if (targets.Count == 0)
        {
            StatusMessage = "请选择要启动的实例";
            return;
        }

        await RunBusyAsync("正在启动实例...", () => Task.Run(() => {
            foreach (var instance in targets)
            {
                StartInstance(instance);
            }

            Dispatcher.Invoke(() => {
                RefreshRunningStates();
                StatusMessage = $"已处理 {targets.Count} 个启动请求";
            });
        }));
    }

    private async Task StopInstances(IReadOnlyCollection<ManagedInstance> targets)
    {
        if (targets.Count == 0)
        {
            StatusMessage = "请选择要关闭的实例";
            return;
        }

        await RunBusyAsync("正在关闭实例...", () => Task.Run(() => {
            foreach (var instance in targets)
            {
                StopInstance(instance);
            }

            Dispatcher.Invoke(() => {
                RefreshRunningStates();
                StatusMessage = $"已处理 {targets.Count} 个关闭请求";
            });
        }));
    }

    private void StartInstance(ManagedInstance instance)
    {
        try
        {
            using var existing = FindRunningProcess(instance);
            if (existing is not null)
            {
                Dispatcher.Invoke(() => {
                    instance.ProcessId = existing.Id;
                    instance.IsRunning = true;
                    instance.Status = "已经运行";
                });
                return;
            }

            var executablePath = GetExecutablePath(instance.DirectoryPath);
            if (!File.Exists(executablePath))
            {
                Dispatcher.Invoke(() => instance.Status = "未找到 MAA.exe");
                return;
            }

            ApplyAdbPort(instance.DirectoryPath, instance.AdbPort);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = instance.DirectoryPath,
                UseShellExecute = false,
            });

            Dispatcher.Invoke(() => {
                instance.ProcessId = process?.Id;
                instance.IsRunning = process is not null && !process.HasExited;
                instance.Status = instance.IsRunning ? "已启动" : "启动失败";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => instance.Status = "启动失败: " + ex.Message);
        }
    }

    private void StopInstance(ManagedInstance instance)
    {
        try
        {
            using var process = FindRunningProcess(instance);
            if (process is null)
            {
                Dispatcher.Invoke(() => {
                    instance.IsRunning = false;
                    instance.ProcessId = null;
                    instance.Status = "未运行";
                });
                return;
            }

            if (!process.CloseMainWindow())
            {
                process.Kill(entireProcessTree: true);
            }
            else if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
            }

            Dispatcher.Invoke(() => {
                instance.IsRunning = false;
                instance.ProcessId = null;
                instance.Status = "已关闭";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => instance.Status = "关闭失败: " + ex.Message);
        }
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        IsEnabled = false;
        StatusMessage = message;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusMessage = "操作失败: " + ex.Message;
        }
        finally
        {
            IsEnabled = true;
            _isBusy = false;
            NotifySummaryProperties();
        }
    }

    private bool ValidateReleasePackage()
    {
        if (!File.Exists(ReleasePackagePath) || !ReleasePackagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "请选择 MAA release zip";
            return false;
        }

        return true;
    }

    private bool ValidateWorkspaceRoot()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceRoot))
        {
            StatusMessage = "请选择实例总目录";
            return false;
        }

        try
        {
            Directory.CreateDirectory(WorkspaceRoot);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "实例总目录不可用: " + ex.Message;
            return false;
        }
    }

    private List<InstanceCreationPlan> BuildCreationPlan(int count)
    {
        Directory.CreateDirectory(WorkspaceRoot);
        var usedNames = new HashSet<string>(Instances.Select(static instance => instance.Name), StringComparer.OrdinalIgnoreCase);
        var usedPorts = new HashSet<int>(Instances.Select(static instance => instance.AdbPort).Where(static port => port > 0));
        foreach (var directory in Directory.EnumerateDirectories(WorkspaceRoot))
        {
            usedNames.Add(Path.GetFileName(directory));
            if (TryReadAdbPort(directory, out var port) && port > 0)
            {
                usedPorts.Add(port);
            }
        }

        var plan = new List<InstanceCreationPlan>(count);
        for (var index = 0; index < count; index++)
        {
            var name = GetNextInstanceName(usedNames);
            var port = GetNextPort(usedPorts);
            var directory = Path.Combine(WorkspaceRoot, name);
            if (Directory.Exists(directory))
            {
                throw new IOException($"实例目录已存在: {directory}");
            }

            usedNames.Add(name);
            usedPorts.Add(port);
            plan.Add(new InstanceCreationPlan(name, directory, port));
        }

        return plan;
    }

    private string GetNextInstanceName(HashSet<string> usedNames)
    {
        var prefix = SanitizeFileNamePrefix(InstanceNamePrefix);
        for (var index = 1; index < 10000; index++)
        {
            var name = $"{prefix}{index:D3}";
            if (!usedNames.Contains(name))
            {
                return name;
            }
        }

        throw new InvalidOperationException("无法生成新的实例名称");
    }

    private int GetNextPort(HashSet<int> usedPorts)
    {
        for (var port = StartAdbPort; port <= 65535; port += PortStep)
        {
            if (!usedPorts.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("没有可用的 ADB 端口");
    }

    private void RefreshInstances()
    {
        DiscoverInstancesFromWorkspace();
        RefreshConfiguredPorts();
        RefreshRunningStates();
        SaveState();
        NotifySummaryProperties();
    }

    private void DiscoverInstancesFromWorkspace()
    {
        if (!Directory.Exists(WorkspaceRoot))
        {
            return;
        }

        var knownDirectories = new HashSet<string>(Instances.Select(static instance => NormalizeDirectory(instance.DirectoryPath)), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(WorkspaceRoot))
        {
            if (!File.Exists(GetExecutablePath(directory)))
            {
                continue;
            }

            var normalized = NormalizeDirectory(directory);
            if (knownDirectories.Contains(normalized))
            {
                continue;
            }

            TryReadAdbPort(directory, out var port);
            Instances.Add(CreateInstance(Path.GetFileName(directory), directory, port, DefaultGroupName, "已发现"));
            knownDirectories.Add(normalized);
        }
    }

    private void RefreshConfiguredPorts()
    {
        foreach (var instance in Instances)
        {
            if (!Directory.Exists(instance.DirectoryPath))
            {
                instance.Status = "目录不存在";
                continue;
            }

            if (TryReadAdbPort(instance.DirectoryPath, out var port) && port > 0)
            {
                instance.AdbPort = port;
            }
        }
    }

    private void RefreshRunningStates()
    {
        foreach (var instance in Instances)
        {
            using var process = FindRunningProcess(instance);
            instance.IsRunning = process is not null;
            instance.ProcessId = process?.Id;
        }

        NotifySummaryProperties();
    }

    private IReadOnlyCollection<ManagedInstance> GetSelectedOrFocusedInstances()
    {
        var selected = Instances.Where(static instance => instance.IsSelected).ToList();
        if (selected.Count > 0)
        {
            return selected;
        }

        return SelectedInstance is null ? [] : [SelectedInstance];
    }

    private IReadOnlyCollection<ManagedInstance> GetSelectedOrAllInstances()
    {
        var selected = Instances.Where(static instance => instance.IsSelected).ToList();
        return selected.Count > 0 ? selected : Instances.ToList();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<InstanceManagerState>(File.ReadAllText(_stateFile));
            if (state is null)
            {
                return;
            }

            _releaseRepositoryUrl = string.IsNullOrWhiteSpace(state.ReleaseRepositoryUrl) ? _releaseRepositoryUrl : state.ReleaseRepositoryUrl;
            _releaseCacheDirectory = string.IsNullOrWhiteSpace(state.ReleaseCacheDirectory) ? _releaseCacheDirectory : state.ReleaseCacheDirectory;
            _selectedVersion = state.SelectedVersion ?? string.Empty;
            _releasePackagePath = state.ReleasePackagePath ?? string.Empty;
            _workspaceRoot = string.IsNullOrWhiteSpace(state.WorkspaceRoot) ? _workspaceRoot : state.WorkspaceRoot;
            _instanceNamePrefix = string.IsNullOrWhiteSpace(state.InstanceNamePrefix) ? _instanceNamePrefix : state.InstanceNamePrefix;
            _targetGroup = NormalizeGroupName(state.TargetGroup);
            _selectedGroupFilter = string.IsNullOrWhiteSpace(state.SelectedGroupFilter) ? AllGroupsFilter : state.SelectedGroupFilter;
            _newInstanceCount = Math.Clamp(state.NewInstanceCount, 1, 999);
            _cloneCount = Math.Clamp(state.CloneCount, 1, 999);
            _startAdbPort = Math.Clamp(state.StartAdbPort, 1, 65535);
            _portStep = Math.Clamp(state.PortStep, 1, 65535);

            Instances.Clear();
            foreach (var instance in state.Instances ?? [])
            {
                if (string.IsNullOrWhiteSpace(instance.DirectoryPath))
                {
                    continue;
                }

                Instances.Add(CreateInstance(
                    instance.Name ?? Path.GetFileName(instance.DirectoryPath),
                    instance.DirectoryPath,
                    instance.AdbPort,
                    NormalizeGroupName(instance.Group),
                    instance.Status ?? string.Empty));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "读取实例管理配置失败: " + ex.Message;
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var state = new InstanceManagerState
            {
                ReleaseRepositoryUrl = ReleaseRepositoryUrl,
                ReleaseCacheDirectory = ReleaseCacheDirectory,
                SelectedVersion = SelectedVersion,
                ReleasePackagePath = ReleasePackagePath,
                WorkspaceRoot = WorkspaceRoot,
                InstanceNamePrefix = InstanceNamePrefix,
                TargetGroup = TargetGroup,
                SelectedGroupFilter = SelectedGroupFilter,
                NewInstanceCount = NewInstanceCount,
                CloneCount = CloneCount,
                StartAdbPort = StartAdbPort,
                PortStep = PortStep,
                Instances = [.. Instances.Select(static instance => new ManagedInstanceState
                {
                    Name = instance.Name,
                    DirectoryPath = instance.DirectoryPath,
                    Group = instance.Group,
                    AdbPort = instance.AdbPort,
                    Status = instance.Status,
                })],
            };

            File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Saving state should not block instance operations.
        }
    }

    private void InstancesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ManagedInstance instance in e.NewItems)
            {
                instance.PropertyChanged += InstancePropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ManagedInstance instance in e.OldItems)
            {
                instance.PropertyChanged -= InstancePropertyChanged;
            }
        }

        RefreshGroupOptions();
        ApplyGroupFilter();
        NotifySummaryProperties();
    }

    private void InstancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ManagedInstance.IsSelected) or nameof(ManagedInstance.IsRunning))
        {
            NotifySummaryProperties();
        }

        if (e.PropertyName is nameof(ManagedInstance.Group))
        {
            RefreshGroupOptions();
            ApplyGroupFilter();
            SaveState();
        }
    }

    private void NotifySummaryProperties()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(RunningCount));
    }

    private static ManagedInstance CreateInstance(string name, string directoryPath, int adbPort, string group, string status)
    {
        return new ManagedInstance
        {
            Name = name,
            DirectoryPath = directoryPath,
            Group = group,
            AdbPort = adbPort,
            Status = status,
        };
    }

    private static void NormalizeExtractedReleaseRoot(string instanceDirectory)
    {
        if (File.Exists(GetExecutablePath(instanceDirectory)))
        {
            return;
        }

        var files = Directory.GetFiles(instanceDirectory);
        var directories = Directory.GetDirectories(instanceDirectory);
        if (files.Length != 0 || directories.Length != 1)
        {
            throw new FileNotFoundException("压缩包根目录下未找到 MAA.exe");
        }

        var nestedRoot = directories[0];
        if (!File.Exists(GetExecutablePath(nestedRoot)))
        {
            throw new FileNotFoundException("压缩包根目录下未找到 MAA.exe");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(nestedRoot).ToList())
        {
            var target = Path.Combine(instanceDirectory, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.Move(entry, target);
            }
            else
            {
                File.Move(entry, target);
            }
        }

        Directory.Delete(nestedRoot, recursive: true);
    }

    private static void EnsureExecutableExists(string instanceDirectory)
    {
        if (!File.Exists(GetExecutablePath(instanceDirectory)))
        {
            throw new FileNotFoundException("实例目录下未找到 MAA.exe", GetExecutablePath(instanceDirectory));
        }
    }

    private static void ApplyAdbPort(string instanceDirectory, int adbPort)
    {
        if (adbPort <= 0)
        {
            return;
        }

        var configDirectory = Path.Combine(instanceDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        var configFile = Path.Combine(configDirectory, "gui.json");
        var address = $"127.0.0.1:{adbPort}";

        JsonObject root;
        if (File.Exists(configFile))
        {
            root = JsonNode.Parse(File.ReadAllText(configFile)) as JsonObject ?? [];
        }
        else
        {
            root = [];
        }

        var current = root["Current"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(current))
        {
            current = DefaultConfigurationName;
        }

        if (root["Configurations"] is not JsonObject configurations)
        {
            configurations = [];
            var migratedConfig = new JsonObject();
            foreach (var property in root.Where(static property => property.Key is not ("Current" or "Global" or "Configurations")).ToList())
            {
                migratedConfig[property.Key] = property.Value?.DeepClone();
                root.Remove(property.Key);
            }

            configurations[current] = migratedConfig;
            root["Configurations"] = configurations;
        }

        if (configurations[current] is not JsonObject currentConfig)
        {
            currentConfig = [];
            configurations[current] = currentConfig;
        }

        currentConfig[ConnectAddressKey] = address;
        currentConfig[ConnectAddressHistoryKey] = JsonSerializer.Serialize(new[] { address });
        root["Current"] = current;
        if (root["Global"] is not JsonObject)
        {
            root["Global"] = new JsonObject();
        }

        File.WriteAllText(configFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryReadAdbPort(string instanceDirectory, out int port)
    {
        port = 0;
        var configFile = Path.Combine(instanceDirectory, "config", "gui.json");
        if (!File.Exists(configFile))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configFile)) as JsonObject;
            var current = root?["Current"]?.GetValue<string>() ?? DefaultConfigurationName;
            var address = root?["Configurations"]?[current]?[ConnectAddressKey]?.GetValue<string>()
                          ?? root?[ConnectAddressKey]?.GetValue<string>();
            return TryParsePort(address, out port);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParsePort(string? address, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var colonIndex = address.LastIndexOf(':');
        var portText = colonIndex >= 0 ? address[(colonIndex + 1)..] : address;
        return int.TryParse(portText, out port) && port > 0;
    }

    private static async Task<List<string>> FetchRepositoryVersionsAsync(string repositoryUrl)
    {
        var output = await RunGitAsync("ls-remote", "--tags", "--refs", repositoryUrl);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[1].StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                continue;
            }

            var tag = parts[1]["refs/tags/".Length..];
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag);
            }
        }

        var versions = tags.ToList();
        versions.Sort(CompareVersionTagsDescending);
        return versions;
    }

    private static async Task<string> DownloadReleasePackageAsync(string repositoryUrl, string tag, string cacheDirectory)
    {
        if (!TryParseGitHubRepository(repositoryUrl, out var owner, out var repository))
        {
            throw new NotSupportedException("自动下载当前仅支持 GitHub 仓库的 Release zip 资产");
        }

        var directPackage = await TryDownloadKnownReleaseAssetAsync(owner, repository, tag, cacheDirectory);
        if (!string.IsNullOrWhiteSpace(directPackage))
        {
            return directPackage;
        }

        var apiUrl = $"https://api.github.com/repos/{owner}/{repository}/releases/tags/{Uri.EscapeDataString(tag)}";
        using var metadataResponse = await ReleaseHttpClient.GetAsync(apiUrl);
        if (!metadataResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"无法读取 GitHub Release: {tag} ({(int)metadataResponse.StatusCode})");
        }

        var metadata = JsonNode.Parse(await metadataResponse.Content.ReadAsStringAsync()) as JsonObject
                       ?? throw new InvalidOperationException("GitHub Release 响应格式无效");
        var assets = metadata["assets"] as JsonArray
                     ?? throw new InvalidOperationException("GitHub Release 未返回资产列表");
        var asset = SelectReleaseAsset(assets)
                    ?? throw new InvalidOperationException("未找到可下载的 Windows x64 release zip");

        var targetDirectory = Path.Combine(cacheDirectory, SanitizePathSegment(tag));
        Directory.CreateDirectory(targetDirectory);
        var targetFile = Path.Combine(targetDirectory, SanitizePathSegment(asset.Name));
        if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
        {
            return targetFile;
        }

        var tempFile = targetFile + ".download";
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }

        using var downloadResponse = await ReleaseHttpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        downloadResponse.EnsureSuccessStatusCode();
        await using (var source = await downloadResponse.Content.ReadAsStreamAsync())
        await using (var target = File.Create(tempFile))
        {
            await source.CopyToAsync(target);
        }

        File.Move(tempFile, targetFile, overwrite: true);
        return targetFile;
    }

    private static async Task<string?> TryDownloadKnownReleaseAssetAsync(string owner, string repository, string tag, string cacheDirectory)
    {
        var candidates = new[]
        {
            $"MAA-{tag}-win-x64.zip",
            $"{repository}-{tag}-win-x64.zip",
            $"{tag}-win-x64.zip",
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var downloadUrl = $"https://github.com/{owner}/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(candidate)}";
            var target = await TryDownloadReleaseAssetAsync(downloadUrl, tag, candidate, cacheDirectory);
            if (!string.IsNullOrWhiteSpace(target))
            {
                return target;
            }
        }

        return null;
    }

    private static async Task<string?> TryDownloadReleaseAssetAsync(string downloadUrl, string tag, string assetName, string cacheDirectory)
    {
        var targetDirectory = Path.Combine(cacheDirectory, SanitizePathSegment(tag));
        Directory.CreateDirectory(targetDirectory);
        var targetFile = Path.Combine(targetDirectory, SanitizePathSegment(assetName));
        if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
        {
            return targetFile;
        }

        using var response = await ReleaseHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        if ((int)response.StatusCode == 404)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var tempFile = targetFile + ".download";
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }

        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(tempFile))
        {
            await source.CopyToAsync(target);
        }

        File.Move(tempFile, targetFile, overwrite: true);
        return targetFile;
    }

    private static (string Name, string DownloadUrl)? SelectReleaseAsset(JsonArray assets)
    {
        var candidates = new List<(string Name, string DownloadUrl, int Score)>();
        foreach (var asset in assets)
        {
            var name = asset?["name"]?.GetValue<string>();
            var downloadUrl = asset?["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(downloadUrl)
                || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 0;
            if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }

            if (name.Contains("MAA", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (name.Contains("OTA", StringComparison.OrdinalIgnoreCase)
                || name.Contains("symbols", StringComparison.OrdinalIgnoreCase)
                || name.Contains("debug", StringComparison.OrdinalIgnoreCase))
            {
                score -= 100;
            }

            candidates.Add((name, downloadUrl, score));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var selected = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        return (selected.Name, selected.DownloadUrl);
    }

    private static async Task<string> RunGitAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "git 命令执行失败" : stderr.Trim());
        }

        return stdout;
    }

    private static int CompareVersionTagsDescending(string left, string right)
    {
        var leftIsVersion = TryParseVersionTag(left, out var leftVersion);
        var rightIsVersion = TryParseVersionTag(right, out var rightVersion);
        if (leftIsVersion && rightIsVersion)
        {
            var versionCompare = rightVersion!.CompareTo(leftVersion);
            if (versionCompare != 0)
            {
                return versionCompare;
            }
        }
        else if (leftIsVersion != rightIsVersion)
        {
            return leftIsVersion ? -1 : 1;
        }

        return string.Compare(right, left, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersionTag(string tag, out Version? version)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var prereleaseIndex = value.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            value = value[..prereleaseIndex];
        }

        return Version.TryParse(value, out version);
    }

    private static bool TryParseGitHubRepository(string repositoryUrl, out string owner, out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;
        var value = repositoryUrl.Trim();
        var match = Regex.Match(
            value,
            @"(?:github\.com[:/])(?<owner>[^/\s:]+)/(?<repo>[^/\s]+?)(?:\.git)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        owner = match.Groups["owner"].Value;
        repository = match.Groups["repo"].Value;
        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repository);
    }

    private static HttpClient CreateReleaseHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MaaInstanceManager/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        }

        return client;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private void RefreshGroupOptions()
    {
        var selected = SelectedGroupFilter;
        var groups = Instances
            .Select(static instance => NormalizeGroupName(instance.Group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Groups.Clear();
        Groups.Add(AllGroupsFilter);
        foreach (var group in groups)
        {
            if (!string.Equals(group, AllGroupsFilter, StringComparison.OrdinalIgnoreCase))
            {
                Groups.Add(group);
            }
        }

        if (!Groups.Contains(selected))
        {
            _selectedGroupFilter = AllGroupsFilter;
            OnPropertyChanged(nameof(SelectedGroupFilter));
        }
    }

    private void ApplyGroupFilter()
    {
        var view = CollectionViewSource.GetDefaultView(Instances);
        view.Filter = IsInstanceVisible;
        view.Refresh();
        NotifySummaryProperties();
    }

    private bool IsInstanceVisible(object item)
    {
        if (item is not ManagedInstance instance)
        {
            return false;
        }

        return string.Equals(SelectedGroupFilter, AllGroupsFilter, StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeGroupName(instance.Group), SelectedGroupFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGroupName(string? group)
    {
        return string.IsNullOrWhiteSpace(group) ? DefaultGroupName : group.Trim();
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string rootName)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        var entryRoot = SanitizePathSegment(rootName);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{entryRoot}/{relativePath}", CompressionLevel.Optimal);
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = NormalizeDirectory(directory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, sourceSubdirectory)));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            var targetParent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            File.Copy(sourceFile, targetFile, overwrite: false);
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup after failed extraction/copy.
        }
    }

    private static Process? FindRunningProcess(ManagedInstance instance)
    {
        var executablePath = GetExecutablePath(instance.DirectoryPath);
        if (instance.ProcessId is int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (!process.HasExited && IsSamePath(GetMainModuleFileName(process), executablePath))
                {
                    return process;
                }

                process.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            try
            {
                if (!process.HasExited && IsSamePath(GetMainModuleFileName(process), executablePath))
                {
                    return process;
                }
            }
            catch
            {
                // ignored
            }

            process.Dispose();
        }

        return null;
    }

    private static string? GetMainModuleFileName(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string GetExecutablePath(string directoryPath)
    {
        return Path.Combine(directoryPath, ExecutableName);
    }

    private static bool IsSamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string directoryPath)
    {
        return Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string SanitizeFileNamePrefix(string prefix)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string((prefix ?? string.Empty).Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "MAA-" : sanitized;
    }

    private static void OpenFolder(string directoryPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = directoryPath,
            UseShellExecute = true,
        });
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record InstanceCreationPlan(string Name, string DirectoryPath, int AdbPort);
}

public sealed class ManagedInstance : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _name = string.Empty;
    private string _directoryPath = string.Empty;
    private string _group = "默认";
    private int _adbPort;
    private bool _isRunning;
    private int? _processId;
    private string _status = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string DirectoryPath
    {
        get => _directoryPath;
        set => SetField(ref _directoryPath, value);
    }

    public string Group
    {
        get => _group;
        set => SetField(ref _group, string.IsNullOrWhiteSpace(value) ? "默认" : value.Trim());
    }

    public int AdbPort
    {
        get => _adbPort;
        set {
            if (SetField(ref _adbPort, value))
            {
                OnPropertyChanged(nameof(ConnectAddress));
            }
        }
    }

    public string ConnectAddress => AdbPort > 0 ? $"127.0.0.1:{AdbPort}" : string.Empty;

    public bool IsRunning
    {
        get => _isRunning;
        set {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string StateText => IsRunning ? "运行中" : "已停止";

    public int? ProcessId
    {
        get => _processId;
        set => SetField(ref _processId, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class InstanceManagerState
{
    public string? ReleaseRepositoryUrl { get; set; }

    public string? ReleaseCacheDirectory { get; set; }

    public string? SelectedVersion { get; set; }

    public string? ReleasePackagePath { get; set; }

    public string? WorkspaceRoot { get; set; }

    public string? InstanceNamePrefix { get; set; }

    public string? TargetGroup { get; set; }

    public string? SelectedGroupFilter { get; set; }

    public int NewInstanceCount { get; set; } = 1;

    public int CloneCount { get; set; } = 1;

    public int StartAdbPort { get; set; } = 16384;

    public int PortStep { get; set; } = 32;

    public List<ManagedInstanceState>? Instances { get; set; }
}

public sealed class ManagedInstanceState
{
    public string? Name { get; set; }

    public string? DirectoryPath { get; set; }

    public string? Group { get; set; }

    public int AdbPort { get; set; }

    public string? Status { get; set; }
}
