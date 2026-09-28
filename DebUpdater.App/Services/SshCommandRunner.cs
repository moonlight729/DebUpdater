using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DebUpdater.App.Services;

/// <summary>
/// SSH 通道实现：密码登录 + sudo 提权 + SFTP 上传。
/// 命令统一包装成 sh -c '&lt;cmd&gt;'，需要提权时前置 sudo，退出码直接取远端 exit status。
/// </summary>
public sealed class SshCommandRunner : IRemoteRunner, IDisposable
{
    private readonly SshConnectionOptions _options;
    private SshClient? _client;

    public SshCommandRunner(SshConnectionOptions options) => _options = options;

    public string Target => _options.Target;

    public PrivilegeMode Mode { get; private set; } = PrivilegeMode.Unknown;

    public bool DryRun { get; set; }

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public Action<string>? CommandEcho { get; set; }

    public Action<string>? LineReceived { get; set; }

    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return;
        }

        var client = new SshClient(CreateConnectionInfo())
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        };

        await client.ConnectAsync(ct).ConfigureAwait(false);
        _client = client;
    }

    public async Task<PrivilegeMode> DetectPrivilegeAsync(CancellationToken ct = default)
    {
        // 1) 登录用户本身就是 root
        var direct = await ShellAsync("id -u", TimeSpan.FromSeconds(15), null, true, ct).ConfigureAwait(false);
        if (direct.Success && LastLine(direct.StdOut) == "0")
        {
            Mode = PrivilegeMode.Direct;
            return Mode;
        }

        // 2) sudo 免密
        var nopass = await ShellAsync("sudo -n -- sh -c 'id -u'", TimeSpan.FromSeconds(20), null, true, ct).ConfigureAwait(false);
        if (nopass.Success && LastLine(nopass.StdOut) == "0")
        {
            Mode = PrivilegeMode.SudoNoPassword;
            return Mode;
        }

        // 3) sudo 需要密码：用登录密码喂给 sudo -S
        Mode = PrivilegeMode.SudoWithPassword;
        var withPassword = await ShellAsync("id -u", TimeSpan.FromSeconds(30), null, true, ct).ConfigureAwait(false);
        if (withPassword.Success && LastLine(withPassword.StdOut) == "0")
        {
            return Mode;
        }

        Mode = PrivilegeMode.Unknown;
        return Mode;
    }

    public async Task<RemoteResult> ShellAsync(
        string remoteCommand,
        TimeSpan? timeout = null,
        Action<string>? lineSink = null,
        bool captureExitCode = true,
        CancellationToken ct = default)
    {
        var effective = timeout ?? DefaultTimeout;
        var payload = BuildCommand(remoteCommand);

        CommandEcho?.Invoke(MaskSecret(payload));

        if (DryRun)
        {
            LineReceived?.Invoke("(dry-run 模式，未真正执行)");
            return new RemoteResult(0, "(dry-run)", string.Empty);
        }

        var client = await RequireConnectedAsync(ct).ConfigureAwait(false);

        using var command = client.CreateCommand(payload);
        command.CommandTimeout = effective + TimeSpan.FromSeconds(5);

        var asyncResult = command.BeginExecute();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var pumpOut = PumpAsync(command.OutputStream, stdout, lineSink, ct);
        var pumpErr = PumpAsync(command.ExtendedOutputStream, stderr, lineSink, ct);

        try
        {
            await WaitCompletionAsync(asyncResult, command, effective, ct).ConfigureAwait(false);
            command.EndExecute(asyncResult);
        }
        catch (SshOperationTimeoutException)
        {
            throw new TimeoutException($"SSH 命令超时（{effective.TotalSeconds:0} 秒）：{MaskSecret(remoteCommand)}");
        }
        finally
        {
            // 命令结束后两个输出流会关闭，最多再等 3 秒把残留输出收干净。
            await Task.WhenAny(Task.WhenAll(pumpOut, pumpErr), Task.Delay(3000)).ConfigureAwait(false);
        }

        var exitCode = command.ExitStatus ?? (command.ExitSignal is null ? 0 : 128);
        return new RemoteResult(
            exitCode,
            stdout.ToString().TrimEnd('\r', '\n'),
            stderr.ToString().TrimEnd('\r', '\n'));
    }

    public async Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<int>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        CommandEcho?.Invoke($"sftp put {localPath} -> {remotePath}");

        if (DryRun)
        {
            LineReceived?.Invoke("(dry-run 模式，未真正上传)");
            return;
        }

        var total = (ulong)new FileInfo(localPath).Length;
        var effective = timeout ?? TimeSpan.FromMinutes(20);

        try
        {
            await UploadBySftpAsync(localPath, remotePath, total, effective, progress, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 部分设备 sshd 未开启 sftp 子系统，回退到 scp 传输。
            LineReceived?.Invoke($"SFTP 上传失败，改用 SCP：{ex.Message}");
        }

        await UploadByScpAsync(localPath, remotePath, effective, progress, ct).ConfigureAwait(false);
        progress?.Report(100);
    }

    private async Task UploadBySftpAsync(
        string localPath,
        string remotePath,
        ulong total,
        TimeSpan timeout,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var sftp = new SftpClient(CreateConnectionInfo())
            {
                OperationTimeout = timeout,
                BufferSize = 256 * 1024
            };

            sftp.Connect();
            try
            {
                using var stream = File.OpenRead(localPath);
                sftp.UploadFile(stream, remotePath, uploaded =>
                {
                    if (total == 0)
                    {
                        return;
                    }

                    progress?.Report((int)Math.Clamp(uploaded * 100UL / total, 0UL, 100UL));
                });
            }
            finally
            {
                sftp.Disconnect();
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task UploadByScpAsync(
        string localPath,
        string remotePath,
        TimeSpan timeout,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var scp = new ScpClient(CreateConnectionInfo())
            {
                OperationTimeout = timeout
            };

            if (progress is not null)
            {
                scp.Uploading += (_, e) =>
                {
                    if (e.Size <= 0)
                    {
                        return;
                    }

                    progress.Report((int)Math.Clamp(e.Uploaded * 100L / e.Size, 0, 100));
                };
            }

            scp.Connect();
            try
            {
                scp.Upload(new FileInfo(localPath), remotePath);
            }
            finally
            {
                scp.Disconnect();
            }
        }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _client?.Disconnect();
            _client?.Dispose();
        }
        catch
        {
            // 释放失败不影响主流程。
        }

        _client = null;
    }

    // ---------- 内部实现 ----------

    private PasswordConnectionInfo CreateConnectionInfo() => new(_options.Host, _options.Port, _options.User, _options.Password)
    {
        Timeout = TimeSpan.FromSeconds(15),
        RetryAttempts = 1,
        Encoding = Encoding.UTF8
    };

    private async Task<SshClient> RequireConnectedAsync(CancellationToken ct)
    {
        if (IsConnected && _client is not null)
        {
            return _client;
        }

        await ConnectAsync(ct).ConfigureAwait(false);
        return _client ?? throw new InvalidOperationException("SSH 未连接");
    }

    /// <summary>按提权模式包装命令；Unknown/Direct 直接执行，其余走 sudo。</summary>
    private string BuildCommand(string remoteCommand)
    {
        var inner = Quote(remoteCommand.Trim());
        return Mode switch
        {
            PrivilegeMode.SudoNoPassword => $"sudo -n -p '' -- sh -c {inner}",
            PrivilegeMode.SudoWithPassword =>
                $"printf '%s\\n' {Quote(_options.Password)} | sudo -S -p '' -- sh -c {inner}",
            _ => $"sh -c {inner}"
        };
    }

    private string MaskSecret(string text) =>
        string.IsNullOrWhiteSpace(_options.Password) ? text : text.Replace(_options.Password, "******");

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim('\r', ' ', '\t') ?? string.Empty;

    private async Task PumpAsync(Stream stream, StringBuilder buffer, Action<string>? sink, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                lock (buffer)
                {
                    buffer.AppendLine(line);
                }

                sink?.Invoke(line);
                LineReceived?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消或通道关闭，停止读取即可。
        }
        catch (IOException)
        {
            // 通道被关闭时读取会抛 IO 异常，忽略。
        }
    }

    private static async Task WaitCompletionAsync(IAsyncResult asyncResult, SshCommand command, TimeSpan timeout, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!asyncResult.IsCompleted)
        {
            if (ct.IsCancellationRequested)
            {
                TryCancel(command);
                ct.ThrowIfCancellationRequested();
            }

            if (sw.Elapsed >= timeout)
            {
                TryCancel(command);
                throw new TimeoutException($"SSH 命令超时（{timeout.TotalSeconds:0} 秒）");
            }

            await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void TryCancel(SshCommand command)
    {
        try
        {
            command.CancelAsync(forceKill: true, millisecondsTimeout: 2000);
        }
        catch
        {
            // 远端可能不支持信号，忽略。
        }
    }
}
