using System.Runtime.InteropServices;

namespace FocusLedger.Collector.Windows;

/// <summary>
/// 键鼠事件计数器。只做自增，绝不记录键码、坐标或内容（隐私红线）。
/// 回调内不允许任何 I/O、锁、LINQ —— 否则会拖慢全局输入。
/// </summary>
public sealed class InputCounter : IDisposable
{
    private long _keyCount;
    private long _mouseCount;

    private Thread? _thread;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Native.LowLevelProc? _keyboardProc;
    private Native.LowLevelProc? _mouseProc;
    private volatile bool _running;

    /// <summary>钩子是否安装成功。失败时应用应降级为"仅空闲"模式。</summary>
    public bool HooksInstalled { get; private set; }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _running = true;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "FocusLedger.InputCounter"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>取走自上次调用以来的计数（原子清零）。</summary>
    public (int Keys, int Mouse) Take()
    {
        int keys = (int)Interlocked.Exchange(ref _keyCount, 0);
        int mouse = (int)Interlocked.Exchange(ref _mouseCount, 0);
        return (keys, mouse);
    }

    private void RunMessageLoop()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;

        IntPtr module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.WhKeyboardLl, _keyboardProc, module, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.WhMouseLl, _mouseProc, module, 0);

        HooksInstalled = _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

        // 消息循环：低级钩子必须由安装线程泵消息，否则回调不会触发
        while (_running && Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = Native.TranslateMessage(ref msg);
            _ = Native.DispatchMessage(ref msg);
        }
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Increment(ref _keyCount);
        }

        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Increment(ref _mouseCount);
        }

        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;

        if (_keyboardHook != IntPtr.Zero)
        {
            _ = Native.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        GC.KeepAlive(_keyboardProc);
        GC.KeepAlive(_mouseProc);
    }
}
