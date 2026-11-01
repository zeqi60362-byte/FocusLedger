namespace FocusLedger.Core;

/// <summary>七状态 + 两个技术性状态（数据缺口）。见 docs/02-state-machine.md。</summary>
public enum ActivityState
{
    ActiveFocus,
    PassiveFocus,
    SilentFocus,
    OffTask,
    IdleAway,
    Locked,
    Ambiguous,
    UnknownGap
}

/// <summary>应用类型，决定空闲阈值与静默专注权重。</summary>
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
    string Confidence,
    string RuleId,
    string? CorrectedBy = null)
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
    string Confidence,
    string RuleId);
