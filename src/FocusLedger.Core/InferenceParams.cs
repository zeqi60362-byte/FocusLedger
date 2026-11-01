namespace FocusLedger.Core;

/// <summary>
/// 判定参数。全部集中在 docs/02-state-machine.md §9，可被设置页覆盖。
/// </summary>
public sealed record InferenceParams
{
    public static readonly InferenceParams Default = new();

    public int TickSeconds { get; init; } = 5;

    public int WindowSeconds { get; init; } = 60;

    public int IdleMaxTypingSeconds { get; init; } = 300;

    public int IdleMaxReadingSeconds { get; init; } = 600;

    public double InputActiveThreshold { get; init; } = 0.10;

    public double VisualChangeThreshold { get; init; } = 0.25;

    public double AudioThreshold { get; init; } = 0.02;

    public int ReturnMaxGapSeconds { get; init; } = 600;

    public int FocusBlockMinSeconds { get; init; } = 1500;

    public int MergeGapSeconds { get; init; } = 60;

    /// <summary>
    /// 画面变化的归一化分母。初始值 12 是估计值，需用真实使用数据标定（见 docs/m0-findings.md §3.2）。
    /// </summary>
    public double VisualNormalizationDenominator { get; init; } = 12.0;

    /// <summary>输入活跃度饱和点：达到该键鼠次数即视为满活跃。</summary>
    public double KeySaturation { get; init; } = 20.0;

    public double MouseSaturation { get; init; } = 10.0;

    public int IdleMaxFor(AppClass appClass) => appClass switch
    {
        AppClass.Reading => IdleMaxReadingSeconds,
        _ => IdleMaxTypingSeconds
    };
}
