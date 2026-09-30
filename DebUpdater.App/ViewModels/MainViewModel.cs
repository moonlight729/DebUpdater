using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DebUpdater.App.Models;
using DebUpdater.App.Services;
using WinForms = System.Windows.Forms;

namespace DebUpdater.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int MaxLogLines = 3000;

    // 固定业务参数：与 originflow_resume.sh 保持一致，界面不再暴露这些可选项。
    private const string ServiceName = "gen1-app.service";
    private const string BtSwitchFile = "/oem/config/wlan0_bt_switch";
    private const string RemoteRoot = "/data/local/tmp/originflow_resume";

    private readonly ToolSettings _settings;
    private readonly string _settingsPath;
    private readonly DeviceScanner _scanner = new();
    private readonly Dictionary<string, UpgradeStepItem> _stepIndex = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private DiscoveredDevice? _selectedDevice;

    private bool _isBusy;
    private bool _isConnected;
    private int _progressValue;
    private string _statusText = "准备就绪";
    private string _host = "192.168.137.245";
    private int _port = 22;
    private string _user = "originflow";
    private string _password = string.Empty;
    private string _packageDirectory = AppContext.BaseDirectory;
    private string _connectionStatus = "未连接";
    private string _elapsedText = "耗时 00:00:00";
    private bool _disableBtSwitch;

    public MainViewModel(ToolSettings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;

        Host = string.IsNullOrWhiteSpace(settings.Host) ? "192.168.137.245" : settings.Host;
        Port = settings.Port <= 0 ? 22 : settings.Port;
        User = string.IsNullOrWhiteSpace(settings.User) ? "originflow" : settings.User;
        Password = settings.Password ?? string.Empty;
        PackageDirectory = string.IsNullOrWhiteSpace(settings.PackageDirectory)
            ? ResolvePackageDirectory()
            : settings.PackageDirectory;
        DisableBtSwitch = settings.DisableBtSwitch;

        foreach (var name in UpgradeSteps.All)
        {
            var item = new UpgradeStepItem(name);
            Steps.Add(item);
            _stepIndex[name] = item;
        }

        TestConnectionCommand = new AsyncRelayCommand(() => TestConnectionAsync(), () => !IsBusy);
        ScanDevicesCommand = new AsyncRelayCommand(() => ScanDevicesAsync(autoRetry: false), () => !IsBusy);
        BrowseDirectoryCommand = new RelayCommand(BrowseDirectory, () => !IsBusy);
        ScanPackagesCommand = new AsyncRelayCommand(ScanPackagesAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && CanStartUpgrade);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ExportLogCommand = new RelayCommand(ExportLog);
        ClearLogCommand = new RelayCommand(() => Logs.Clear());

        LoadPackages(PackageDirectory);
    }

    public ObservableCollection<LocalDebPackage> Packages { get; } = [];
    public ObservableCollection<UpgradeLogLine> Logs { get; } = [];
    public ObservableCollection<UpgradeStepItem> Steps { get; } = [];
    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];

    /// <summary>探测到的设备；选中后自动填充 IP，界面不需要手填。</summary>
    public DiscoveredDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value))
            {
                return;
            }

            if (value is not null)
            {
                Host = value.Ip;
                AppendLog("INFO", $"已选择设备 {value.Description}");

                // 记住这次连的设备，下次启动优先探测，秒级命中。
                SaveSettings();
            }

            RaiseCommandStates();
            OnPropertyChanged(nameof(CanStartUpgrade));
        }
    }

    public ICommand TestConnectionCommand { get; }
    public ICommand ScanDevicesCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand ScanPackagesCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ExportLogCommand { get; }
    public ICommand ClearLogCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>必须先探测到设备并连上，才允许开始升级。</summary>
    public bool CanStartUpgrade => IsConnected && SelectedDevice is not null;

    /// <summary>底部状态栏显示的本次升级耗时。</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public int ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>程序版本号，取自 csproj 的 Version / InformationalVersion，如 1.0.0。</summary>
    public string AppVersion { get; } = ReadAppVersion();

    /// <summary>标题栏以 tag 形式展示的版本号，如 V1.0.0。</summary>
    public string AppVersionTag => $"V{AppVersion}";

    /// <summary>窗口标题：工具名 + 版本 tag。</summary>
    public string WindowTitle => $"离线 deb 升级工具 · SSH 网络版  {AppVersionTag}";

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
            {
                ResetConnectionState();
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
            {
                ResetConnectionState();
            }
        }
    }

    public string User
    {
        get => _user;
        set
        {
            if (SetProperty(ref _user, value))
            {
                ResetConnectionState();
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                ResetConnectionState();
            }
        }
    }

    public string PackageDirectory
    {
        get => _packageDirectory;
        set => SetProperty(ref _packageDirectory, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    /// <summary>
    /// 勾选后升级完成把 /oem/config/wlan0_bt_switch 写成 0（系统应用会关闭 wlan0 与蓝牙）；
    /// 不勾选时写 1（保持 wlan0 与蓝牙开启，出厂默认行为）。
    /// </summary>
    public bool DisableBtSwitch
    {
        get => _disableBtSwitch;
        set
        {
            if (!SetProperty(ref _disableBtSwitch, value))
            {
                return;
            }

            OnPropertyChanged(nameof(BtSwitchValue));
            OnPropertyChanged(nameof(BtSwitchSummary));
            AppendLog("INFO", BtSwitchSummary);
            SaveSettings();
        }
    }

    /// <summary>实际写入设备的标志位值：勾选 = 0，未勾选 = 1。</summary>
    public int BtSwitchValue => DisableBtSwitch ? 0 : 1;

    /// <summary>界面上展示的当前标志位策略。</summary>
    public string BtSwitchSummary =>
        $"{BtSwitchFile} = {BtSwitchValue}（{(DisableBtSwitch ? "关闭 wlan0 + 蓝牙" : "保持 wlan0 + 蓝牙开启")}）";

    public void LoadPackages(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        PackageDirectory = directory;

        var files = Directory.GetFiles(directory, "*.deb", SearchOption.TopDirectoryOnly)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RunOnUi(() =>
        {
            Packages.Clear();
            foreach (var file in files)
            {
                Packages.Add(new LocalDebPackage(file));
            }
        });

        AppendLog("INFO", $"扫描到 {files.Count} 个 deb：{directory}");
    }

    public void SaveSettings()
    {
        _settings.Host = Host;
        _settings.Port = Port;
        _settings.User = User;
        _settings.Password = Password;
        _settings.PackageDirectory = PackageDirectory;
        _settings.LastDeviceIp = SelectedDevice?.Ip ?? Host;
        _settings.DisableBtSwitch = DisableBtSwitch;

        ToolSettingsStore.Save(_settingsPath, _settings);
    }

    // ---------- 命令 ----------

    private async Task TestConnectionAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? owned = null;
        if (!ct.CanBeCanceled)
        {
            owned = new CancellationTokenSource();
            _cts = owned;
            ct = owned.Token;
        }

        IsBusy = true;
        ConnectionStatus = "连接中…";

        try
        {
            using var runner = CreateRunner();
            await runner.ConnectAsync(ct);

            AppendLog("OK", $"SSH 登录成功：{runner.Target}");

            var mode = await runner.DetectPrivilegeAsync(ct);
            var who = await runner.ShellAsync("id -u; hostname", TimeSpan.FromSeconds(15), null, true, ct);
            var info = who.StdOut.Replace('\n', ' ').Trim();

            IsConnected = mode != PrivilegeMode.Unknown;
            ConnectionStatus = mode switch
            {
                PrivilegeMode.Direct => $"已连接 {Host} · root 直登（{info}）",
                PrivilegeMode.SudoNoPassword => $"已连接 {Host} · sudo 免密（{info}）",
                PrivilegeMode.SudoWithPassword => $"已连接 {Host} · sudo 提权（{info}）",
                _ => $"已连接 {Host} · 无法提权"
            };

            AppendLog(mode == PrivilegeMode.Unknown ? "ERROR" : "OK", ConnectionStatus);
            if (mode == PrivilegeMode.Unknown)
            {
                AppendLog("WARN", "originflow 账号无法通过 sudo 拿到 root，请检查 sudoers 与密码。");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("WARN", "连接已被取消");
            ConnectionStatus = "已取消";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatus = "连接失败";
            AppendLog("ERROR", $"SSH 连接失败：{ex.Message}");
            AppendLog("WARN", "请确认网线已连、设备 IP 正确、sshd 已启动，且账号密码无误。");
        }
        finally
        {
            IsBusy = false;
            if (owned is not null)
            {
                _cts = null;
            }

            owned?.Dispose();
        }
    }

    /// <summary>自动探测设备：arp -a 过滤候选 → 22 端口 → SSH 登录验证。</summary>
    public async Task ScanDevicesAsync(bool autoRetry = false, CancellationToken ct = default)
    {
        CancellationTokenSource? owned = null;
        if (!ct.CanBeCanceled)
        {
            owned = new CancellationTokenSource();
            _cts = owned;
            ct = owned.Token;
        }

        // 重新探测就是一次全新的开始：清掉上一次的连接状态、耗时和候选列表，避免旧结果误导。
        ResetConnectionState();
        ElapsedText = "耗时 00:00:00";
        RunOnUi(() =>
        {
            Devices.Clear();
            SelectedDevice = null;
        });

        IsBusy = true;
        ProgressValue = 0;
        StatusText = "正在探测设备…";
        AppendLog("STEP", "=== 开始探测设备（arp -a 过滤 → 22 端口 → SSH 登录验证）===");

        try
        {
            var template = new SshConnectionOptions(Host, Port, User.Trim(), Password);
            var attempts = autoRetry ? 3 : 1;
            IReadOnlyList<DiscoveredDevice> devices = [];

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                devices = await _scanner.ScanAsync(
                    template,
                    _settings.LastDeviceIp,
                    log: message => AppendLog("INFO", message),
                    progress: (done, total) =>
                    {
                        if (total <= 0)
                        {
                            return;
                        }

                        ProgressValue = (int)Math.Clamp(done * 100L / total, 0, 100);
                        StatusText = $"正在探测设备… {done}/{total}";
                    },
                    ct,
                    deepSweep: attempt == 1);

                if (devices.Count > 0 || attempt == attempts)
                {
                    break;
                }

                // 常见场景：先开软件、后插网线/上电，稍等再试一次。
                AppendLog("WARN", $"第 {attempt} 轮未发现设备，6 秒后重试（可先插好网线、给设备上电）…");
                StatusText = "未发现设备，6 秒后重试…";
                ProgressValue = 0;
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
            }

            RunOnUi(() =>
            {
                Devices.Clear();
                foreach (var device in devices)
                {
                    Devices.Add(device);
                }
            });

            if (devices.Count == 1)
            {
                SelectedDevice = devices[0];

                // 只探到“端口开着但没登录上”的机器：IP 已经填好，等用户核对账号密码再连。
                if (devices[0].Source.StartsWith("端口开放", StringComparison.Ordinal))
                {
                    StatusText = "已填入候选 IP，请点“测试连接”确认";
                    AppendLog("WARN", $"只找到 {devices[0].Ip}（22 端口开放，SSH 未验证），已填入设备框：请核对账号密码后点“测试连接”。");
                    return;
                }

                AppendLog("OK", $"探测到 1 台设备，已自动选中：{devices[0].Display}");
                await TestConnectionAsync(ct);
                return;
            }

            if (devices.Count > 1)
            {
                SelectedDevice = devices[0];
                StatusText = $"发现 {devices.Count} 台设备，请选择";
                AppendLog("WARN", $"发现 {devices.Count} 台设备，请在下拉框选择：{string.Join("、", devices.Select(d => d.Ip))}");
                return;
            }

            StatusText = "未探测到设备";
            AppendLog("ERROR", "未探测到设备。请确认网线已连接、设备已开机且 sshd 已启动，再点“探测设备”重试。");

            // 保留上次设备的 IP：设备可能只是临时掉电/网线松了，下次启动仍要优先探测它。
        }
        catch (OperationCanceledException)
        {
            StatusText = "探测已取消";
            AppendLog("WARN", "设备探测已取消");
        }
        catch (Exception ex)
        {
            StatusText = "探测失败";
            AppendLog("ERROR", $"设备探测失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            if (owned is not null)
            {
                _cts = null;
            }

            owned?.Dispose();
        }
    }

    private async Task ScanPackagesAsync()
    {
        LoadPackages(PackageDirectory);
        await Task.CompletedTask;
    }

    private async Task StartAsync()
    {
        if (!ValidateReady(out var reason))
        {
            AppendLog("ERROR", reason);
            return;
        }

        IsBusy = true;
        SaveSettings();
        _cts = new CancellationTokenSource();
        ResetSteps();

        // 计时：底部状态栏实时显示本次升级耗时。
        var stopwatch = Stopwatch.StartNew();
        ElapsedText = "耗时 00:00:00";
        var timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Normal,
            (_, _) => ElapsedText = FormatElapsed(stopwatch.Elapsed),
            Application.Current.Dispatcher);
        timer.Start();

        ProgressValue = 0;
        StatusText = $"开始执行 · 目标 {Host}";
        AppendLog("STEP", "=== 开始升级流程（SSH 版，逻辑对齐 originflow_resume.sh）===");
        AppendLog("INFO", $"目标设备：{User}@{Host}:{Port}；服务：{ServiceName}；标志位：{BtSwitchSummary}");

        try
        {
            using var runner = CreateRunner();
            var service = new DebUpgradeService(runner)
            {
                Logger = (level, message) => AppendLog(level, message),
                StepReporter = UpdateStep
            };

            var selected = Packages.Where(p => p.Selected).ToList();
            var request = new UpgradeRequest(
                selected,
                ServiceName: ServiceName,
                BtSwitchFile: BtSwitchFile,
                BtSwitchValue: BtSwitchValue,
                RemoteRoot: RemoteRoot,
                StartServiceAfterInstall: true,
                RebootAfterInstall: false,
                KeepRemoteFiles: false,
                StrictConfigStep: true);

            var progress = new Progress<UpgradeProgress>(value =>
            {
                ProgressValue = value.Percent;
                StatusText = value.Message;
            });

            var result = await service.RunAsync(request, progress, _cts.Token);

            StatusText = result.Success ? "升级完成" : "升级失败";
            AppendLog(result.Success ? "OK" : "ERROR", $"{result.Message}（耗时 {result.Duration.TotalSeconds:0.#}s）");
            if (!string.IsNullOrWhiteSpace(result.Detail))
            {
                AppendLog("WARN", result.Detail);
            }

            foreach (var verification in result.Verifications)
            {
                AppendLog(
                    verification.Installed && verification.VersionMatched ? "OK" : "ERROR",
                    $"校验 {verification.FileName} → {verification.Package}：{verification.StatusText}");
            }
        }
        catch (Exception ex)
        {
            StatusText = "升级失败";
            AppendLog("ERROR", $"执行异常：{ex.Message}");
        }
        finally
        {
            timer.Stop();
            stopwatch.Stop();
            ElapsedText = $"耗时 {FormatElapsed(stopwatch.Elapsed)}";
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string ReadAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString()
            : informational;

        version = (version ?? "0.0.0").Trim().TrimStart('v', 'V');

        // InformationalVersion 可能带 +build 元数据，展示时去掉。
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    private static string FormatElapsed(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private void Cancel()
    {
        if (_cts is null)
        {
            return;
        }

        AppendLog("WARN", "已请求取消，等待当前命令退出…");
        StatusText = "正在取消…";
        _cts.Cancel();
    }

    private void BrowseDirectory()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择存放 deb 包的目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (Directory.Exists(PackageDirectory))
        {
            dialog.SelectedPath = PackageDirectory;
        }

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            LoadPackages(dialog.SelectedPath);
        }
    }

    private void ExportLog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出日志",
            Filter = "文本文件|*.log|所有文件|*.*",
            FileName = $"deb-upgrade-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var lines = Logs.Select(line => $"{line.TimeText} [{line.LevelText}] {line.Message}").ToList();
        File.WriteAllLines(dialog.FileName, lines);
        AppendLog("OK", $"日志已导出：{dialog.FileName}");
    }

    // ---------- 内部 ----------

    private SshCommandRunner CreateRunner()
    {
        var runner = new SshCommandRunner(new SshConnectionOptions(Host.Trim(), Port, User.Trim(), Password));
        runner.CommandEcho = text => AppendLog("CMD", text);
        runner.LineReceived = text =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            AppendLog("OUT", text.TrimEnd('\r'));
        };

        return runner;
    }

    private void UpdateStep(string name, StepStatus status, string? detail)
    {
        RunOnUi(() =>
        {
            if (!_stepIndex.TryGetValue(name, out var item))
            {
                return;
            }

            item.Status = status;
            item.Detail = detail ?? string.Empty;
        });
    }

    private void ResetSteps()
    {
        RunOnUi(() =>
        {
            foreach (var item in Steps)
            {
                item.Status = StepStatus.Pending;
                item.Detail = string.Empty;
            }
        });
    }

    private void ResetConnectionState()
    {
        IsConnected = false;
        if (!string.Equals(ConnectionStatus, "未连接", StringComparison.Ordinal))
        {
            ConnectionStatus = "未连接";
        }
    }

    private bool ValidateReady(out string reason)
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            reason = "请填写设备 IP";
            return false;
        }

        if (Port is <= 0 or > 65535)
        {
            reason = "SSH 端口不合法";
            return false;
        }

        if (string.IsNullOrWhiteSpace(User) || string.IsNullOrWhiteSpace(Password))
        {
            reason = "请填写 SSH 账号与密码";
            return false;
        }

        if (Packages.Count == 0)
        {
            reason = "包目录里没有找到 deb 文件，请先选择正确目录";
            return false;
        }

        if (Packages.All(p => !p.Selected))
        {
            reason = "请至少勾选一个需要安装的 deb 包";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string ResolvePackageDirectory()
    {
        // 从 exe 所在目录向上找，定位包含 *.deb 的目录（通常是仓库根目录/包存放目录）。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir.Exists && dir.GetFiles("*.deb", SearchOption.TopDirectoryOnly).Length > 0)
            {
                return dir.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }

    private void AppendLog(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            RunOnUi(() =>
            {
                Logs.Add(new UpgradeLogLine(DateTimeOffset.Now, MapLevel(level), trimmed));
                if (Logs.Count > MaxLogLines)
                {
                    Logs.RemoveAt(0);
                }
            });
        }
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action);
    }

    private void RaiseCommandStates()
    {
        ((AsyncRelayCommand)TestConnectionCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ScanDevicesCommand).NotifyCanExecuteChanged();
        ((RelayCommand)BrowseDirectoryCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ScanPackagesCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)StartCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
    }

    private static LogLevel MapLevel(string level) => level switch
    {
        "OK" => LogLevel.Ok,
        "WARN" => LogLevel.Warn,
        "ERROR" => LogLevel.Error,
        "CMD" => LogLevel.Command,
        "OUT" => LogLevel.Output,
        "STEP" => LogLevel.Step,
        _ => LogLevel.Info
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
