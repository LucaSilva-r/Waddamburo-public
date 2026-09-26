using System.Diagnostics;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>F5 diagnostic HUD. Text uses a small built-in bitmap alphabet so sampling never uploads textures.</summary>
internal sealed class PerformanceOverlay : IDisposable
{
    private const int ScreenWidth = 1280, ScreenHeight = 720;
    private const int Margin = 12, CompactWidth = 112, CompactHeight = 38;
    private const int ExpandedWidth = 296, ExpandedHeight = 178;
    private const int CellWidth = 6, CellHeight = 8, AtlasColumns = 16;
    private readonly SdlApplication _application;
    private readonly Func<AudioEngine?> _audio;
    private RenderTextureId? _glyphs;
    private RenderTextureId? _compactBackground;
    private RenderTextureId? _expandedBackground;
    private RenderQuad[] _quads = [];
    private bool _hovered;
    private bool _dirty = true;
    private long _sampleStart;
    private AudioPerformanceCounters _audioStart;
    private int _frames, _ticks;
    private double _inputMs, _updateMs, _drawMs;
    private double _fps, _inputRate, _audioRate, _updateRate;
    private double _intervalMs, _inputWorkMs, _audioWorkMs, _updateWorkMs, _drawWorkMs;

    public PerformanceOverlay(SdlApplication application, Func<AudioEngine?> audio)
    {
        _application = application;
        _audio = audio;
    }

    public bool Visible { get; private set; }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible && _glyphs is null)
        {
            _glyphs = _application.UploadRgba8(96, 32, makeGlyphAtlas());
            _compactBackground = _application.UploadRgba8(CompactWidth, CompactHeight,
                makeBackground(CompactWidth, CompactHeight, expanded: false));
            _expandedBackground = _application.UploadRgba8(ExpandedWidth, ExpandedHeight,
                makeBackground(ExpandedWidth, ExpandedHeight, expanded: true));
        }
        _audio()?.SetPerformanceMonitoring(Visible);
        _sampleStart = 0;
        _frames = _ticks = 0;
        _inputMs = _updateMs = _drawMs = 0;
        _dirty = true;
    }

    public void SetPointer(float x, float y)
    {
        var px = x * ScreenWidth;
        var py = y * ScreenHeight;
        var pill = px >= ScreenWidth - Margin - CompactWidth && px < ScreenWidth - Margin
            && py >= Margin && py < Margin + CompactHeight;
        var panel = _hovered && px >= ScreenWidth - Margin - ExpandedWidth && px < ScreenWidth - Margin
            && py >= Margin && py < Margin + ExpandedHeight;
        var hovered = pill || panel;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            _dirty = true;
        }
    }

    public void Record(SdlFrameMetrics frame)
    {
        if (!Visible) return;
        var now = Stopwatch.GetTimestamp();
        if (_sampleStart == 0)
        {
            _sampleStart = now;
            _audioStart = _audio()?.GetPerformanceCounters() ?? default;
        }
        _frames++;
        _ticks += frame.SimulationTicks;
        _inputMs += frame.Input.TotalMilliseconds;
        _updateMs += frame.Update.TotalMilliseconds;
        _drawMs += frame.Draw.TotalMilliseconds;
        var seconds = Stopwatch.GetElapsedTime(_sampleStart, now).TotalSeconds;
        if (seconds < 0.5) return;
        _fps = _frames / seconds;
        _inputRate = _fps;
        _updateRate = _ticks / seconds;
        _intervalMs = 1000 / _fps;
        _inputWorkMs = _inputMs / _frames;
        _updateWorkMs = _ticks == 0 ? 0 : _updateMs / _ticks;
        _drawWorkMs = _drawMs / _frames;
        var audioEnd = _audio()?.GetPerformanceCounters() ?? default;
        var blocks = Math.Max(0, audioEnd.Blocks - _audioStart.Blocks);
        _audioRate = blocks / seconds;
        _audioWorkMs = blocks == 0 ? 0 : (audioEnd.WorkTicks - _audioStart.WorkTicks)
            * 1000d / Stopwatch.Frequency / blocks;
        _audioStart = audioEnd;
        _sampleStart = now;
        _frames = _ticks = 0;
        _inputMs = _updateMs = _drawMs = 0;
        _dirty = true;
    }

    public IEnumerable<RenderQuad> Quads()
    {
        if (!Visible) return [];
        if (_dirty) rebuild();
        return _quads;
    }

    public void Dispose()
    {
        if (_glyphs is { } glyphs) _application.ReleaseTexture(glyphs);
        if (_compactBackground is { } compact) _application.ReleaseTexture(compact);
        if (_expandedBackground is { } expanded) _application.ReleaseTexture(expanded);
    }

    private void rebuild()
    {
        var quads = new List<RenderQuad>(110);
        var width = _hovered ? ExpandedWidth : CompactWidth;
        var height = _hovered ? ExpandedHeight : CompactHeight;
        var left = ScreenWidth - Margin - width;
        var top = Margin;
        quads.Add(rect(_hovered ? _expandedBackground!.Value : _compactBackground!.Value,
            left, top, width, height, RenderColor.White));
        var pillLeft = ScreenWidth - Margin - CompactWidth;
        var timingColor = _intervalMs > 8.33 ? rgb(255, 105, 105)
            : _intervalMs > 4.17 ? rgb(255, 206, 91) : rgb(133, 229, 130);
        write(quads, $"{_intervalMs:F1} MS", pillLeft + 11, top + 5, 2, timingColor);
        write(quads, $"{_fps:F0} FPS", pillLeft + 11, top + 21, 1, rgb(255, 157, 121));
        if (_hovered)
        {
            write(quads, "PERFORMANCE", left + 14, top + 52, 2, rgb(242, 244, 250));
            write(quads, "F5", left + 264, top + 55, 1, rgb(142, 148, 161));
            row(quads, left, top + 80, "POLL", _inputRate, "/S", _inputWorkMs, rgb(140, 200, 255));
            row(quads, left, top + 103, "AUDIO", _audioRate, "/S", _audioWorkMs, rgb(195, 157, 255));
            row(quads, left, top + 126, "UPDATE", _updateRate, "/S", _updateWorkMs, rgb(255, 209, 125));
            row(quads, left, top + 149, "DRAW", _fps, "FPS", _drawWorkMs, rgb(133, 229, 130));
        }
        _quads = [.. quads];
        _dirty = false;
    }

    private void row(List<RenderQuad> quads, int left, int y, string name, double rate, string unit,
        double milliseconds, RenderColor color)
    {
        write(quads, name, left + 15, y, 1, color);
        write(quads, $"{rate:F0}{unit}", left + 106, y, 1, rgb(236, 237, 242));
        write(quads, $"{milliseconds:F2}MS", left + 206, y, 1, rgb(180, 185, 196));
    }

    private void write(List<RenderQuad> quads, string value, int x, int y, int scale, RenderColor color)
    {
        foreach (var character in value.ToUpperInvariant())
        {
            if (character is < ' ' or > '_') { x += CellWidth * scale; continue; }
            var index = character - ' ';
            quads.Add(RenderQuad.FromRectangles(_glyphs!.Value,
                new RenderRectangle(x / (float)ScreenWidth, y / (float)ScreenHeight,
                    5f * scale / ScreenWidth, 7f * scale / ScreenHeight),
                new RenderRectangle((index % AtlasColumns * CellWidth) / 96f,
                    (index / AtlasColumns * CellHeight) / 32f, 5f / 96, 7f / 32),
                color, RenderColor.Transparent, RenderSampling.Nearest));
            x += CellWidth * scale;
        }
    }

    private static RenderQuad rect(RenderTextureId texture, int x, int y, int width, int height, RenderColor color) =>
        RenderQuad.FromRectangles(texture,
            new RenderRectangle(x / (float)ScreenWidth, y / (float)ScreenHeight,
                width / (float)ScreenWidth, height / (float)ScreenHeight),
            RenderRectangle.Full, color, RenderColor.Transparent, RenderSampling.Nearest);

    private static RenderColor rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1);

    private static byte[] makeBackground(int width, int height, bool expanded)
    {
        var pixels = new byte[width * height * 4];
        if (expanded)
        {
            rounded(pixels, width, height, 0, 44, width, height - 44, 11, 12, 13, 19, 231);
            rounded(pixels, width, height, width - CompactWidth, 0, CompactWidth, CompactHeight,
                9, 12, 13, 19, 231);
        }
        else
            rounded(pixels, width, height, 0, 0, width, height, 9, 12, 13, 19, 231);
        return pixels;
    }

    private static void rounded(byte[] pixels, int canvasWidth, int canvasHeight, int x, int y,
        int width, int height, int radius, byte r, byte g, byte b, byte alpha)
    {
        for (var py = y; py < y + height && py < canvasHeight; py++)
            for (var px = x; px < x + width && px < canvasWidth; px++)
            {
                var dx = Math.Max(Math.Abs(px + .5f - (x + width / 2f)) - (width / 2f - radius), 0);
                var dy = Math.Max(Math.Abs(py + .5f - (y + height / 2f)) - (height / 2f - radius), 0);
                if (dx * dx + dy * dy > radius * radius) continue;
                var offset = (py * canvasWidth + px) * 4;
                pixels[offset] = r; pixels[offset + 1] = g; pixels[offset + 2] = b; pixels[offset + 3] = alpha;
            }
    }

    private static byte[] makeGlyphAtlas()
    {
        var pixels = new byte[96 * 32 * 4];
        foreach (var (character, rows) in Glyphs)
        {
            var index = character - ' ';
            var left = index % AtlasColumns * CellWidth;
            var top = index / AtlasColumns * CellHeight;
            for (var y = 0; y < 7; y++)
                for (var x = 0; x < 5; x++)
                    if (rows[y][x] == '#')
                    {
                        var offset = ((top + y) * 96 + left + x) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 255;
                    }
        }
        return pixels;
    }

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = [".###.","#...#","#..##","#.#.#","##..#","#...#",".###."],
        ['1'] = ["..#..",".##..","..#..","..#..","..#..","..#..",".###."],
        ['2'] = [".###.","#...#","....#","...#.","..#..",".#...","#####"],
        ['3'] = ["####.","....#","....#",".###.","....#","....#","####."],
        ['4'] = ["...#.","..##.",".#.#.","#..#.","#####","...#.","...#."],
        ['5'] = ["#####","#....","#....","####.","....#","....#","####."],
        ['6'] = [".###.","#....","#....","####.","#...#","#...#",".###."],
        ['7'] = ["#####","....#","...#.","..#..",".#...",".#...",".#..."],
        ['8'] = [".###.","#...#","#...#",".###.","#...#","#...#",".###."],
        ['9'] = [".###.","#...#","#...#",".####","....#","....#",".###."],
        ['A'] = [".###.","#...#","#...#","#####","#...#","#...#","#...#"],
        ['B'] = ["####.","#...#","#...#","####.","#...#","#...#","####."],
        ['C'] = [".####","#....","#....","#....","#....","#....",".####"],
        ['D'] = ["####.","#...#","#...#","#...#","#...#","#...#","####."],
        ['E'] = ["#####","#....","#....","####.","#....","#....","#####"],
        ['F'] = ["#####","#....","#....","####.","#....","#....","#...."],
        ['I'] = [".###.","..#..","..#..","..#..","..#..","..#..",".###."],
        ['M'] = ["#...#","##.##","#.#.#","#.#.#","#...#","#...#","#...#"],
        ['N'] = ["#...#","##..#","##..#","#.#.#","#..##","#..##","#...#"],
        ['O'] = [".###.","#...#","#...#","#...#","#...#","#...#",".###."],
        ['P'] = ["####.","#...#","#...#","####.","#....","#....","#...."],
        ['R'] = ["####.","#...#","#...#","####.","#.#..","#..#.","#...#"],
        ['S'] = [".####","#....","#....",".###.","....#","....#","####."],
        ['T'] = ["#####","..#..","..#..","..#..","..#..","..#..","..#.."],
        ['U'] = ["#...#","#...#","#...#","#...#","#...#","#...#",".###."],
        ['W'] = ["#...#","#...#","#...#","#.#.#","#.#.#","##.##","#...#"],
        ['/'] = ["....#","....#","...#.","..#..",".#...","#....","#...."],
        ['.'] = [".....",".....",".....",".....",".....","..##.","..##."],
        ['-'] = [".....",".....",".....","#####",".....",".....","....."],
    };
}
