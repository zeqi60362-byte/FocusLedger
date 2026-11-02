using FocusLedger.Collector.Windows;
using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App.Services;

/// <summary>
/// 采集调度：后台跑采集循环，按批写入 SQLite，并向界面广播"当前状态变了"。
/// 暂停时必须真正停止采集（不安装钩子），而不是继续采了丢掉。
/// </summary>
public sealed class RecordingService : IAsyncDisposable
{
    private const int FlushBatch = 12; // 12 × 5s = 60s

    private const string SelfAppName = "FocusLedger.exe";

    private readonly FocusLedgerStore _store;
    private readonly AppSettings _settings;
    private readonly RuleService _rules;
    private readonly List<Sample> _today = new();
    private readonly List<Sample> _pending = new();
    private readonly Queue<Sample> _recent = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private CollectorService? _collector;

    public RecordingService(FocusLedgerStore store, AppSettings settings, RuleService rules)
    {
        _store = store;
        _settings = settings;
        _rules = rules;
    }

    public bool IsRecording { get; private set; }

    public Sample? Current { get; private set; }

    /// <summary>最近一个窗口判定出的状态，用于托盘图标着色。</summary>
    public ActivityState? CurrentState { get; private set; }

    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        LoadToday();

        if (!_settings.StartPaused)
        {
            await StartAsync().ConfigureAwait(false);
        }
    }

    public void ReloadToday()
    {
        LoadToday();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAsync()
    {
        if (IsRecording)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _collector = new CollectorService(_rules.Current);
        await _collector.StartAsync().ConfigureAwait(false);

        IsRecording = true;
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task PauseAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        CurrentState = null;
        _cts?.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 采集线程未及时退出时不阻塞界面，钩子会在进程结束时释放
            }
        }

        Flush();
        _collector?.Dispose();
        _collector = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task ToggleAsync() => IsRecording ? PauseAsync() : StartAsync();

    public IReadOnlyList<Sample> SnapshotToday()
    {
        lock (_gate)
        {
            return _today.ToArray();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        if (_collector is null)
        {
            return;
        }

        try
        {
            await foreach (var sample in _collector.CollectAsync(null, token).ConfigureAwait(false))
            {
                // "当前窗口"指的是你打开面板之前用的那个窗口，所以记录所有非自身的样本
                if (!string.Equals(sample.App, SelfAppName, StringComparison.OrdinalIgnoreCase))
                {
                    Current = sample;
                }

                if (_settings.IsExcluded(sample.App))
                {
                    continue;
                }

                bool flush;
                lock (_gate)
                {
                    _today.Add(sample);
                    _pending.Add(sample);

                    _recent.Enqueue(sample);
                    while (_recent.Count > 15)
                    {
                        _recent.Dequeue();
                    }

                    flush = _pending.Count >= FlushBatch;
                }

                UpdateCurrentState();

                if (flush)
                {
                    Flush();
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常的暂停路径
        }
        catch (Exception)
        {
            // 采集异常不得让应用退出；界面会显示"已停止"
            IsRecording = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>用最近 60 秒的样本算一次判定，供托盘图标取色。</summary>
    private void UpdateCurrentState()
    {
        Sample[] window;
        lock (_gate)
        {
            window = _recent.ToArray();
        }

        if (window.Length == 0)
        {
            return;
        }

        // 样本不足 50 秒时先不判定，否则会拿 5 秒的碎片当结论，托盘一直显示"专注"。
        // 攒够一个 60 秒窗口再更新，代价是最多滞后一分钟，但结论和日报一致。
        long spanMs = window[^1].TsUtcMs - window[0].TsUtcMs;
        if (spanMs < 50_000)
        {
            return;
        }

        var segments = new SegmentBuilder(_rules.Current).Build(window, finalize: false);
        if (segments.Count > 0)
        {
            // 末尾那段是"还没走完"的残段（比如只有 10 秒），拿它当结论会永远显示专注。
            // 有完整窗口时取倒数第二段，也就是最近一个满 60 秒的判定。
            var chosen = segments.Count >= 2 ? segments[^2] : segments[^1];
            CurrentState = chosen.State;
#if DEBUG
            try
            {
                string log = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FocusLedger", "state-debug.log");
                System.IO.File.AppendAllText(log,
                    $"{DateTime.Now:HH:mm:ss} n={window.Length} span={spanMs / 1000}s segs={segments.Count} pick={(segments.Count >= 2 ? "lastFull" : "onlyPartial")} state={CurrentState}{Environment.NewLine}");
            }
            catch
            {
                // 诊断日志失败不影响主流程
            }
#endif
        }
    }

    private void Flush()
    {
        List<Sample> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = new List<Sample>(_pending);
            _pending.Clear();
        }

        try
        {
            _store.AppendSamples(batch);
        }
        catch (Exception)
        {
            // 写失败时把数据放回队列，下次再试
            lock (_gate)
            {
                _pending.InsertRange(0, batch);
            }
        }
    }

    private void LoadToday()
    {
        var (start, end) = DailyAggregator.DayRangeUtc(
            DateOnly.FromDateTime(DateTime.Now), TimeZoneInfo.Local);

        var samples = _store.LoadSamples(start, end);

        lock (_gate)
        {
            _today.Clear();
            _today.AddRange(samples);
        }

        // 启动时用最近一条非自身样本兜底，否则刚打开面板时"刚才在用的窗口"是空的
        if (samples.Count > 0)
        {
            var last = samples[^1];
            long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last.TsUtcMs;
            if (ageMs < 30 * 60 * 1000
                && !string.Equals(last.App, SelfAppName, StringComparison.OrdinalIgnoreCase))
            {
                Current = last;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await PauseAsync().ConfigureAwait(false);
    }
}
