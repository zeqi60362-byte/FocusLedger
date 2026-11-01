using FocusLedger.Core;
using Windows.Media.Control;

namespace FocusLedger.Collector.Windows;

public sealed record MediaSnapshot(
    MediaPlaybackState State,
    string? App,
    string? Title);

/// <summary>
/// 媒体播放状态。走 WinRT SMTC，覆盖浏览器、播放器、音乐客户端。
/// 这是区分"看网课"和"发呆"的关键信号，也是本机唯一能识别无键鼠学习的证据。
/// </summary>
public sealed class MediaWatcher
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public bool Available => _manager is not null;

    public async Task InitializeAsync()
    {
        if (_manager is not null)
        {
            return;
        }

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception)
        {
            _manager = null;
        }
    }

    public async Task<MediaSnapshot> PollAsync()
    {
        if (_manager is null)
        {
            return new MediaSnapshot(MediaPlaybackState.None, null, null);
        }

        try
        {
            var sessions = _manager.GetSessions();
            if (sessions.Count == 0)
            {
                return new MediaSnapshot(MediaPlaybackState.None, null, null);
            }

            GlobalSystemMediaTransportControlsSession? playing = null;
            GlobalSystemMediaTransportControlsSession? paused = null;

            foreach (var session in sessions)
            {
                var status = session.GetPlaybackInfo().PlaybackStatus;
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    playing = session;
                    break;
                }

                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    paused ??= session;
                }
            }

            var target = playing ?? paused;
            if (target is null)
            {
                return new MediaSnapshot(MediaPlaybackState.None, null, null);
            }

            string app = ShortApp(target.SourceAppUserModelId);
            string? title = null;

            try
            {
                var properties = await target.TryGetMediaPropertiesAsync();
                title = properties?.Title;
            }
            catch (Exception)
            {
                // 部分应用拒绝暴露媒体属性，不影响播放状态的判断
            }

            return new MediaSnapshot(
                playing is not null ? MediaPlaybackState.Playing : MediaPlaybackState.Paused,
                app,
                title);
        }
        catch (Exception)
        {
            return new MediaSnapshot(MediaPlaybackState.None, null, null);
        }
    }

    private static string ShortApp(string? appUserModelId)
    {
        if (string.IsNullOrEmpty(appUserModelId))
        {
            return "unknown";
        }

        int bang = appUserModelId.IndexOf('!');
        return bang > 0 ? appUserModelId[..bang] : appUserModelId;
    }
}
