namespace FocusLedger.Core;

/// <summary>
/// 判定参数。全部集中在 docs/02-state-machine.md，可被设置页覆盖。
/// </summary>
public sealed record InferenceParams
{
    public static readonly InferenceParams Default = new();

    public int TickSeconds { get; init; } = 5;

    public int WindowSeconds { get; init; } = 60;

    /// <summary>超过这个秒数没有任何输入，且画面与媒体都没动，就算离席。</summary>
    public int AwayIdleSeconds { get; init; } = 120;

    public double InputActiveThreshold { get; init; } = 0.10;

    public double VisualChangeThreshold { get; init; } = 0.25;

    public double AudioThreshold { get; init; } = 0.02;

    public int FocusBlockMinSeconds { get; init; } = 1500;

    public int MergeGapSeconds { get; init; } = 60;

    /// <summary>
    /// 画面变化的归一化分母。初始值 12 是估计值，需用真实使用数据标定。
    /// </summary>
    public double VisualNormalizationDenominator { get; init; } = 12.0;

    public double KeySaturation { get; init; } = 20.0;

    public double MouseSaturation { get; init; } = 10.0;
}
