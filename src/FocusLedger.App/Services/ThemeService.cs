using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FocusLedger.App.Services;

public enum AppTheme
{
    Dark,
    Light,
    Midnight
}

/// <summary>
/// 皮肤切换：替换第一份合并字典（调色板），样式表里的引用是 DynamicResource，会自动跟随。
/// 自定义强调色以顶层资源覆盖的方式追加。
/// </summary>
public static class ThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) ? theme : AppTheme.Dark;

    public static void Apply(AppTheme theme, string? accentHex)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        CurrentTheme = theme;

        var uri = new Uri($"/FocusLedger;component/Themes/Palette.{theme}.xaml", UriKind.Relative);
        var palette = (ResourceDictionary)System.Windows.Application.LoadComponent(uri);

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(palette);
        }
        else
        {
            merged[0] = palette;
        }

        app.Resources.Remove("Accent");

        if (!string.IsNullOrWhiteSpace(accentHex)
            && ColorConverter.ConvertFromString(accentHex) is Color color)
        {
            app.Resources["Accent"] = new SolidColorBrush(color);
        }

        if (app.MainWindow is { } window)
        {
            ApplyTitleBar(window, theme != AppTheme.Light);
        }
    }

    /// <summary>让系统标题栏跟随皮肤，否则浅色皮肤上会顶一条黑边。</summary>
    public static void ApplyTitleBar(Window window, bool dark)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int enabled = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老系统没有这个属性，保持默认外观
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
