using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FocusLedger.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryApplyDarkTitleBar();
    }

    /// <summary>让系统标题栏跟随暗色主题，否则会出现一条刺眼的白条。</summary>
    private void TryApplyDarkTitleBar()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int enabled = 1;
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
