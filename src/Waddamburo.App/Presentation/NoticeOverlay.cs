using System.Collections.Concurrent;
using System.Globalization;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Online;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The notices, after osu!'s notification overlay: new ones pop up as toasts at the top right (held
/// back while a lane is on screen, so a song is never covered), and F8 opens a sidebar listing them
/// with the background jobs on top. The <c>shown</c> callback hears of each notice once the player could
/// have read it (personal ones are then marked read on the server).
/// </summary>
internal sealed class NoticeOverlay : IDisposable
{
    private const float Stage = 1280, StageHeight = 720;
    private const float PanelWidth = 420, CardWidth = 388, Gap = 10, Margin = 16;
    private const float ToastTop = 12;
    private const int MaxToasts = 3, MaxLines = 6, FontSize = 20, LineHeight = 24;
    private static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(400);

    private readonly SdlApplication _application;
    private readonly string _fontPath;
    private readonly NoticeBoard _notices;
    private readonly JobBoard _jobs;
    private readonly Func<long, string?> _playerName;
    private readonly Action<IReadOnlyList<Notice>> _shown;
    private readonly ConcurrentQueue<Notice> _arrived = new();
    private readonly List<(Notice Notice, DateTimeOffset Since)> _toasts = [];
    private readonly HashSet<string> _reported = [];
    private readonly Dictionary<(string Key, uint Scale), (RenderTextureId Texture, float Height)> _textures = [];
    private readonly RenderTextureId _white;

    public NoticeOverlay(SdlApplication application, string fontPath, NoticeBoard notices, JobBoard jobs,
        Func<long, string?> playerName, Action<IReadOnlyList<Notice>> shown)
    {
        _application = application;
        _fontPath = fontPath;
        _notices = notices;
        _jobs = jobs;
        _playerName = playerName;
        _shown = shown;
        _white = application.UploadRgba8(1, 1, [255, 255, 255, 255]);
        notices.Added += _arrived.Enqueue;
        // Notices that arrived before the overlay existed (the backlog loads while the game starts).
        foreach (var notice in notices.Current.Reverse())
            _arrived.Enqueue(notice);
    }

    public bool Open { get; private set; }

    public void Toggle()
    {
        Open = !Open;
        if (Open)
            lock (_toasts)
                _toasts.Clear(); // the sidebar lists them
    }

    /// <summary><paramref name="onLane"/>: a song or the calibration is on screen; nothing is drawn and toasts wait.</summary>
    public IEnumerable<RenderQuad> Quads(bool onLane)
    {
        if (onLane)
            return [];
        var now = DateTimeOffset.UtcNow;
        var quads = new List<RenderQuad>();
        if (Open)
        {
            while (_arrived.TryDequeue(out _))
            {
            }
            sidebar(quads);
            return quads;
        }
        lock (_toasts)
        {
            _toasts.RemoveAll(toast => now - toast.Since > ToastTime);
            while (_toasts.Count < MaxToasts && _arrived.TryDequeue(out var notice))
                _toasts.Add((notice, now));
            var y = ToastTop;
            foreach (var (notice, since) in _toasts)
            {
                var (texture, height) = card(notice);
                var left = now - since;
                var alpha = (float)Math.Clamp(Math.Min(left / Fade, (ToastTime - left) / Fade), 0, 1);
                // Slides in from the right edge.
                var x = Stage - Margin - CardWidth + (1 - alpha) * 40;
                quads.Add(rect(texture, x, y, CardWidth, height, new RenderColor(1, 1, 1, alpha)));
                y += height + Gap;
            }
            report(_toasts.Select(static toast => toast.Notice));
        }
        return quads;
    }

    private void sidebar(List<RenderQuad> quads)
    {
        var x = Stage - PanelWidth;
        quads.Add(rect(_white, 0, 0, Stage, StageHeight, new RenderColor(0, 0, 0, 0.35f)));
        quads.Add(rect(_white, x, 0, PanelWidth, StageHeight, new RenderColor(0.07f, 0.07f, 0.09f, 0.94f)));
        var (header, headerHeight) = text("header", T("notices.title"), PanelWidth - 2 * Margin, 30, anchor: 0);
        quads.Add(rect(header, x + Margin, 20, PanelWidth - 2 * Margin, headerHeight));
        var (hint, hintHeight) = text("hint", T("notices.close_hint"), PanelWidth - 2 * Margin, 18, anchor: 1);
        quads.Add(rect(hint, x + Margin, 28, PanelWidth - 2 * Margin, hintHeight));
        var y = 70f;

        foreach (var job in _jobs.Visible)
        {
            var title = job.Failure is { } failure ? T("job.failed", job.Title, failure) : job.Title;
            var (label, labelHeight) = text($"job:{title}", title, CardWidth, 18, anchor: 0);
            quads.Add(rect(label, x + Margin, y, CardWidth, labelHeight));
            y += labelHeight + 4;
            quads.Add(rect(_white, x + Margin, y, CardWidth, 6, new RenderColor(1, 1, 1, 0.15f)));
            var colour = job.Failure is not null ? new RenderColor(0.9f, 0.3f, 0.25f, 1) : new RenderColor(0.35f, 0.7f, 1, 1);
            // No count: a block sweeping across the bar.
            var (from, width) = job.Fraction is { } fraction
                ? (0f, (float)fraction * CardWidth)
                : ((float)(DateTimeOffset.UtcNow - job.StartedAt).TotalSeconds % 1.5f / 1.5f * (CardWidth - 80), 80f);
            quads.Add(rect(_white, x + Margin + from, y, job.EndedAt is null || job.Failure is not null ? width : CardWidth, 6, colour));
            y += 6 + Gap * 1.5f;
        }

        var notices = _notices.Current;
        if (notices.Count == 0)
        {
            var (empty, emptyHeight) = text("empty", T("notices.empty"), CardWidth, FontSize);
            quads.Add(rect(empty, x + Margin, y + 20, CardWidth, emptyHeight, new RenderColor(1, 1, 1, 0.6f)));
            return;
        }
        var shown = new List<Notice>();
        foreach (var notice in notices)
        {
            var (texture, height) = card(notice);
            if (y + height > StageHeight - Margin)
                break; // ponytail: no scrolling; the oldest that do not fit are not drawn
            quads.Add(rect(texture, x + Margin, y, CardWidth, height));
            shown.Add(notice);
            y += height + Gap;
        }
        report(shown);
    }

    // Tells the owner once per notice that it was on screen.
    private void report(IEnumerable<Notice> notices)
    {
        var fresh = notices.Where(notice => _reported.Add(notice.Id)).ToList();
        if (fresh.Count > 0)
            _shown(fresh);
    }

    // One notice as a card: a severity stripe, where it came from, and the message wrapped.
    private (RenderTextureId Texture, float Height) card(Notice notice)
    {
        var scale = scaleNow();
        if (_textures.TryGetValue((notice.Id, scale), out var cached))
            return cached;
        var lines = TextWrap.Lines(notice.Message, (CardWidth - 36) / (FontSize * 0.5f), MaxLines);
        var height = 16 + 20 + lines.Count * LineHeight + 12;
        var canvas = new VectorCanvas((int)CardWidth, height, (int)scale, _fontPath);
        canvas.RoundedRect(0, 0, CardWidth, height, 8, (32, 33, 40), outline: 1.5f, outlineColour: (70, 72, 84));
        canvas.RoundedRect(6, 8, 5, height - 16, 2.5f, notice.Severity switch
        {
            NoticeSeverity.Critical => (235, 72, 60),
            NoticeSeverity.Warning => (245, 180, 40),
            _ => (90, 170, 255),
        }, outline: 0);
        var source = notice.Source switch
        {
            NoticeSource.Personal when notice.Baid is { } baid && _playerName(baid) is { } name => T("notices.source.player", name),
            NoticeSource.Local => T("notices.source.local"),
            _ => T("notices.source.server"),
        };
        var at = notice.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        canvas.Text($"{source} · {at}", 22, 16 + 9, CardWidth - 36, 16, anchorX: 0, tint: (170, 175, 190));
        for (var line = 0; line < lines.Count; line++)
            canvas.Text(lines[line], 22, 16 + 20 + line * LineHeight + LineHeight / 2f, CardWidth - 36, FontSize, anchorX: 0);
        var texture = _application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        return _textures[(notice.Id, scale)] = (texture, height);
    }

    private (RenderTextureId Texture, float Height) text(string key, string value, float width, int height, float anchor = 0.5f)
    {
        var scale = scaleNow();
        if (_textures.TryGetValue(($"text:{key}:{value}", scale), out var cached))
            return cached;
        var canvas = new VectorCanvas((int)width, height, (int)scale, _fontPath);
        canvas.Text(value, width * anchor, height / 2f, width, height, anchorX: anchor);
        var texture = _application.UploadRgba8((uint)canvas.PixelWidth, (uint)canvas.PixelHeight, canvas.Pixels);
        return _textures[($"text:{key}:{value}", scale)] = (texture, height);
    }

    private uint scaleNow() => (uint)Math.Clamp((_application.GetPixelSize().Height + 719) / 720, 1, 3);

    private static RenderQuad rect(RenderTextureId texture, float x, float y, float width, float height, RenderColor? tint = null) =>
        RenderQuad.FromRectangles(texture, new(x / Stage, y / StageHeight, width / Stage, height / StageHeight),
            RenderRectangle.Full, tint ?? RenderColor.White, RenderColor.Transparent);

    public void Dispose()
    {
        _notices.Added -= _arrived.Enqueue;
        _application.ReleaseTexture(_white);
        foreach (var (texture, _) in _textures.Values)
            _application.ReleaseTexture(texture);
    }
}
