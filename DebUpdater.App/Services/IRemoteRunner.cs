namespace DebUpdater.App.Services;

/// <summary>远端命令执行结果（与 adb 版本语义一致）。</summary>
public sealed record RemoteResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string Combined => string.Join('\n', new[] { StdOut, StdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// 提权方式：登录用户本身是 root / sudo 免密 / sudo 需要密码（用登录密码喂给 sudo -S）。
/// </summary>
public enum PrivilegeMode
{
    Unknown,
    Direct,
    SudoNoPassword,
    SudoWithPassword
}

/// <summary>SSH 登录参数。</summary>
public sealed record SshConnectionOptions(string Host, int Port, string User, string Password)
{
    public string Target => $"{User}@{Host}:{Port}";
}

/// <summary>
/// 远端执行通道抽象（当前实现：SSH.NET）。上层编排只依赖本接口，
/// 以后换回 adb / 串口只需再实现一个 runner。
/// </summary>
public interface IRemoteRunner
{
    /// <summary>形如 originflow@192.168.137.245:22，用于日志与提示。</summary>
    string Target { get; }

    bool DryRun { get; set; }

    TimeSpan DefaultTimeout { get; set; }

    Action<string>? CommandEcho { get; set; }

    Action<string>? LineReceived { get; set; }

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>探测提权方式：root 直登 / sudo 免密 / sudo 需密码；Unknown 表示拿不到 root。</summary>
    Task<PrivilegeMode> DetectPrivilegeAsync(CancellationToken ct = default);

    Task<RemoteResult> ShellAsync(
        string remoteCommand,
        TimeSpan? timeout = null,
        Action<string>? lineSink = null,
        bool captureExitCode = true,
        CancellationToken ct = default);

    Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<int>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}
