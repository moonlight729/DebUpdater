using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Renci.SshNet.Common;
using DebUpdater.App.Models;

namespace DebUpdater.App.Services;

/// <summary>
/// 设备自动探测：不需要手工填 IP。
/// 顺序为 上次设备 → arp -a 邻居候选 → ping 刷新 ARP 缓存后重试 → 直连网段 22 端口扫描；
/// 每个候选先用 TCP 探端口，再用配置里的账号密码尝试 SSH 登录，登录成功才算找到设备。
/// </summary>
public sealed class DeviceScanner
{
    private static readonly Regex IpRegex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex MacRegex = new(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);

    /// <summary>全段扫描时最多扫几个网段，避免耗时失控。</summary>
    private const int MaxSweepSegments = 4;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        SshConnectionOptions template,
        string? preferredIp = null,
        Action<string>? log = null,
        Action<int, int>? progress = null,
        CancellationToken ct = default,
        bool deepSweep = true)
    {
        // 收集“端口开着但 SSH 没登录上”的地址：即便密码不对，也至少把 IP 交给用户，不至于全屏无结果。
        var openPorts = new ConcurrentBag<Candidate>();
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // 0) 上次连过的设备优先，命中就直接返回，最快。
        if (!string.IsNullOrWhiteSpace(preferredIp) && IPAddress.TryParse(preferredIp, out _))
        {
            log?.Invoke($"优先探测上次连接的设备 {preferredIp} …");
            var remembered = await ProbeSshAsync(
                [new Candidate(preferredIp, string.Empty, string.Empty)],
                template,
                "上次连接",
                log,
                progress,
                ct,
                openPorts: openPorts,
                failures: failures);

            if (remembered.Count > 0)
            {
                return remembered;
            }

            log?.Invoke($"{preferredIp} 未能通过 SSH 验证（{failures.GetValueOrDefault(preferredIp, $"{template.Port} 端口未开放")}），改为全网探测。");
        }

        var arp = await ReadArpTableAsync(ct);
        log?.Invoke($"arp -a：{arp.Entries.Count} 条邻居记录，{arp.Segments.Count} 个本机网段");

        // 1) arp 缓存里的邻居
        var found = await ProbeSshAsync(
            arp.Entries,
            template,
            "arp 缓存",
            log,
            progress,
            ct,
            openPorts: openPorts,
            failures: failures);

        if (found.Count > 0)
        {
            return found;
        }

        // 2) ARP 缓存可能还没学到刚接线的设备，先 ping 一遍网段刷新再试。
        foreach (var segment in arp.Segments)
        {
            log?.Invoke($"刷新 ARP 缓存：ping {segment} …");
            await PingSweepAsync(segment, ct);
        }

        var refreshed = await ReadArpTableAsync(ct);
        var known = arp.Entries.Select(e => e.Ip).ToHashSet(StringComparer.Ordinal);
        var fresh = refreshed.Entries.Where(e => !known.Contains(e.Ip)).ToList();
        log?.Invoke($"刷新后新增 {fresh.Count} 条邻居记录");

        found = await ProbeSshAsync(
            fresh,
            template,
            "arp 刷新",
            log,
            progress,
            ct,
            openPorts: openPorts,
            failures: failures);

        if (found.Count > 0)
        {
            return found;
        }

        // 3) 设备可能禁 ICMP，或刚上电还没进 ARP 缓存：直接扫直连网段的 22 端口。
        //    重试轮（deepSweep=false）跳过这一步，否则每轮都要等半分钟才出结果。
        foreach (var segment in deepSweep ? arp.Segments.Take(MaxSweepSegments) : [])
        {
            var hosts = EnumerateHosts(segment)
                .Select(ip => new Candidate(ip, string.Empty, string.Empty))
                .ToList();

            if (hosts.Count == 0)
            {
                continue;
            }

            log?.Invoke($"网段 {segment} 未命中，改为扫描 {hosts.Count} 个地址的 {template.Port} 端口 …");
            found = await ProbeSshAsync(
                hosts,
                template,
                "网段扫描",
                log,
                progress,
                ct,
                maxConcurrency: 64,
                portTimeoutMs: 1000,
                openPorts: openPorts,
                failures: failures);

            if (found.Count > 0)
            {
                return found;
            }
        }

        // SSH 都没登录上，但确实有机器开着 22 端口：把这些 IP 交出来，用户至少能看到并手动验证。
        var unverified = openPorts
            .GroupBy(c => c.Ip, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => c.Ip, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unverified.Count > 0)
        {
            log?.Invoke(
                $"{template.Port} 端口开放但 SSH 登录均未通过（{string.Join("、", unverified.Select(c => $"{c.Ip}:{failures.GetValueOrDefault(c.Ip, "原因未知")}"))}）。" +
                "已把 IP 填入设备框，请核对账号密码后点“测试连接”。");
            return unverified
                .Select(c => new DiscoveredDevice
                {
                    Ip = c.Ip,
                    HostName = "未验证（需测试连接）",
                    Architecture = string.Empty,
                    HasDpkg = false,
                    MacAddress = c.Mac,
                    InterfaceIp = c.InterfaceIp,
                    Source = "端口开放·未验证"
                })
                .ToList();
        }

        log?.Invoke($"未探测到设备：所有地址的 {template.Port} 端口都不通。请确认网线已连接、设备已开机，且 sshd 已启动。");
        return [];
    }

    // ---------- SSH 探测 ----------

    private static async Task<List<DiscoveredDevice>> ProbeSshAsync(
        IReadOnlyList<Candidate> candidates,
        SshConnectionOptions template,
        string source,
        Action<string>? log,
        Action<int, int>? progress,
        CancellationToken ct,
        int maxConcurrency = 16,
        int portTimeoutMs = 1200,
        ConcurrentBag<Candidate>? openPorts = null,
        ConcurrentDictionary<string, string>? failures = null)
    {
        var targets = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Ip))
            .GroupBy(c => c.Ip, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (targets.Count == 0)
        {
            return [];
        }

        log?.Invoke($"SSH 探测 {targets.Count} 个地址（{source}）…");

        var bag = new ConcurrentBag<DiscoveredDevice>();
        var done = 0;

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (candidate, token) =>
            {
                try
                {
                    if (!await IsPortOpenAsync(candidate.Ip, template.Port, portTimeoutMs, token))
                    {
                        return;
                    }

                    log?.Invoke($"{candidate.Ip} 的 {template.Port} 端口开放，尝试 SSH 登录 …");

                    using var runner = new SshCommandRunner(template with { Host = candidate.Ip });
                    await runner.ConnectAsync(token);

                    var probe = await runner.ShellAsync(
                        "hostname; uname -m; command -v dpkg >/dev/null 2>&1 && echo dpkg-ok || echo dpkg-missing",
                        TimeSpan.FromSeconds(12),
                        null,
                        true,
                        token);

                    var lines = probe.StdOut
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();

                    bag.Add(new DiscoveredDevice
                    {
                        Ip = candidate.Ip,
                        HostName = lines.ElementAtOrDefault(0) ?? "未知主机",
                        Architecture = lines.ElementAtOrDefault(1) ?? string.Empty,
                        HasDpkg = lines.ElementAtOrDefault(2) == "dpkg-ok",
                        MacAddress = candidate.Mac,
                        InterfaceIp = candidate.InterfaceIp,
                        Source = source
                    });

                    log?.Invoke($"发现设备 {candidate.Ip}（{lines.ElementAtOrDefault(0)}）");
                }
                catch (Exception ex)
                {
                    // 端口是开的，只是没登录上：记下原因，最后把这些 IP 一并交出来。
                    openPorts?.Add(candidate);
                    failures?[candidate.Ip] = ShortReason(ex);
                    log?.Invoke($"{candidate.Ip} 端口开放，但 SSH 登录失败：{ShortReason(ex)}");
                }
                finally
                {
                    var current = Interlocked.Increment(ref done);
                    progress?.Invoke(current, targets.Count);
                }
            });

        return bag
            .OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把 SSH 异常翻成一句人能看懂的原因。</summary>
    private static string ShortReason(Exception ex) => ex switch
    {
        SshAuthenticationException => "账号或密码不正确",
        SshOperationTimeoutException or TimeoutException or OperationCanceledException => "连接超时",
        SshConnectionException => "SSH 连接被拒绝或中断",
        System.Net.Sockets.SocketException => "网络不可达",
        _ => ex.Message
    };

    private static async Task<bool> IsPortOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(ip, port, linked.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    // ---------- arp 表 ----------

    private sealed record Candidate(string Ip, string Mac, string InterfaceIp);

    private sealed record ArpTable(IReadOnlyList<Candidate> Entries, IReadOnlyList<NetworkSegment> Segments);

    private sealed record NetworkSegment(string Network, int PrefixLength, bool IsPhysical = false)
    {
        public override string ToString() => $"{Network}/{PrefixLength}";
    }

    private static async Task<ArpTable> ReadArpTableAsync(CancellationToken ct)
    {
        var output = await RunProcessAsync("arp", "-a", ct);

        var entries = new List<Candidate>();
        var segments = new List<NetworkSegment>();
        var localAddresses = LocalAddresses();
        var currentInterface = string.Empty;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var ipMatch = IpRegex.Match(line);

            // 接口行形如 "接口: 192.168.137.1 --- 0x5"（中文表头在重定向时可能乱码，
            // 但 "---" 与 "0x" 是 ASCII，始终可识别）。
            var isInterfaceLine = (line.Contains("---") && line.Contains("0x"))
                || line.StartsWith("Interface", StringComparison.OrdinalIgnoreCase);

            if (isInterfaceLine)
            {
                if (ipMatch.Success)
                {
                    currentInterface = ipMatch.Value;
                    segments.Add(new NetworkSegment(GuessNetwork(currentInterface), 24));
                }

                continue;
            }

            if (!ipMatch.Success)
            {
                continue;
            }

            var ip = ipMatch.Value;
            var macMatch = MacRegex.Match(line);
            var mac = macMatch.Success ? macMatch.Value : string.Empty;

            if (!IsCandidate(ip, mac, localAddresses))
            {
                continue;
            }

            entries.Add(new Candidate(ip, mac, currentInterface));
        }

        segments.AddRange(LocalSegments());

        var uniqueSegments = segments
            .Where(s => s.PrefixLength is >= 16 and <= 30)
            .GroupBy(s => s.ToString(), StringComparer.Ordinal)
            .Select(g => new NetworkSegment(g.First().Network, g.First().PrefixLength, g.Any(s => s.IsPhysical)))
            .OrderByDescending(s => s.IsPhysical) // 有线网卡优先，VMware 等虚拟网卡排后面
            .ToList();

        var uniqueEntries = entries
            .GroupBy(e => e.Ip, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Ip, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ArpTable(uniqueEntries, uniqueSegments);
    }

    private static bool IsCandidate(string ip, string mac, IReadOnlySet<string> localAddresses)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();

        // 组播 / 广播 / 未指定地址都不是设备
        if (bytes[0] is >= 224 and <= 239 || ip.EndsWith(".255", StringComparison.Ordinal) || ip == "255.255.255.255" || ip == "0.0.0.0")
        {
            return false;
        }

        // 本机自己的地址
        if (localAddresses.Contains(ip))
        {
            return false;
        }

        // 广播 MAC / IPv4 组播映射 MAC / IPv6 组播映射 MAC / 全零
        if (mac.Length > 0)
        {
            var normalized = mac.Replace('-', ':').ToLowerInvariant();
            if (normalized is "ff:ff:ff:ff:ff:ff" or "00:00:00:00:00:00"
                || normalized.StartsWith("01:00:5e", StringComparison.Ordinal)
                || normalized.StartsWith("33:33", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // ---------- 网段 ----------

    private static async Task PingSweepAsync(NetworkSegment segment, CancellationToken ct)
    {
        var hosts = EnumerateHosts(segment);
        if (hosts.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = 96, CancellationToken = ct },
            async (ip, token) =>
            {
                try
                {
                    using var ping = new Ping();
                    // 目的只是让 ARP 缓存学到邻居，超时给很短即可。
                    await ping.SendPingAsync(ip, 120);
                }
                catch
                {
                    // 忽略不可达主机。
                }
            });
    }

    private static List<string> EnumerateHosts(NetworkSegment segment)
    {
        var hosts = new List<string>();
        if (!IPAddress.TryParse(segment.Network, out var network))
        {
            return hosts;
        }

        var totalHosts = 1L << (32 - segment.PrefixLength);
        if (totalHosts > 1026)
        {
            return hosts; // 网段过大（比 /22 还大）就不扫，避免耗时失控
        }

        var bytes = network.GetAddressBytes();
        var baseAddress = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        for (var offset = 1u; offset < totalHosts - 1; offset++)
        {
            var ip = baseAddress + offset;
            hosts.Add($"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}");
        }

        return hosts;
    }

    private static List<NetworkSegment> LocalSegments()
    {
        var segments = new List<NetworkSegment>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            // 不要求 OperationalStatus = Up：网线可能还没插、设备还没上电，
            // 这些接口的网段仍要纳入扫描，否则"先开软件再上电"就永远找不到设备。
            var isPhysical = nic.NetworkInterfaceType
                is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.Wireless80211;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var prefix = MaskToPrefix(unicast.IPv4Mask);
                segments.Add(new NetworkSegment(NetworkOf(unicast.Address, unicast.IPv4Mask), prefix, isPhysical));
            }
        }

        return segments;
    }

    private static IReadOnlySet<string> LocalAddresses()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    set.Add(unicast.Address.ToString());
                }
            }
        }

        return set;
    }

    private static string NetworkOf(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        return $"{a[0] & m[0]}.{a[1] & m[1]}.{a[2] & m[2]}.{a[3] & m[3]}";
    }

    private static int MaskToPrefix(IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
        var bits = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var prefix = 0;
        while (bits != 0)
        {
            prefix++;
            bits <<= 1;
        }

        return prefix;
    }

    private static string GuessNetwork(string interfaceIp) =>
        string.Join('.', interfaceIp.Split('.').Take(3)) + ".0";

    // ---------- 工具 ----------

    private static async Task<string> RunProcessAsync(string file, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return string.Empty;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
