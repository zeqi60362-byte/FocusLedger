namespace FocusLedger.Core;

/// <summary>
/// 七状态判定。顺序即优先级，不可调整 —— 尤其是媒体必须先于空闲判断，
/// 否则 45 分钟的网课会被判成"离席"（见 docs/02-state-machine.md §6.1）。
/// </summary>
public sealed class StateMachine
{
    public Classification Classify(WindowFeatures f, InferenceParams parameters)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(parameters);

        // R1 锁屏/睡眠：不计入任何时长
        if (f.LockedRatio >= 0.5)
        {
            return new(ActivityState.Locked, 0, 0, 0, "high", "R1:locked");
        }

        // R2 分心：上下文判定为娱乐/社交，且确实在使用
        if (f.ContextValue <= 0.01 && (f.InputActivity > 0.05 || f.MediaPlaying))
        {
            return new(ActivityState.OffTask, 0, 0, 0, "high", "R2:off-task");
        }

        // R3 媒体消费：必须先于空闲判断
        if (f.MediaPlaying
            && f.ContextValue >= 0.6
            && (f.AudioPeak > parameters.AudioThreshold || f.VisualActivity > parameters.VisualChangeThreshold))
        {
            return new(ActivityState.PassiveFocus, 0.85, 0.75, 0.95, "high", "R3:media");
        }

        // R4 离席
        if (f.MaxIdleSeconds > parameters.IdleMaxFor(f.AppClass) && !f.MediaPlaying)
        {
            return new(ActivityState.IdleAway, 0, 0, 0, "high", "R4:idle");
        }

        // R5 深度专注（任务类）
        if (f.InputActivity > parameters.InputActiveThreshold && f.ContextValue >= 0.99)
        {
            return new(ActivityState.ActiveFocus, 1.00, 0.95, 1.00, "high", "R5:active-task");
        }

        // R6 活跃但在中性上下文（例如浏览器未命中任何标题规则）
        if (f.InputActivity > parameters.InputActiveThreshold && f.ContextValue >= 0.6)
        {
            return new(ActivityState.ActiveFocus, 0.60, 0.45, 0.75, "medium", "R6:active-neutral");
        }

        // R7 画面在变（滚动长文、幻灯片）
        if (f.VisualActivity > parameters.VisualChangeThreshold && f.ContextValue >= 0.6)
        {
            return new(ActivityState.PassiveFocus, 0.70, 0.55, 0.85, "medium", "R7:visual");
        }

        // R8 静默专注：无输入无变化，但在任务应用里
        if (f.ContextValue >= 0.6)
        {
            double w = SilentWeight(f.AppClass, f.MaxIdleSeconds);
            return new(
                ActivityState.SilentFocus,
                w,
                Math.Max(0, w - 0.20),
                Math.Min(1, w + 0.15),
                "low",
                $"R8:silent({f.AppClass})");
        }

        // R9 存疑
        return new(ActivityState.Ambiguous, 0.50, 0.30, 0.70, "low", "R9:ambiguous");
    }

    /// <summary>
    /// 静默专注权重带。阅读类长时间不滚动是正常行为，不应与"离开"同罚；
    /// 打字类则随时间递减。
    /// </summary>
    internal static double SilentWeight(AppClass appClass, int idleSeconds) => appClass switch
    {
        AppClass.Reading => 0.70,
        AppClass.Typing => idleSeconds <= 180 ? 0.70 : 0.50,
        _ => 0.50
    };
}
