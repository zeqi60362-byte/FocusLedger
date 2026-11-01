using System.Windows;
using FocusLedger.App.Localization;
using FocusLedger.App.Services;
using FocusLedger.App.ViewModels;
using FocusLedger.Storage;
using WinForms = System.Windows.Forms;

namespace FocusLedger.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _pauseItem;
    private FocusLedgerStore? _store;
    private RuleService? _rules;
    private RecordingService? _recording;
    private MainViewModel? _main;
    private MainWindow? _window;
    private AppSettings? _settings;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "FocusLedger.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("FocusLedger 已经在运行了。", "FocusLedger");
            Shutdown();
            return;
        }

        this.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _settings = AppSettings.Load();

        Localization.Localization.Instance.SetLanguage(
            string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.English
                : AppLanguage.Chinese);

        _store = new FocusLedgerStore(FocusLedgerStore.DefaultDatabasePath);
        _store.Initialize();

        _rules = new RuleService(_store);
        _recording = new RecordingService(_store, _settings, _rules);
        await _recording.InitializeAsync();

        var intent = new IntentViewModel(_store, _rules);
        var today = new TodayViewModel(_recording, _rules, intent);
        var report = new ReportViewModel(_store, _rules);
        var rulesView = new RulesViewModel(_rules, _recording);
        var calendar = new CalendarViewModel(_store);
        var settingsView = new SettingsViewModel(_settings, _store, _recording, _rules);
        _main = new MainViewModel(today, report, rulesView, calendar, settingsView, _recording);

        settingsView.LanguageChanged += (_, _) => _main?.RebuildForLanguage();

        // 规则一变，所有页面重算。用 BeginInvoke 避免在绑定更新中途重建列表。
        _rules.Changed += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => _main?.RefreshAll()));

        _window = new MainWindow { DataContext = _main };
        _window.Closing += OnWindowClosing;

        CreateTray();

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            _window.Show();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
    }

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = TrayIcon.Build(recording: _recording?.IsRecording ?? false),
            Visible = true,
            Text = "FocusLedger"
        };

        _tray.DoubleClick += (_, _) => ShowWindow();

        var open = new WinForms.ToolStripMenuItem("打开面板", null, (_, _) => ShowWindow());

        _pauseItem = new WinForms.ToolStripMenuItem(
            _recording?.IsRecording == true ? "暂停记录" : "继续记录",
            null,
            async (_, _) => await ToggleRecordingAsync());

        var exclude = new WinForms.ToolStripMenuItem("排除当前应用", null, (_, _) => ExcludeCurrentApp());
        exclude.Enabled = _recording?.Current is not null;

        var quit = new WinForms.ToolStripMenuItem("退出", null, (_, _) => ExitApplication());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(open);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(exclude);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(quit);

        _tray.ContextMenuStrip = menu;
        _tray.ContextMenuStrip.Opening += (_, _) => exclude.Enabled = _recording?.Current is not null;

        if (_recording is not null)
        {
            _recording.Changed += (_, _) => UpdateTrayState();
        }

        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateTrayState);
            return;
        }

        if (_tray is null || _recording is null)
        {
            return;
        }

        bool recording = _recording.IsRecording;
        _tray.Text = recording ? "FocusLedger · 记录中" : "FocusLedger · 已暂停";
        _tray.Icon = TrayIcon.Build(recording);

        if (_pauseItem is not null)
        {
            _pauseItem.Text = recording ? "暂停记录" : "继续记录";
        }

        _main?.UpdateStatus();
    }

    private async Task ToggleRecordingAsync()
    {
        if (_recording is null)
        {
            return;
        }

        await _recording.ToggleAsync();
        UpdateTrayState();
        _main?.Entries
            .Select(entry => entry.ViewModel)
            .OfType<TodayViewModel>()
            .FirstOrDefault()
            ?.Refresh();
    }

    private void ExcludeCurrentApp()
    {
        string? app = _recording?.Current?.App;
        if (_settings is null || string.IsNullOrEmpty(app))
        {
            return;
        }

        if (!_settings.ExcludedApps.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ExcludedApps.Add(app);
            _settings.Save();
        }

        _tray?.ShowBalloonTip(2000, "FocusLedger", $"已排除 {app}，之后的记录不再包含它。", WinForms.ToolTipIcon.Info);
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Show();
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void ExitApplication()
    {
        _exiting = true;

        _settings?.Save();

        if (_recording is not null)
        {
            _recording.PauseAsync().GetAwaiter().GetResult();
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _store?.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _store?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
