namespace FocusLedger.App.Services;

internal static class Fmt
{
    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weixin.exe"] = "微信",
        ["wechat.exe"] = "微信",
        ["msedge.exe"] = "Edge",
        ["chrome.exe"] = "Chrome",
        ["firefox.exe"] = "Firefox",
        ["explorer.exe"] = "文件资源管理器",
        ["winword.exe"] = "Word",
        ["excel.exe"] = "Excel",
        ["powerpnt.exe"] = "PowerPoint",
        ["wps.exe"] = "WPS",
        ["et.exe"] = "WPS 表格",
        ["code.exe"] = "VS Code",
        ["cursor.exe"] = "Cursor",
        ["devenv.exe"] = "Visual Studio",
        ["pycharm64.exe"] = "PyCharm",
        ["rstudio.exe"] = "RStudio",
        ["typora.exe"] = "Typora",
        ["obsidian.exe"] = "Obsidian",
        ["zotero.exe"] = "Zotero",
        ["acrobat.exe"] = "Acrobat",
        ["sumatrapdf.exe"] = "SumatraPDF",
        ["qq.exe"] = "QQ",
        ["telegram.exe"] = "Telegram",
        ["bilibili.exe"] = "哔哩哔哩",
        ["vlc.exe"] = "VLC",
        ["potplayermini64.exe"] = "PotPlayer",
        ["windowsterminal.exe"] = "终端",
        ["powershell.exe"] = "PowerShell",
        ["pwsh.exe"] = "PowerShell",
        ["cmd.exe"] = "命令提示符",
        ["searchhost.exe"] = "搜索",
        ["systemsettings.exe"] = "设置",
        ["taskmgr.exe"] = "任务管理器",
        ["deepseek harness.exe"] = "DeepSeek Harness",
        ["qgis-ltr-bin.exe"] = "QGIS",
        ["qgis.exe"] = "QGIS",
        ["stata-mp.exe"] = "Stata",
        ["stata.exe"] = "Stata",
        ["lockapp.exe"] = "锁屏"
    };

    /// <summary>把 weixin.exe 这类进程名翻成人话。</summary>
    public static string AppName(string app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            return "—";
        }

        if (AppNames.TryGetValue(app, out string? friendly))
        {
            return friendly;
        }

        return app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
    }

    /// <summary>列表里用的紧凑分钟数，例如 19min。</summary>
    public static string MinutesShort(double seconds)
    {
        int minutes = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
        return minutes <= 0 ? "<1min" : $"{minutes}min";
    }

    public static string Duration(double seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds:F0}s";
        }

        if (seconds < 3600)
        {
            return $"{seconds / 60:F0}m";
        }

        return $"{seconds / 3600:F1}h";
    }

    public static string Clock(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().ToString("HH:mm");

    public static string ClockWithSeconds(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().ToString("HH:mm:ss");
}
