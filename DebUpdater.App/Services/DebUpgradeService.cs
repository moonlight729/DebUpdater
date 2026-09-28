using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DebUpdater.App.Models;

namespace DebUpdater.App.Services;

/// <summary>
/// 把 originflow_resume.sh 的执行逻辑搬到上位机编排层：
/// C# 负责流程控制，SSH 只下发最小粒度的 POSIX sh 命令（设备 shell 通常是 bash/sh）。
/// 顺序严格对齐脚本：root → 停服 → 写 /oem/config/wlan0_bt_switch=1 → 装 ota 组 → 装 gen1 组。
/// </summary>
public sealed class DebUpgradeService
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);

    private readonly IRemoteRunner _remote;
    private string? _currentStep;

    public DebUpgradeService(IRemoteRunner runner) => _remote = runner;

    /// <summary>(level, message) 回调，level 取 INFO/OK/WARN/ERROR/CMD/OUT/STEP。</summary>
    public Action<string, string>? Logger { get; set; }

    /// <summary>(stepName, status, detail) 回调，驱动界面上的「关键流程」面板。</summary>
    public Action<string, StepStatus, string?>? StepReporter { get; set; }

    public PrivilegeMode Privilege { get; private set; } = PrivilegeMode.Unknown;

    private void Log(string level, string message) => Logger?.Invoke(level, message);

    private void Step(string name, StepStatus status, string? detail = null)
    {
        _currentStep = status == StepStatus.Running ? name : null;
        StepReporter?.Invoke(name, status, detail);
    }

    public async Task<UpgradeResult> RunAsync(
        UpgradeRequest request,
        IProgress<UpgradeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var session = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var remoteDir = $"{request.RemoteRoot.TrimEnd('/')}/{session}";

        try
        {
            var selected = request.Packages.Where(p => p.Selected).ToList();
            if (selected.Count == 0)
            {
                throw new UpgradeException("没有选中任何 deb 包", "请在安装包列表中勾选本次要安装的 deb 文件。");
            }

            Report(progress, 2, "SSH 登录设备");
            Step(UpgradeSteps.Connect, StepStatus.Running);
            await WaitOnlineAsync(ct);
            Step(UpgradeSteps.Connect, StepStatus.Done, _remote.Target);

            Report(progress, 8, "确认 root 权限（脚本要求 uid=0）");
            Step(UpgradeSteps.Root, StepStatus.Running);
            await EnsureRootAsync(ct);
            Step(UpgradeSteps.Root, StepStatus.Done, DescribePrivilege());

            Report(progress, 14, "设备能力预检");
            Step(UpgradeSteps.Preflight, StepStatus.Running);
            await PreflightAsync(ct);
            Step(UpgradeSteps.Preflight, StepStatus.Done);

            Report(progress, 20, $"创建远端目录 {remoteDir}");
            await CreateRemoteDirAsync(remoteDir, ct);
            await CheckDiskSpaceAsync(remoteDir, selected, ct);

            Report(progress, 25, "上传 deb 包到设备");
            Step(UpgradeSteps.Upload, StepStatus.Running, $"{selected.Count} 个包");
            await PushPackagesAsync(selected, remoteDir, progress, 25, 55, ct);
            Step(UpgradeSteps.Upload, StepStatus.Done);

            Report(progress, 58, "读取 deb 元数据（同时验证 dpkg 能否识别 zstd 包）");
            Step(UpgradeSteps.Metadata, StepStatus.Running);
            await ReadRemoteMetadataAsync(selected, remoteDir, ct);
            Step(UpgradeSteps.Metadata, StepStatus.Done);

            Report(progress, 62, $"停止 {request.ServiceName}");
            Step(UpgradeSteps.StopService, StepStatus.Running, request.ServiceName);
            await StopServiceAsync(request.ServiceName, ct);
            Step(UpgradeSteps.StopService, StepStatus.Done);

            Report(progress, 68, $"写入 {request.BtSwitchFile}");
            Step(UpgradeSteps.BtSwitch, StepStatus.Running);
            await EnsureBluetoothSwitchAsync(request, ct);
            Step(UpgradeSteps.BtSwitch, StepStatus.Done, "值 = 1");

            var groups = DebPackageNaming.Ordered(selected.Select(p => p.Group)).ToList();
            var step = 72;
            var span = groups.Count == 0 ? 0 : 22 / groups.Count;
            foreach (var group in groups)
            {
                var stepName = StepNameOf(group);
                Report(progress, step, $"安装 {DebPackageNaming.Label(group)}");
                Step(stepName, StepStatus.Running);
                var installed = await InstallGroupAsync(group, selected, remoteDir, ct);
                Step(stepName, installed ? StepStatus.Done : StepStatus.Skipped, installed ? null : "无匹配 deb");
                step += span;
            }

            Report(progress, 95, "校验安装结果");
            Step(UpgradeSteps.Verify, StepStatus.Running);
            var verifications = await VerifyAsync(selected, ct);
            Step(UpgradeSteps.Verify, StepStatus.Done, $"{verifications.Count} 个包");

            Report(progress, 98, "收尾：daemon-reload / 启动服务 / 复查标志位");
            Step(UpgradeSteps.Finish, StepStatus.Running);
            await PostInstallAsync(request, ct);
            await FinalizeBluetoothSwitchAsync(request, ct);
            if (!request.KeepRemoteFiles)
            {
                await CleanupAsync(remoteDir, ct);
            }

            Step(UpgradeSteps.Finish, StepStatus.Done);
            Report(progress, 100, "升级完成");
            return new UpgradeResult(true, "升级完成", verifications, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            MarkCurrentStepFailed();
            Log("WARN", "任务已被取消");
            return new UpgradeResult(false, "任务已被取消", [], sw.Elapsed);
        }
        catch (UpgradeException ex)
        {
            MarkCurrentStepFailed();
            Log("ERROR", ex.Message);
            if (!string.IsNullOrWhiteSpace(ex.Hint))
            {
                Log("WARN", ex.Hint);
            }

            return new UpgradeResult(false, ex.Message, [], sw.Elapsed, ex.Hint);
        }
        catch (TimeoutException ex)
        {
            MarkCurrentStepFailed();
            Log("ERROR", ex.Message);
            return new UpgradeResult(false, ex.Message, [], sw.Elapsed,
                "请检查网线/SSH 是否断开；若 dpkg 本身较慢，可适当增大安装超时时间。");
        }
        catch (Exception ex)
        {
            MarkCurrentStepFailed();
            Log("ERROR", ex.Message);
            return new UpgradeResult(false, $"执行异常：{ex.Message}", [], sw.Elapsed);
        }
    }

    /// <summary>失败后恢复现场：修复 dpkg 未完成的事务并拉起业务服务。</summary>
    public async Task<string> RecoverAsync(string serviceName, CancellationToken ct = default)
    {
        await EnsureRootAsync(ct);

        Log("STEP", "修复 dpkg 未完成的事务");
        var configure = await TryShellAsync("dpkg --configure -a", true, TimeSpan.FromMinutes(10), ct);
        Log(configure.Success ? "OK" : "WARN", $"dpkg --configure -a 退出码 {configure.ExitCode}");
        if (!configure.Success)
        {
            Log("OUT", configure.Combined);
        }

        Log("STEP", "重载 systemd 并启动服务");
        var reload = await TryShellAsync("systemctl daemon-reload", true, TimeSpan.FromSeconds(30), ct);
        Log(reload.Success ? "OK" : "WARN", $"systemctl daemon-reload 退出码 {reload.ExitCode}");

        var start = await TryShellAsync($"systemctl start {serviceName}", true, TimeSpan.FromSeconds(60), ct);
        if (!start.Success)
        {
            var status = await TryShellAsync($"systemctl status {serviceName} --no-pager -n 20 | tail -25", false, TimeSpan.FromSeconds(20), ct);
            Log("OUT", status.Combined);
            throw new UpgradeException($"恢复失败：无法启动 {serviceName}", status.Combined);
        }

        var active = await WaitForServiceAsync(serviceName, true, TimeSpan.FromSeconds(30), ct);
        return active
            ? $"已恢复：{serviceName} 处于 active 状态"
            : $"已下发启动命令，但 {serviceName} 尚未进入 active，请手动确认";
    }

    // ---------- 各步骤 ----------

    private async Task WaitOnlineAsync(CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await _remote.ConnectAsync(ct);
                var probe = await _remote.ShellAsync("echo probe", TimeSpan.FromSeconds(15), null, true, ct);
                if (probe.ExitCode == 0)
                {
                    Log("OK", $"SSH 已登录：{_remote.Target}");
                    var host = (await TryShellAsync("hostname; uname -sr", false, TimeSpan.FromSeconds(15), ct)).StdOut.Trim();
                    if (!string.IsNullOrWhiteSpace(host))
                    {
                        Log("INFO", $"设备信息：{host.Replace('\n', ' ')}");
                    }

                    return;
                }

                last = new UpgradeException($"设备探测失败（退出码 {probe.ExitCode}）", probe.Combined);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
            }

            Log("WARN", $"SSH 连接失败，等待重试（{attempt}/2）…");
            await Task.Delay(1500, ct);
        }

        throw new UpgradeException(
            $"无法通过 SSH 连接 {_remote.Target}",
            last is null
                ? "请检查网线/网口、设备 IP 与 sshd 是否运行。"
                : $"底层错误：{last.Message}");
    }

    private async Task EnsureRootAsync(CancellationToken ct)
    {
        Privilege = await _remote.DetectPrivilegeAsync(ct);
        switch (Privilege)
        {
            case PrivilegeMode.Direct:
                Log("OK", "登录用户即为 root（uid=0）");
                return;
            case PrivilegeMode.SudoNoPassword:
                Log("OK", "sudo 免密提权成功（uid=0）");
                return;
            case PrivilegeMode.SudoWithPassword:
                Log("OK", "sudo 已用登录密码提权（uid=0）");
                return;
            default:
                throw new UpgradeException(
                    "无法获得 root 权限，脚本要求 uid=0",
                    "请确认 originflow 账号在 sudoers 中且密码正确；普通用户下 dpkg -i 会直接失败。");
        }
    }

    private string DescribePrivilege() => Privilege switch
    {
        PrivilegeMode.Direct => "root 直登",
        PrivilegeMode.SudoNoPassword => "sudo 免密",
        PrivilegeMode.SudoWithPassword => "sudo + 密码",
        _ => "未获得 root"
    };

    private async Task PreflightAsync(CancellationToken ct)
    {
        var arch = (await TryShellAsync("uname -m", false, TimeSpan.FromSeconds(15), ct)).StdOut.Trim();
        Log("INFO", $"设备架构：{arch}");
        if (!arch.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
            !arch.Contains("arm64", StringComparison.OrdinalIgnoreCase))
        {
            Log("WARN", "设备架构不是 arm64，请确认 deb 包是否匹配");
        }

        foreach (var tool in new[] { "dpkg", "dpkg-deb", "dpkg-query", "systemctl" })
        {
            var result = await TryShellAsync($"command -v {tool}", false, TimeSpan.FromSeconds(15), ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut.Trim()))
            {
                throw new UpgradeException(
                    $"设备上缺少必需工具：{tool}",
                    "该流程依赖 dpkg + systemd（Debian 系 RootFS），请确认目标固件类型。");
            }
        }

        Log("OK", "dpkg / dpkg-deb / dpkg-query / systemctl 均可用");

        var dpkgVersion = (await TryShellAsync("dpkg-query --version | head -1", false, TimeSpan.FromSeconds(15), ct)).StdOut.Trim();
        Log("INFO", $"dpkg 版本：{dpkgVersion}");
        if (!SupportsZstd(dpkgVersion))
        {
            Log("WARN", "dpkg 低于 1.21.18 时不支持 zstd 压缩（本目录 deb 为 data.tar.zst），安装阶段大概率失败");
        }

        var locks = await TryShellAsync("fuser /var/lib/dpkg/lock-frontend 2>/dev/null | tr -d ' '", false, TimeSpan.FromSeconds(10), ct);
        if (locks.Success && !string.IsNullOrWhiteSpace(locks.StdOut.Trim()))
        {
            Log("WARN", $"检测到 dpkg 锁被占用（pid: {locks.StdOut.Trim()}），请等待其他安装结束");
        }
    }

    private async Task CreateRemoteDirAsync(string remoteDir, CancellationToken ct)
    {
        // chmod 0777：SFTP 以 originflow 身份上传，dpkg 以 root 身份读取，目录必须对双方都可写。
        var result = await TryShellAsync(
            $"mkdir -p '{remoteDir}' && chmod 0777 '{remoteDir}' && cd '{remoteDir}' && pwd",
            true,
            TimeSpan.FromSeconds(20),
            ct);

        if (!result.Success)
        {
            throw new UpgradeException($"远端目录不可用：{remoteDir}", result.Combined);
        }

        Log("OK", $"远端工作目录：{result.StdOut.Trim().Split('\n').Last().Trim()}");
    }

    private async Task CheckDiskSpaceAsync(string remoteDir, IReadOnlyList<LocalDebPackage> packages, CancellationToken ct)
    {
        var raw = (await TryShellAsync($"df -Pk '{remoteDir}' | tail -1 | awk '{{print $4}}'", false, TimeSpan.FromSeconds(20), ct)).StdOut.Trim();
        if (!long.TryParse(raw.Split('\n').Last().Trim(), out var freeKb))
        {
            Log("WARN", $"无法解析可用空间：{raw}");
            return;
        }

        var needKb = packages.Sum(p => p.SizeBytes) / 1024;
        Log("INFO", $"可用空间 {DebPackageNaming.FormatSize(freeKb * 1024)}，本次需要 ≥ {DebPackageNaming.FormatSize(needKb * 1024)}");
        if (freeKb < needKb)
        {
            throw new UpgradeException("设备存储空间不足", "请先清理 /data/local/tmp 或 /oem 下无用文件。");
        }

        if (freeKb < needKb * 3L)
        {
            Log("WARN", "剩余空间偏小：dpkg 解包期间还需要额外空间");
        }
    }

    private async Task PushPackagesAsync(
        IReadOnlyList<LocalDebPackage> packages,
        string remoteDir,
        IProgress<UpgradeProgress>? progress,
        int fromPercent,
        int toPercent,
        CancellationToken ct)
    {
        for (var i = 0; i < packages.Count; i++)
        {
            var package = packages[i];
            var remote = package.RemotePath(remoteDir);
            var index = i;

            Report(progress, fromPercent, $"上传 {package.FileName}");
            var subProgress = progress is null
                ? null
                : new SubProgress(percent =>
                {
                    var ratio = (index + percent / 100.0) / packages.Count;
                    Report(progress, (int)(fromPercent + (toPercent - fromPercent) * ratio), $"上传 {package.FileName} {percent}%");
                });

            var localMd5 = await ComputeMd5Async(package.FilePath, ct);
            await _remote.UploadAsync(package.FilePath, remote, subProgress, TimeSpan.FromMinutes(20), ct);

            Log("OK", $"已上传 {package.FileName}（{package.SizeText}）");
            await VerifyUploadAsync(package, remote, localMd5, ct);
        }
    }

    private async Task VerifyUploadAsync(LocalDebPackage package, string remote, string localMd5, CancellationToken ct)
    {
        var result = await TryShellAsync($"md5sum '{remote}' | awk '{{print $1}}'", false, TimeSpan.FromSeconds(60), ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut.Trim()))
        {
            Log("WARN", "设备上没有 md5sum，跳过上传完整性校验");
            return;
        }

        var remoteMd5 = result.StdOut.Trim().Split('\n').Last().Trim();
        if (!string.Equals(remoteMd5, localMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpgradeException(
                $"上传后校验失败：{package.FileName}",
                $"本地 MD5 {localMd5} ≠ 设备 MD5 {remoteMd5}，传输可能损坏。");
        }

        Log("OK", $"{package.FileName} MD5 校验一致");
    }

    private async Task ReadRemoteMetadataAsync(IReadOnlyList<LocalDebPackage> packages, string remoteDir, CancellationToken ct)
    {
        foreach (var package in packages)
        {
            var remote = package.RemotePath(remoteDir);
            var result = await TryShellAsync(
                $"dpkg-deb -f '{remote}' Package Version Architecture",
                true,
                TimeSpan.FromSeconds(60),
                ct);

            if (!result.Success)
            {
                throw new UpgradeException(
                    $"读取 deb 元数据失败：{package.FileName}",
                    string.IsNullOrWhiteSpace(result.Combined)
                        ? "设备 dpkg 可能无法解析 zstd 压缩的 deb（需要 dpkg ≥ 1.21.18）。"
                        : result.Combined);
            }

            var fields = ParseDebianFields(result.StdOut, "Package", "Version", "Architecture");
            package.RemotePackage = fields.GetValueOrDefault("Package");
            package.RemoteVersion = fields.GetValueOrDefault("Version");

            var architecture = fields.GetValueOrDefault("Architecture");
            if (!string.IsNullOrWhiteSpace(architecture) && !architecture.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            {
                Log("WARN", $"{package.FileName} 架构为 {architecture}，与 arm64 设备可能不匹配");
            }

            Log("INFO", $"{package.FileName} → {package.RemotePackage ?? "?"} {package.RemoteVersion ?? "?"}（{architecture ?? "?"}）");
        }
    }

    /// <summary>对应脚本 12-23 行：停服 → 轮询 5s → 未死透则 SIGKILL + pkill -9。</summary>
    private async Task StopServiceAsync(string serviceName, CancellationToken ct)
    {
        Log("STEP", $"停止 {serviceName}");
        var stop = await TryShellAsync($"systemctl stop {serviceName}", true, TimeSpan.FromSeconds(60), ct);
        Log(stop.Success ? "OK" : "WARN", $"systemctl stop 退出码 {stop.ExitCode}");
        if (!stop.Success)
        {
            Log("OUT", stop.Combined);
        }

        for (var i = 1; i <= 5; i++)
        {
            var active = await IsServiceActiveAsync(serviceName, ct);
            if (!active)
            {
                Log("INFO", $"{serviceName} 已停止（轮询 {i} 次）");
                break;
            }

            if (i == 5)
            {
                Log("WARN", $"{serviceName} 5 秒内未停止，强制 KILL");
                var baseName = serviceName.EndsWith(".service", StringComparison.OrdinalIgnoreCase)
                    ? serviceName[..^".service".Length]
                    : serviceName;

                var kill = await TryShellAsync(
                    $"systemctl kill --signal=KILL {serviceName} 2>/dev/null; pkill -KILL -f {baseName} 2>/dev/null; sleep 1",
                    true,
                    TimeSpan.FromSeconds(30),
                    ct);
                Log("INFO", $"强制 KILL 完成，退出码 {kill.ExitCode}");
            }
            else
            {
                await Task.Delay(1000, ct);
            }
        }

        var finalActive = await IsServiceActiveAsync(serviceName, ct);
        Log(finalActive ? "WARN" : "OK", $"{serviceName} 最终状态：{(finalActive ? "active" : "inactive")}");
        if (finalActive)
        {
            Log("WARN", "服务仍在运行，dpkg 替换文件时可能报 Text file busy");
        }
    }

    /// <summary>对应脚本 26-34 行：写 /oem/config/wlan0_bt_switch = 1 并回读确认。</summary>
    private async Task EnsureBluetoothSwitchAsync(UpgradeRequest request, CancellationToken ct)
    {
        Log("STEP", $"写入 {request.BtSwitchFile} = 1");

        var ok = await TryWriteSwitchAsync(request.BtSwitchFile, ct);
        if (!ok)
        {
            Log("WARN", "首次写入失败，尝试重新挂载 /oem 为可写");
            var remount = await TryShellAsync("mount -o remount,rw /oem 2>&1", false, TimeSpan.FromSeconds(30), ct);
            Log("OUT", remount.Combined);
            ok = await TryWriteSwitchAsync(request.BtSwitchFile, ct);
        }

        if (ok)
        {
            Log("OK", $"{request.BtSwitchFile} 内容已确认为 1");
            return;
        }

        // 默认严格：标志位必须写成 1，否则升级结果不可用。
        if (request.StrictConfigStep)
        {
            throw new UpgradeException(
                $"写入失败：{request.BtSwitchFile}",
                "请确认 /oem 分区可写（mount | grep oem）以及 SELinux 策略。");
        }

        Log("WARN", $"写入失败：{request.BtSwitchFile}（已继续执行）");
    }

    /// <summary>安装收尾再复查一次，防止 deb 覆盖配置文件导致标志位被改回。</summary>
    private async Task FinalizeBluetoothSwitchAsync(UpgradeRequest request, CancellationToken ct)
    {
        var current = await TryShellAsync($"cat '{request.BtSwitchFile}' 2>/dev/null | tr -d ' \\t\\r\\n'", false, TimeSpan.FromSeconds(20), ct);
        var value = current.StdOut.Trim();
        Log("INFO", $"复查 {request.BtSwitchFile} 当前值：{(string.IsNullOrEmpty(value) ? "<空>" : value)}");

        if (value == "1")
        {
            Log("OK", $"标志位最终确认为 1");
            await TryShellAsync("sync", false, TimeSpan.FromSeconds(30), ct);
            return;
        }

        Log("WARN", "安装后标志位不为 1，重新写入");
        await EnsureBluetoothSwitchAsync(request, ct);
        await TryShellAsync("sync", false, TimeSpan.FromSeconds(30), ct);

        var again = await TryShellAsync($"cat '{request.BtSwitchFile}' 2>/dev/null | tr -d ' \\t\\r\\n'", false, TimeSpan.FromSeconds(20), ct);
        Log(again.StdOut.Trim() == "1" ? "OK" : "ERROR", $"标志位最终值：{again.StdOut.Trim()}");
    }

    private async Task<bool> TryWriteSwitchAsync(string filePath, CancellationToken ct)
    {
        var directory = ParentDir(filePath);
        var command = $"mkdir -p '{directory}'; printf '1\\n' > '{filePath}'; echo write_rc=$?; cat '{filePath}'";
        var result = await TryShellAsync(command, true, TimeSpan.FromSeconds(30), ct);
        if (!result.Success)
        {
            return false;
        }

        return result.StdOut
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Any(line => line == "1");
    }

    private static string ParentDir(string unixPath)
    {
        var index = unixPath.LastIndexOf('/');
        return index <= 0 ? "/" : unixPath[..index];
    }

    /// <summary>对应脚本 37-70 行：按 ota / gen1 两组分别 dpkg -i，组内一次性安装。</summary>
    private async Task<bool> InstallGroupAsync(
        DebGroup group,
        IReadOnlyList<LocalDebPackage> packages,
        string remoteDir,
        CancellationToken ct)
    {
        var files = packages.Where(p => p.Group == group).ToList();
        var label = DebPackageNaming.Label(group);

        if (files.Count == 0)
        {
            Log("INFO", $"未找到 {group}*.deb，跳过 {label} 安装");
            return false;
        }

        Log("INFO", $"找到 {files.Count} 个 {label}");
        foreach (var file in files)
        {
            Log("INFO", $"  - {file.FileName}（{file.SizeText}）");
        }

        var logFile = $"{remoteDir}/dpkg-{group.ToString().ToLowerInvariant()}.log";
        var quoted = string.Join(' ', files.Select(f => $"'{f.FileName}'"));
        var command = $"cd '{remoteDir}' && dpkg -i {quoted} > '{logFile}' 2>&1";

        var sw = Stopwatch.StartNew();
        var result = await TryShellAsync(command, true, InstallTimeout, ct);
        var detail = await ReadRemoteLogAsync(logFile, ct);

        if (result.Success)
        {
            Log("OK", $"{label} 安装成功（耗时 {sw.Elapsed.TotalSeconds:0.#}s）");
            return true;
        }

        throw new UpgradeException($"{label} 安装失败（dpkg 退出码 {result.ExitCode}）", GuessFailureHint(detail));
    }

    private async Task<string> ReadRemoteLogAsync(string remoteLog, CancellationToken ct)
    {
        // 回读 dpkg 日志时临时关掉实时回显，避免同一行被打印两次（一次流式、一次回读）。
        var echo = _remote.LineReceived;
        _remote.LineReceived = null;
        try
        {
            return await ReadRemoteLogCoreAsync(remoteLog, ct);
        }
        finally
        {
            _remote.LineReceived = echo;
        }
    }

    private async Task<string> ReadRemoteLogCoreAsync(string remoteLog, CancellationToken ct)
    {
        var result = await TryShellAsync($"cat '{remoteLog}' 2>/dev/null", false, TimeSpan.FromSeconds(60), ct);
        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            return string.Empty;
        }

        foreach (var line in result.StdOut.Split('\n'))
        {
            Log("OUT", line.TrimEnd('\r'));
        }

        return result.StdOut;
    }

    private async Task<IReadOnlyList<PackageVerification>> VerifyAsync(IReadOnlyList<LocalDebPackage> packages, CancellationToken ct)
    {
        var names = packages
            .Select(p => p.RemotePackage)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct()
            .ToList();

        if (names.Count == 0)
        {
            Log("WARN", "未读取到包名，跳过安装校验");
            return [];
        }

        var result = await TryShellAsync($"dpkg-query -W {string.Join(' ', names)}", true, TimeSpan.FromSeconds(30), ct);
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.StdOut.Split('\n'))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                installed[parts[0]] = parts[1].Trim();
            }
        }

        var verifications = new List<PackageVerification>();
        foreach (var package in packages)
        {
            var name = package.RemotePackage ?? string.Empty;
            installed.TryGetValue(name, out var version);
            package.InstalledVersion = version;

            var verification = new PackageVerification(package.FileName, name, package.RemoteVersion, version);
            verifications.Add(verification);

            var suffix = verification.Installed ? $"（当前 {version}）" : string.Empty;
            Log(verification.Installed && verification.VersionMatched ? "OK" : "ERROR",
                $"{name}：{verification.StatusText}{suffix}");
        }

        return verifications;
    }

    private async Task PostInstallAsync(UpgradeRequest request, CancellationToken ct)
    {
        var reload = await TryShellAsync("systemctl daemon-reload", true, TimeSpan.FromSeconds(30), ct);
        Log(reload.Success ? "OK" : "WARN", $"systemctl daemon-reload 退出码 {reload.ExitCode}");

        var ldconfig = await TryShellAsync("ldconfig 2>/dev/null || true", false, TimeSpan.FromSeconds(30), ct);
        Log("INFO", $"刷新动态库缓存 ldconfig（退出码 {ldconfig.ExitCode}）");

        if (request.StartServiceAfterInstall)
        {
            Log("STEP", $"启动 {request.ServiceName}");
            var start = await TryShellAsync($"systemctl start {request.ServiceName}", true, TimeSpan.FromSeconds(60), ct);
            if (!start.Success)
            {
                Log("WARN", start.Combined);
            }

            var active = await WaitForServiceAsync(request.ServiceName, true, TimeSpan.FromSeconds(45), ct);
            Log(active ? "OK" : "WARN", $"{request.ServiceName} 状态：{(active ? "active" : "未 active")}");
        }
        else
        {
            Log("INFO", "按脚本语义：安装后不主动拉起服务，由设备下次启动/业务流程触发");
        }

        if (request.RebootAfterInstall)
        {
            Log("STEP", "下发重启指令");
            try
            {
                await TryShellAsync("reboot", false, TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                Log("INFO", "设备已开始重启，SSH 连接断开属正常现象");
            }
        }
    }

    private async Task CleanupAsync(string remoteDir, CancellationToken ct)
    {
        var result = await TryShellAsync($"rm -rf '{remoteDir}'", true, TimeSpan.FromSeconds(60), ct);
        Log(result.Success ? "OK" : "WARN", $"清理远端目录 {remoteDir}（退出码 {result.ExitCode}）");
    }

    private async Task<bool> WaitForServiceAsync(string serviceName, bool activeExpected, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsServiceActiveAsync(serviceName, ct) == activeExpected)
            {
                return true;
            }

            await Task.Delay(1000, ct);
        }

        return false;
    }

    private async Task<bool> IsServiceActiveAsync(string serviceName, CancellationToken ct)
    {
        var result = await TryShellAsync($"systemctl is-active --quiet {serviceName}", true, TimeSpan.FromSeconds(15), ct);
        return result.ExitCode == 0;
    }

    /// <summary>允许失败：返回原始结果，不向上抛宿主侧异常。</summary>
    private async Task<RemoteResult> TryShellAsync(string command, bool captureExitCode, TimeSpan? timeout, CancellationToken ct)
    {
        try
        {
            return await _remote.ShellAsync(command, timeout, null, captureExitCode, ct);
        }
        catch (TimeoutException ex)
        {
            Log("ERROR", $"命令超时：{command}");
            return new RemoteResult(124, string.Empty, ex.Message);
        }
    }

    private void MarkCurrentStepFailed()
    {
        if (_currentStep is not null)
        {
            Step(_currentStep, StepStatus.Failed);
        }
    }

    private static string StepNameOf(DebGroup group) => group switch
    {
        DebGroup.Ota => UpgradeSteps.InstallOta,
        DebGroup.Gen1 => UpgradeSteps.InstallGen1,
        _ => "安装其他包"
    };

    private static Dictionary<string, string> ParseDebianFields(string text, params string[] expected)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
            else
            {
                positional.Add(line.Trim());
            }
        }

        for (var i = 0; i < positional.Count && i < expected.Length; i++)
        {
            fields[expected[i]] = positional[i];
        }

        return fields;
    }

    private static bool SupportsZstd(string dpkgVersionLine)
    {
        var match = Regex.Match(dpkgVersionLine ?? string.Empty, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
        {
            return false;
        }

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

        return major > 1 || (major == 1 && (minor > 21 || (minor == 21 && patch >= 18)));
    }

    private static string GuessFailureHint(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "设备上没有拿到 dpkg 日志，请检查远端目录是否可写。";
        }

        if (detail.Contains("dependency problems", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("依赖关系", StringComparison.OrdinalIgnoreCase))
        {
            return "依赖不满足，请先补齐依赖或使用 apt 本地安装。";
        }

        if (detail.Contains("No space left", StringComparison.OrdinalIgnoreCase))
        {
            return "设备存储空间不足，请清理 /data、/oem 后重试。";
        }

        if (detail.Contains("unsupported compression", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("zstd", StringComparison.OrdinalIgnoreCase))
        {
            return "设备 dpkg 不支持 zstd 压缩格式（需要 dpkg ≥ 1.21.18）。";
        }

        if (detail.Contains("Text file busy", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Device or resource busy", StringComparison.OrdinalIgnoreCase))
        {
            return "仍有进程占用目标文件，请确认 gen1-app 相关进程已完全停止。";
        }

        return "见上方 dpkg 日志输出。";
    }

    private Task<string> ComputeMd5Async(string filePath, CancellationToken ct) => Task.Run(() =>
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }, ct);

    private static void Report(IProgress<UpgradeProgress>? progress, int percent, string message) =>
        progress?.Report(new UpgradeProgress(Math.Clamp(percent, 0, 100), message));

    private sealed class SubProgress : IProgress<int>
    {
        private readonly Action<int> _report;

        public SubProgress(Action<int> report) => _report = report;

        public void Report(int value) => _report(value);
    }
}
