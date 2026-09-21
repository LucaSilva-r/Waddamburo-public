using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

/// <summary>SDL presentation/input adapter for platform-neutral Taiko gameplay state.</summary>
internal sealed class TaikoGameplayPresentation : IDisposable
{
    private const float StageWidth = 1280;
    private const float StageHeight = 720;
    private const float HitX = 414;
    private const float PlayerOneY = 272;
    private const float TravelPixelsPerSecond = 420;
    private static readonly TimeSpan LookBehind = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan LookAhead = TimeSpan.FromSeconds(2.5);
    private static readonly TaikoJudgementWindows DefaultWindows = new(
        TimeSpan.FromMilliseconds(35),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(95));

    private readonly SdlApplication _application;
    private readonly RenderTextureId _donTexture;
    private readonly RenderTextureId _kaTexture;
    private readonly HashSet<SdlKeyboardKey> _previousKeys = [];
    private PlayableChart[] _charts = [];
    private TaikoJudgementSession[] _sessions = [];
    private bool _disposed;

    public TaikoGameplayPresentation(SdlApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _donTexture = application.UploadRgba8(64, 64, createNoteTexture(220, 48, 36));
        _kaTexture = application.UploadRgba8(64, 64, createNoteTexture(46, 145, 220));
    }

    public void Start(IEnumerable<PlayableChart> charts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(charts);
        _charts = charts.ToArray();
        if (_charts.Length != 1)
            throw new NotSupportedException("The initial gameplay presentation supports exactly one local player.");
        _sessions = _charts
            .Select(chart => new TaikoJudgementSession(
                chart,
                DefaultWindows,
                TimeSpan.FromMilliseconds(30)))
            .ToArray();
        _previousKeys.Clear();
    }

    public void Stop()
    {
        _charts = [];
        _sessions = [];
        _previousKeys.Clear();
    }

    public void Advance(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(keyboard);
        if (_sessions.Length == 0)
            return;

        _sessions[0].AdvanceTo(chartTime);
        submitOnPress(keyboard, SdlKeyboardKey.F, TaikoInputAction.LeftDon, chartTime);
        submitOnPress(keyboard, SdlKeyboardKey.J, TaikoInputAction.RightDon, chartTime);
        submitOnPress(keyboard, SdlKeyboardKey.D, TaikoInputAction.LeftKa, chartTime);
        submitOnPress(keyboard, SdlKeyboardKey.K, TaikoInputAction.RightKa, chartTime);

        _previousKeys.Clear();
        _previousKeys.UnionWith(keyboard.PressedKeys);
    }

    public RenderFrame Compose(RenderFrame lumenFrame, TimeSpan chartTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lumenFrame);
        if (_charts.Length == 0)
            return lumenFrame;

        var quads = lumenFrame.Quads.ToBuilder();
        appendChart(quads, _charts[0], _sessions[0].CreateSnapshot(), chartTime, PlayerOneY);
        return new RenderFrame(lumenFrame.ClearColor, quads, lumenFrame.ContentAspectRatio);
    }

    private void submitOnPress(
        SdlKeyboardSnapshot keyboard,
        SdlKeyboardKey key,
        TaikoInputAction action,
        TimeSpan chartTime)
    {
        if (keyboard.IsDown(key) && !_previousKeys.Contains(key))
            _sessions[0].SubmitInput(action, chartTime);
    }

    private void appendChart(
        System.Collections.Immutable.ImmutableArray<RenderQuad>.Builder quads,
        PlayableChart chart,
        System.Collections.Immutable.ImmutableArray<TaikoNoteJudgement> judgements,
        TimeSpan chartTime,
        float centreY)
    {
        for (var index = 0; index < chart.HitObjects.Length; index++)
        {
            if (judgements[index].Result is not null)
                continue;
            var note = chart.HitObjects[index];
            var until = note.StartTime - chartTime;
            if (until < -LookBehind || until > LookAhead)
                continue;
            var scroll = scrollAt(chart, note.StartTime);
            var centreX = HitX + (float)until.TotalSeconds * TravelPixelsPerSecond * (float)scroll;
            var diameter = note.IsStrong ? 72f : 52f;
            if (centreX + diameter / 2 < 0 || centreX - diameter / 2 > StageWidth)
                continue;
            quads.Add(RenderQuad.FromRectangles(
                note.Kind is PlayableNoteKind.Don or PlayableNoteKind.BigDon ? _donTexture : _kaTexture,
                new RenderRectangle(
                    (centreX - diameter / 2) / StageWidth,
                    (centreY - diameter / 2) / StageHeight,
                    diameter / StageWidth,
                    diameter / StageHeight),
                RenderRectangle.Full,
                RenderColor.White,
                RenderColor.Transparent));
        }
    }

    private static double scrollAt(PlayableChart chart, TimeSpan time)
    {
        var multiplier = chart.ScrollPoints[0].Multiplier;
        foreach (var point in chart.ScrollPoints)
        {
            if (point.Time > time)
                break;
            multiplier = point.Multiplier;
        }
        return multiplier;
    }

    private static byte[] createNoteTexture(byte red, byte green, byte blue)
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        var centre = (size - 1) / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = MathF.Sqrt(MathF.Pow(x - centre, 2) + MathF.Pow(y - centre, 2));
                var offset = (y * size + x) * 4;
                if (distance > 31)
                    continue;
                var border = distance >= 26;
                pixels[offset] = border ? (byte)255 : red;
                pixels[offset + 1] = border ? (byte)255 : green;
                pixels[offset + 2] = border ? (byte)255 : blue;
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _application.ReleaseTexture(_donTexture);
        _application.ReleaseTexture(_kaTexture);
    }
}
