using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusLedger.App.Services;

/// <summary>应用设置，持久化到 %LOCALAPPDATA%\FocusLedger\settings.json。</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public bool AutoStart { get; set; }

    public bool StartPaused { get; set; }

    /// <summary>界面语言：zh / en。</summary>
    public string Language { get; set; } = "zh";

    public int RetentionDays { get; set; } = 14;

    public bool ApplyRetentionOnStartup { get; set; } = true;

    /// <summary>排除列表：命中进程名的样本不落库（FR-A8）。默认排除自身，否则会记录自己在记录。</summary>
    public List<string> ExcludedApps { get; set; } = new() { "FocusLedger.exe" };

    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusLedger");

    public static string SettingsPath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings = new();

        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    settings = loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置损坏时退回默认值，不阻塞启动
        }

        // 迁移：旧版本没有默认排除自身，会导致"记录自己在记录"
        if (!settings.ExcludedApps.Contains("FocusLedger.exe", StringComparer.OrdinalIgnoreCase))
        {
            settings.ExcludedApps.Add("FocusLedger.exe");
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置写不进去不应该打断使用
        }
    }

    public bool IsExcluded(string app) =>
        ExcludedApps.Exists(e => string.Equals(e, app, StringComparison.OrdinalIgnoreCase));
}
