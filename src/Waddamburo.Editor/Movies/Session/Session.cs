using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Editor;

/// <summary>
/// Coordinates the movie editor's panels, preview transport and host integration. Feature implementations and
/// their state live in the matching partial files; package edits use MovieDocument, while original movies are
/// inspected as read-only references. See docs/development/free-movies-plan.md.
/// </summary>
public sealed partial class MovieInspectorSession : IDisposable
{
    private readonly SdlApplication _application;
    private readonly ImGuiLayer _layer;
    private readonly RenderRectangle? _previousRegion;
    private readonly Func<LumenPlayer> _player;
    private readonly string _title;
    private readonly bool _gameData;
    private readonly MovieDocument? _document;
    public Action<bool>? SetScriptsEnabled { get; set; }
    private bool _step, _disposed;
    private static readonly uint Outline = color(1, .56f, .18f), Muted = color(.52f, .56f, .61f), Text = color(.85f, .87f, .9f);

    /// <param name="application">The window.</param>
    /// <param name="player">The movie being shown (asked every frame: a reloaded package is a new player).</param>
    /// <param name="title">The movie's name.</param>
    /// <param name="gameData">The movie comes from the user's game data: shown as read-only reference.</param>
    /// <param name="select">A clip to select at once, by instance name (for screenshots).</param>
    /// <param name="document">A package's movie.json, to edit it; null shows the movie without editing.</param>
    public MovieInspectorSession(SdlApplication application, Func<LumenPlayer> player, string title, bool gameData, string? select = null,
        MovieDocument? document = null)
    {
        _document = document;
        _selected = select is null ? null : player().InspectDisplayList().FirstOrDefault(clip => clip.Name == select)?.Path;
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(player);
        _application = application;
        _player = player;
        _title = title;
        _gameData = gameData;
        _previousRegion = application.StageRegion;
        _layer = application.AttachImGui(draw);
        EditorTheme.Apply();
        _layer.CaptureKey = static key => key is ImGuiKey.Space or ImGuiKey.RightArrow;
        _layer.CaptureControlShortcuts = true;
        // A PNG or SVG dropped on the window becomes a texture of the package.
        if (document is { ReadOnly: false })
            _layer.FileDropped = path => importTexture(path);
    }

    /// <summary>The scripts left out of the running movie because they do not compile (set by the host on each load).</summary>
    public IReadOnlyList<Waddamburo.Formats.Package.ActionScriptException> ScriptErrors { get; set; } = [];

    /// <summary>A package texture by its name in movie.json, for ImGui.Image thumbnails (set by the host).</summary>
    public Func<string, (nint Texture, int Width, int Height)?>? TextureOf { get; set; }
    public Func<string, nint?>? IconOf { get; set; }

    /// <summary>Shows one sprite of the movie alone on stage, by its name in the document; null: the whole movie (set by the host).</summary>
    public Action<string?>? ShowSprite { get; set; }

    /// <summary>The movie is held on its current frame.</summary>
    public bool Paused { get; private set; }

    /// <summary>Whether the movie advances this tick: playing, or a single step was asked for.</summary>
    public bool TakeTick()
    {
        var advance = !Paused || _step;
        _step = false;
        return advance;
    }

    private static uint color(float r, float g, float b, float a = 1) => ImGui.ColorConvertFloat4ToU32(new(r, g, b, a));

    private static string number(double value) => (Math.Abs(value) < .0005 ? 0 : value).ToString("0.###", CultureInfo.InvariantCulture);

    private static bool panel(string name, EditorPanel rect, ImGuiWindowFlags extra = 0)
    {
        ImGui.SetNextWindowPos(rect.Position);
        ImGui.SetNextWindowSize(rect.Size);
        return ImGui.Begin(name, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings | extra);
    }

    private void draw()
    {
        var io = ImGui.GetIO();
        var size = io.DisplaySize;
        if (size.X < 32 || size.Y < 32)
            return;
        pollAssetFile();
        refreshAssetSelection();
        if (!io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
                Paused = !Paused;
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
                (Paused, _step) = (true, true);
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            _assetGesture = _gesture = false;
            edited(() => { if (io.KeyShift) _document?.Redo(); else _document?.Undo(); });
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y, false))
        {
            _assetGesture = _gesture = false;
            edited(() => _document?.Redo());
        }
        var player = _player();
        var clips = player.InspectDisplayList();
        _selection.RemoveWhere(path => clips.All(clip => clip.Path != path));
        if (_selected is not null && clips.All(clip => clip.Path != _selected))
            _selected = null;
        var layout = EditorLayout.Calculate(size.X, size.Y, .24f, .26f, .3f);
        _application.StageRegion = EditorLayout.Preview(layout.Stage).Normalized(size.X, size.Y);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        toolbar(layout.Toolbar, player);
        tree(layout.Targets, clips);
        inspector(layout.Inspector, player, clips, clips.FirstOrDefault(clip => clip.Path == _selected));
        bottom(layout.Tracks, player, clips);
        stage(layout.Stage, player, clips);
        ImGui.PopStyleVar();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _layer.Dispose();
        _application.StageRegion = _previousRegion;
        _disposed = true;
    }
}
