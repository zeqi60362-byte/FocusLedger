namespace FocusLedger.Core;

/// <summary>
/// 四种状态。刻意保持精简：用户能一眼看懂，且每一种都能对应到明确的行为。
/// 锁屏并入离席，存疑与「被动专注」取消。
/// </summary>
public enum ActivityState
{
    /// <summary>深度专注：有键鼠输入，且前台是工作类应用。</summary>
    ActiveFocus,

    /// <summary>静默专注：手基本不动，但在看/听学习内容（网课、教程、长文）。</summary>
    SilentFocus,

    /// <summary>分心：娱乐、社交、购物类。</summary>
    OffTask,

    /// <summary>离席：人不在电脑前，或锁屏、睡眠。</summary>
    Away
}

public enum AppClass
{
    Reading,
    Typing,
    Media,
    Other
}

/// <summary>上下文类别，映射到上下文系数 C（1.0 / 0.6 / 0.0）。</summary>
public enum ContextLevel
{
    Task,
    Neutral,
    Distraction
}

public enum MediaPlaybackState
{
    None,
    Playing,
    Paused
}

/// <summary>tick 级原始样本（默认 5 秒一条）。</summary>
public sealed record Sample(
    long TsUtcMs,
    long Hwnd,
    string App,
    string Title,
    ContextLevel Context,
    double ContextValue,
    AppClass AppClass,
    int IdleSeconds,
    int KeyCount,
    int MouseCount,
    ulong VisualHash,
    int VisualDelta,
    double AudioPeak,
    MediaPlaybackState MediaState,
    string? MediaApp,
    string? MediaTitle,
    bool Locked);

/// <summary>60 秒窗口聚合后的特征向量。</summary>
public sealed record WindowFeatures(
    long StartUtcMs,
    long EndUtcMs,
    string App,
    string Title,
    AppClass AppClass,
    double InputActivity,
    double VisualActivity,
    double AudioPeak,
    bool MediaPlaying,
    double ContextValue,
    ContextLevel Context,
    int MaxIdleSeconds,
    double LockedRatio,
    int SampleCount)
{
    public double DurationSeconds => (EndUtcMs - StartUtcMs) / 1000.0;
}

/// <summary>一次判定结果。</summary>
public sealed record Segment(
    long StartUtcMs,
    long EndUtcMs,
    string App,
    string Title,
    AppClass AppClass,
    ActivityState State,
    double Weight,
    double WeightLow,
    double WeightHigh,
    string RuleId)
{
    public double DurationSeconds => (EndUtcMs - StartUtcMs) / 1000.0;

    public double EffectiveSeconds => DurationSeconds * Weight;
}

/// <summary>状态机的单次输出（尚未落成 Segment）。</summary>
public sealed record Classification(
    ActivityState State,
    double Weight,
    double WeightLow,
    double WeightHigh,
    string RuleId);

public static class StateNames
{
    public static string Chinese(ActivityState state) => state switch
    {
        ActivityState.ActiveFocus => "深度专注",
        ActivityState.SilentFocus => "静默专注",
        ActivityState.OffTask => "分心",
        _ => "离席"
    };
}
