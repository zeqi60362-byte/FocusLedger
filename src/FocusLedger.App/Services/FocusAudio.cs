using System.IO;
using System.Media;

namespace FocusLedger.App.Services;

/// <summary>
/// 专注背景音：完全由代码合成，不打包任何音频文件，因此没有版权问题。
/// 目前两种：白噪音（屏蔽环境杂音）、棕噪音（更接近雨声/风声）。
/// </summary>
public static class FocusAudio
{
    private static SoundPlayer? _player;
    private static MemoryStream? _stream;

    public static readonly (string Id, string Label)[] Options =
    {
        ("none", "无"),
        ("white", "白噪音"),
        ("rain", "雨声")
    };

    public static void Play(string mode)
    {
        Stop();

        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        byte[] wav = string.Equals(mode, "rain", StringComparison.OrdinalIgnoreCase)
            ? BuildWav(BrownNoise(seconds: 8))
            : BuildWav(WhiteNoise(seconds: 8));

        _stream = new MemoryStream(wav);
        _player = new SoundPlayer(_stream);
        try
        {
            _player.PlayLooping();
        }
        catch (Exception)
        {
            // 没有声卡或播放失败不影响专注本身
        }
    }

    public static void Stop()
    {
        try
        {
            _player?.Stop();
        }
        catch (Exception)
        {
        }

        _player?.Dispose();
        _player = null;
        _stream?.Dispose();
        _stream = null;
    }

    private static float[] WhiteNoise(int seconds, int rate = 22050)
    {
        int n = seconds * rate;
        var data = new float[n];
        var random = new Random(20261103);
        for (int i = 0; i < n; i++)
        {
            data[i] = (float)(random.NextDouble() * 2 - 1) * 0.18f;
        }

        FadeEnds(data, rate);
        return data;
    }

    /// <summary>棕噪音：白噪音做低频累积，听感接近雨声。</summary>
    private static float[] BrownNoise(int seconds, int rate = 22050)
    {
        var white = WhiteNoise(seconds, rate);
        var data = new float[white.Length];
        float last = 0;

        for (int i = 0; i < white.Length; i++)
        {
            last = (last + 0.02f * white[i] * 6f) * 0.998f;
            data[i] = Math.Clamp(last, -0.25f, 0.25f);
        }

        FadeEnds(data, rate);
        return data;
    }

    /// <summary>首尾各淡入淡出 0.4 秒，循环播放时不会出现爆音。</summary>
    private static void FadeEnds(float[] data, int rate)
    {
        int fade = Math.Min(data.Length / 4, rate * 2 / 5);
        for (int i = 0; i < fade; i++)
        {
            float gain = i / (float)fade;
            data[i] *= gain;
            data[data.Length - 1 - i] *= gain;
        }
    }

    private static byte[] BuildWav(float[] samples, int rate = 22050)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        int dataBytes = samples.Length * 2;

        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + dataBytes);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(dataBytes);

        foreach (float sample in samples)
        {
            writer.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        writer.Flush();
        return ms.ToArray();
    }
}
