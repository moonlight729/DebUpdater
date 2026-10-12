using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DebUpdater.App.Models;

/// <summary>
/// 随 deb 一起推送的普通配置文件（当前只有 rclone.conf）。
/// 与 deb 包列表分开维护：勾选才推送，未勾选则整步跳过、不影响升级主流程。
/// </summary>
public sealed class ConfigFileItem : INotifyPropertyChanged
{
    private bool _selected = true;

    public ConfigFileItem(string filePath, string remotePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        RemotePath = remotePath;
        SizeBytes = new FileInfo(filePath).Length;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string RemotePath { get; }
    public long SizeBytes { get; }

    public string SizeText => DebPackageNaming.FormatSize(SizeBytes);

    /// <summary>默认勾选：选目录时自动带上，取消勾选则不推送。</summary>
    public bool Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

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
