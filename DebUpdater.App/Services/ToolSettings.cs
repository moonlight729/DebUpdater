using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebUpdater.App.Services;

/// <summary>上位机保存的连接参数（密码明文落在 appsettings.json，仅适用于产线/调试环境）。</summary>
public sealed class ToolSettings
{
    public string Host { get; set; } = "192.168.137.245";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "originflow";
    public string Password { get; set; } = "yctc@2026";
    public string PackageDirectory { get; set; } = string.Empty;

    /// <summary>上次连上的设备 IP，启动时优先探测，命中就不用全扫。</summary>
    public string LastDeviceIp { get; set; } = string.Empty;
}

public static class ToolSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static ToolSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<ToolSettings>(text, Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置文件损坏时回退到默认值。
        }

        return CreateDefault();
    }

    public static void Save(string path, ToolSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 配置写失败不影响主流程。
        }
    }

    public static ToolSettings CreateDefault() => new();
}
