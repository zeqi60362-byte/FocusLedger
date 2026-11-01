using FocusLedger.Core;

namespace FocusLedger.Core.Tests;

internal static class TestSamples
{
    public const long TickMs = 5_000;

    public static Sample Sample(
        long tsMs,
        string app,
        string title,
        ContextLevel context,
        AppClass appClass,
        int idleSeconds = 0,
        int keys = 0,
        int mouse = 0,
        int visualDelta = 0,
        double audioPeak = 0,
        MediaPlaybackState media = MediaPlaybackState.None,
        bool locked = false)
    {
        double contextValue = context switch
        {
            ContextLevel.Task => 1.0,
            ContextLevel.Neutral => 0.6,
            _ => 0.0
        };

        return new Sample(
            tsMs,
            0,
            app,
            title,
            context,
            contextValue,
            appClass,
            idleSeconds,
            keys,
            mouse,
            0,
            visualDelta,
            audioPeak,
            media,
            null,
            null,
            locked);
    }

    /// <summary>生成 count 个连续样本，间隔 5 秒。</summary>
    public static List<Sample> Run(
        long startMs,
        int count,
        string app,
        string title,
        ContextLevel context,
        AppClass appClass,
        int idleSeconds = 0,
        int keys = 0,
        int mouse = 0,
        int visualDelta = 0,
        double audioPeak = 0,
        MediaPlaybackState media = MediaPlaybackState.None,
        bool locked = false)
    {
        var list = new List<Sample>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(Sample(
                startMs + i * TickMs,
                app,
                title,
                context,
                appClass,
                idleSeconds,
                keys,
                mouse,
                visualDelta,
                audioPeak,
                media,
                locked));
        }

        return list;
    }
}
