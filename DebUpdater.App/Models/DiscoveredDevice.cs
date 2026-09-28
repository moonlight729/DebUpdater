namespace DebUpdater.App.Models;

/// <summary>探测到的目标设备（来源：arp 缓存 / 网段扫描 / 上次连接）。</summary>
public sealed class DiscoveredDevice
{
    public string Ip { get; init; } = string.Empty;

    public string HostName { get; init; } = "未知主机";

    public string Architecture { get; init; } = string.Empty;

    public string MacAddress { get; init; } = string.Empty;

    /// <summary>设备出现在哪个本机接口的网段里。</summary>
    public string InterfaceIp { get; init; } = string.Empty;

    /// <summary>arp 缓存 / 网段扫描 / 上次连接。</summary>
    public string Source { get; init; } = string.Empty;

    public bool HasDpkg { get; init; }

    public string Display =>
        string.IsNullOrWhiteSpace(Architecture)
            ? $"{Ip} · {HostName}"
            : $"{Ip} · {HostName} · {Architecture}";

    public string Description =>
        $"{Ip}（{HostName}）{(HasDpkg ? " · dpkg 可用" : string.Empty)}" +
        $"{(string.IsNullOrWhiteSpace(MacAddress) ? string.Empty : $" · MAC {MacAddress}")}" +
        $"{(string.IsNullOrWhiteSpace(InterfaceIp) ? string.Empty : $" · 接口 {InterfaceIp}")}" +
        $" · 来源：{Source}";

    /// <summary>兜底：下拉框若没吃到 DisplayMemberPath，也能显示 IP 而不是类型名。</summary>
    public override string ToString() => Display;
}
