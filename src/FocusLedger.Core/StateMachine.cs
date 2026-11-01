namespace FocusLedger.Core;

/// <summary>
/// 四状态判定。核心原则：**没有正面证据就算离席**。
/// 宁可少算，也不要把「人不在」算成「在思考」——那正是番茄钟高估的老路。
/// </summary>
public sealed class StateMachine
{
    public Classification Classify(WindowFeatures f, InferenceParams parameters)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(parameters);

        // R1 锁屏、睡眠 = 人不在
        if (f.LockedRatio >= 0.5)
        {
            return Away("R1:locked");
        }

        // R2 分心：娱乐/社交，且有活动（在点或在播）
        if (f.ContextValue <= 0.01 && (f.InputActivity > 0.05 || f.MediaPlaying))
        {
            return new Classification(ActivityState.OffTask, 0, 0, 0, "R2:off-task");
        }

        // R3 深度专注：在打字/操作，且是工作类应用
        if (f.InputActivity > parameters.InputActiveThreshold && f.ContextValue >= 0.99)
        {
            return new Classification(ActivityState.ActiveFocus, 1.00, 0.95, 1.00, "R3:active-task");
        }

        // R4 深度专注：在操作，但应用是中性（浏览器、系统界面）
        if (f.InputActivity > parameters.InputActiveThreshold && f.ContextValue >= 0.6)
        {
            return new Classification(ActivityState.ActiveFocus, 0.75, 0.60, 0.90, "R4:active-neutral");
        }

        // R5 静默专注：手没动，但在看/听学习内容（网课、教程、讲座）
        if (f.MediaPlaying
            && f.ContextValue >= 0.6
            && (f.AudioPeak > parameters.AudioThreshold || f.VisualActivity > parameters.VisualChangeThreshold))
        {
            return new Classification(ActivityState.SilentFocus, 0.85, 0.75, 0.95, "R5:media");
        }

        // R6 静默专注：画面在变但没输入（滚动长文、翻 PPT）
        if (f.VisualActivity > parameters.VisualChangeThreshold && f.ContextValue >= 0.6)
        {
            return new Classification(ActivityState.SilentFocus, 0.70, 0.55, 0.85, "R6:visual");
        }

        // R7 其余一律离席：没有输入、没有画面变化、没有媒体
        return Away("R7:away");
    }

    private static Classification Away(string rule) =>
        new(ActivityState.Away, 0, 0, 0, rule);
}
