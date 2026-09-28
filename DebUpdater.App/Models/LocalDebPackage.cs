using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DebUpdater.App.Models;

public enum DebGroup
{
    Ota,
    Gen1,
    Other
}

public sealed class LocalDebPackage : INotifyPropertyChanged
{
    private bool _selected = true;
    private string? _remotePackage;
    private string? _remoteVersion;
    private string? _installedVersion;

    public LocalDebPackage(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        SizeBytes = new FileInfo(filePath).Length;
        Group = DebPackageNaming.Classify(FileName);
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public DebGroup Group { get; }

    public string GroupLabel => DebPackageNaming.Label(Group);
    public string SizeText => DebPackageNaming.FormatSize(SizeBytes);

    public bool Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public string? RemotePackage
    {
        get => _remotePackage;
        set => SetField(ref _remotePackage, value);
    }

    public string? RemoteVersion
    {
        get => _remoteVersion;
        set
        {
            if (SetField(ref _remoteVersion, value))
            {
                OnPropertyChanged(nameof(TargetVersion));
            }
        }
    }

    public string? InstalledVersion
    {
        get => _installedVersion;
        set => SetField(ref _installedVersion, value);
    }

    public string TargetVersion => string.IsNullOrWhiteSpace(RemoteVersion) ? "未读取" : RemoteVersion;

    public string RemotePath(string remoteDir) => $"{remoteDir.TrimEnd('/')}/{FileName}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
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

public static class DebPackageNaming
{
    // 脚本按 ota*.deb / gen1*.deb 两个 glob 安装，这里保持同样的分组规则。
    public static DebGroup Classify(string fileName)
    {
        if (fileName.StartsWith("ota", StringComparison.OrdinalIgnoreCase))
        {
            return DebGroup.Ota;
        }

        if (fileName.StartsWith("gen1", StringComparison.OrdinalIgnoreCase))
        {
            return DebGroup.Gen1;
        }

        return DebGroup.Other;
    }

    public static string Label(DebGroup group) => group switch
    {
        DebGroup.Ota => "OTA 包",
        DebGroup.Gen1 => "Gen1 包",
        _ => "其他包"
    };

    public static IReadOnlyList<DebGroup> Ordered(IEnumerable<DebGroup> present)
    {
        var set = present.ToHashSet();
        DebGroup[] order = [DebGroup.Ota, DebGroup.Gen1, DebGroup.Other];
        return order.Where(set.Contains).ToList();
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
