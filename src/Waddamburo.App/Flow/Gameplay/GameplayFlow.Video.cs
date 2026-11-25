using Waddamburo.App.Presentation;
using Waddamburo.Game.Flow;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow.Gameplay;

internal sealed partial class GameplayFlow
{
    // The playing chart's background video (BackgroundVideo), opened when a song that has one is drawn.
    // ponytail: it stays open after the song until the next song's first frame replaces or closes it.
    private BackgroundVideo? _video;

    /// <summary>The background video's frame, for the scene's native fill.</summary>
    public RenderTextureId? ResolveVideo(LumenNativeSurfaceKey surface) => _video?.Resolve(surface);

    // Each frame: the video of the chart at that time, its frame, and where the scene draws it.
    private void showVideo(TimeSpan chartTime)
    {
        var mode = Shell.Arcade.BackgroundVideo;
        var wanted = mode == BackgroundVideoMode.Off || Demo ? null : _mode.Video(chartTime);
        if (wanted?.Video.Path != _video?.Path)
        {
            _video?.Dispose();
            _video = wanted is { } open ? BackgroundVideo.Open(Shell.Application, open.Video,
                open.Video.Area?.Height ?? (mode == BackgroundVideoMode.UnderLane && _play.Charts.Length == 1 ? 372 : 720),
                Shell.Arcade.BackgroundVideoFps) : null;
        }
        if (_video is not null && wanted is { } shown)
            _video.Show(shown.Time);
        // An opaque video (not added nor keyed) covers the backdrops from the song's start: before its first frame they
        // are black, not the skin's (a video starting later in the song).
        Shell.Gameplay.Video = _video is { } video && (video.Visible || !video.HidesDancers)
            ? new(video.Width / video.Height, mode == BackgroundVideoMode.UnderLane && _play.Charts.Length == 1,
                Shell.Arcade.BackgroundVideoDim / 100f, video.Additive, video.Area, video.HidesDancers, video.LumaCurve,
                video.Visible)
            : null;
    }
}
