using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DebUpdater.App.Models;

public enum LogLevel
{
    Info,
    Ok,
    Warn,
    Error,
    Command,
    Output,
    Step
}

public sealed record UpgradeLogLine(DateTimeOffset Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Ok => "OK",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Command => "CMD",
        LogLevel.Output => "OUT",
        _ => "STEP"
    };
}

public sealed class UpgradeException : Exception
{
    public UpgradeException(string message, string? hint = null)
        : base(message) => Hint = hint;

    public string? Hint { get; }
}

public sealed record UpgradeProgress(int Percent, string Message);

public sealed record PackageVerification(string FileName, string Package, string? ExpectedVersion, string? InstalledVersion)
{
    public bool Installed => !string.IsNullOrWhiteSpace(InstalledVersion);

    public bool VersionMatched => ExpectedVersion is null || VersionMatchedCore();

    public string StatusText => !Installed
        ? "未安装"
        : VersionMatched
            ? "版本一致"
            : $"版本不一致（期望 {ExpectedVersion}）";

    private bool VersionMatchedCore() => string.Equals(ExpectedVersion, InstalledVersion, StringComparison.Ordinal);
}

public sealed record UpgradeResult(
    bool Success,
    string Message,
    IReadOnlyList<PackageVerification> Verifications,
    TimeSpan Duration,
    string? Detail = null);

public sealed record UpgradeRequest(
    IReadOnlyList<LocalDebPackage> Packages,
    string ServiceName = "gen1-app.service",
    string BtSwitchFile = "/oem/config/wlan0_bt_switch",
    string RemoteRoot = "/data/local/tmp/originflow_resume",
    bool StartServiceAfterInstall = true,
    bool RebootAfterInstall = false,
    bool KeepRemoteFiles = false,
    bool StrictConfigStep = true);

public enum StepStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped
}

/// <summary>关键流程面板中的一步。</summary>
public sealed class UpgradeStepItem : INotifyPropertyChanged
{
    private StepStatus _status = StepStatus.Pending;
    private string _detail = string.Empty;

    public UpgradeStepItem(string name) => Name = name;

    public string Name { get; }

    public StepStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    public string StatusText => Status switch
    {
        StepStatus.Running => "进行中",
        StepStatus.Done => "已完成",
        StepStatus.Failed => "失败",
        StepStatus.Skipped => "跳过",
        _ => "待执行"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>关键流程步骤名，编排层与界面共用同一份定义。</summary>
public static class UpgradeSteps
{
    public const string Connect = "SSH 登录设备";
    public const string Root = "获取 root 权限";
    public const string Preflight = "设备能力预检";
    public const string Upload = "上传 deb 包";
    public const string Metadata = "读取 deb 元数据";
    public const string StopService = "停止业务服务";
    public const string BtSwitch = "写入 BT 开关标志位";
    public const string InstallOta = "安装 OTA 包";
    public const string InstallGen1 = "安装 Gen1 包";
    public const string Verify = "校验安装结果";
    public const string Finish = "收尾并复查标志位";

    public static IReadOnlyList<string> All =>
    [
        Connect, Root, Preflight, Upload, Metadata, StopService, BtSwitch, InstallOta, InstallGen1, Verify, Finish
    ];
}
