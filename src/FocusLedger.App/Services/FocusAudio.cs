using System.IO;
using System.Media;

namespace FocusLedger.App.Services;

/// <summary>
/// 专注背景音：完全由代码合成，不打包任何音频文件，因此没有版权问题。
/// 目前两种：白噪音（屏蔽环境杂音）、棕噪音（更接近雨声/风声）。
/// </summary>
public sealed record MusicOption(string Id, string Label);

public static class FocusAudio
{
    private static SoundPlayer? _player;
    private static MemoryStream? _stream;

    public static readonly MusicOption[] Options =
    {
        new("none", "无"),
        new("folder", "本地音乐")
    };

    /// <summary>本地音乐文件夹，启动时从设置注入。</summary>
    public static string Folder = string.Empty;

    private static System.Windows.Media.MediaPlayer? _media;
    private static List<string> _playlist = new();
    private static int _index;

    /// <summary>当前曲目文件名（不含扩展名），没有播放时为空。</summary>
    public static string CurrentTrack { get; private set; } = string.Empty;

    public static event EventHandler? TrackChanged;

    /// <summary>播放指定文件夹里的音频，播完自动下一首，列表循环。</summary>
    public static void PlayFolder(string folder)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        _playlist = Directory
            .EnumerateFiles(folder)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_playlist.Count == 0)
        {
            return;
        }

        _index = 0;
        _media = new System.Windows.Media.MediaPlayer { Volume = 0.35 };
        _media.MediaEnded += (_, _) => NextTrack();
        _media.MediaFailed += (_, _) => { };
        PlayCurrent();
    }

    private static void NextTrack()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        _index = (_index + 1) % _playlist.Count;
        PlayCurrent();
    }

    private static void PlayCurrent()
    {
        if (_media is null || _index >= _playlist.Count)
        {
            return;
        }

        string path = _playlist[_index];
        CurrentTrack = System.IO.Path.GetFileNameWithoutExtension(path);
        TrackChanged?.Invoke(null, EventArgs.Empty);

        try
        {
            _media.Open(new Uri(path));
            _media.Play();
        }
        catch (Exception)
        {
        }
    }

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
            _media?.Stop();
            _media?.Close();
        }
        catch (Exception)
        {
        }

        _media = null;
        _playlist = new List<string>();
        if (CurrentTrack.Length > 0)
        {
            CurrentTrack = string.Empty;
            TrackChanged?.Invoke(null, EventArgs.Empty);
        }

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
            data[i] = (float)(random.NextDouble() * 2 - 1) * 0.12f;
        }

        FadeEnds(data, rate);
        return data;
    }

    /// <summary>
    /// 雨声：白噪音过一阶低通再叠一点高频颗粒，加缓慢的音量起伏，
    /// 比原始棕噪音柔和，不会有刺耳的嘶声。
    /// </summary>
    private static float[] BrownNoise(int seconds, int rate = 22050)
    {
        var white = WhiteNoise(seconds, rate);
        var data = new float[white.Length];
        var random = new Random(77);
        float low = 0;

        for (int i = 0; i < white.Length; i++)
        {
            // 一阶低通：系数越小越闷
            low += 0.06f * (white[i] - low);

            // 缓慢起伏（0.15Hz），模拟雨势强弱
            float swell = 0.75f + 0.25f * (float)Math.Sin(i / (double)rate * 0.15 * 2 * Math.PI);

            // 少量随机颗粒，模拟雨滴打在物体上的细碎声
            float drop = random.NextDouble() < 0.0006 ? (float)(random.NextDouble() * 0.5) : 0f;

            data[i] = Math.Clamp(low * 1.6f + drop, -0.22f, 0.22f) * swell;
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
