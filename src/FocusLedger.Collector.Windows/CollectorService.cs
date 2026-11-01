using System.Diagnostics;
using System.Runtime.CompilerServices;
using FocusLedger.Core;

namespace FocusLedger.Collector.Windows;

/// <summary>
/// 采集器：按固定周期产出一个 <see cref="Sample"/>。
/// 只读系统状态，不落盘、不联网、不记录任何内容。
/// </summary>
public sealed class CollectorService : IDisposable
{
    private readonly IRuleEngine _rules;
    private readonly InferenceParams _parameters;
    private readonly InputCounter _input = new();
    private readonly VisualHasher _hasher = new();
    private readonly MediaWatcher _media = new();
    private bool _started;

    public CollectorService(IRuleEngine? rules = null, InferenceParams? parameters = null)
    {
        _rules = rules ?? RuleEngine.Default;
        _parameters = parameters ?? InferenceParams.Default;
    }

    /// <summary>低级钩子是否安装成功。false 时判定精度下降，应提示用户。</summary>
    public bool HooksInstalled => _input.HooksInstalled;

    /// <summary>媒体信号是否可用。</summary>
    public bool MediaAvailable => _media.Available;

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _input.Start();
        await _media.InitializeAsync().ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Sample> CollectAsync(
        TimeSpan? duration,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        ulong? previousHash = null;
        var tick = TimeSpan.FromSeconds(_parameters.TickSeconds);

        while ((duration is null || stopwatch.Elapsed < duration)
               && !cancellationToken.IsCancellationRequested)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var foreground = DesktopProbe.Capture();
            var media = await _media.PollAsync().ConfigureAwait(false);
            ulong? hash = _hasher.Capture(foreground.Hwnd);

            int delta = 0;
            if (hash.HasValue && previousHash.HasValue)
            {
                delta = VisualHasher.HammingDistance(hash.Value, previousHash.Value);
            }

            if (hash.HasValue)
            {
                previousHash = hash.Value;
            }

            var (keys, mouse) = _input.Take();
            var match = _rules.Match(foreground.App, foreground.Title);

            yield return new Sample(
                timestamp,
                foreground.Hwnd.ToInt64(),
                foreground.App,
                foreground.Title,
                match.Context,
                match.ContextValue,
                match.AppClass,
                Math.Max(0, foreground.IdleSeconds),
                keys,
                mouse,
                hash ?? 0UL,
                delta,
                // M1 暂未采集音频峰值，见 docs/m1-report.md「已知缺口」
                0.0,
                media.State,
                media.App,
                media.Title,
                foreground.Locked);

            await Task.Delay(tick, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _input.Dispose();
        _hasher.Dispose();
    }
}
