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
    private string _title;
    private bool _gameData;
    private MovieDocument? _document;
    public Action<bool>? SetScriptsEnabled { get; set; }
    private bool _disposed;
    private bool _deleteAsked, _deleteKeys, _timelineShown, _keysClickedLast; // Delete goes to what was clicked last: keys or a clip.
    private readonly Func<bool>? _previousClose;
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
        _layer.CaptureKey = static key => key is ImGuiKey.Space or ImGuiKey.RightArrow or ImGuiKey.LeftArrow;
        _previousClose = application.ConfirmClose;
        application.ConfirmClose = confirmWindowClose;
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
    public bool TakeTick() => !Paused;

    public void SetDocument(MovieDocument? document, string title, bool gameData)
    {
        _document = document;
        _title = title;
        _gameData = gameData;
        selectClip(null);
        _shown = null;
        _timelineContext.Reset();
        _timelineAssetId = null;
        _trail.Clear();
        _assetNames.Clear();
        _assetFolder = "";
        _texturesView = false;
        _key = null;
        _notice = null;
        Paused = true;
        _layer.FileDropped = document is { ReadOnly: false } ? path => importTexture(path) : null;
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

    private string? _drawError;
    private bool _failNow; // WADDAMBURO_EDITOR_FAIL: a panel fails once on frame 120, to check the window survives

    // A mistake in one panel must not take the window (and the unsaved work) with it: the frame is cut short where it
    // failed, ImGui closes what was left open, and the error stays on screen. Undo and save still work.
    // ponytail: one catch for the whole frame, so panels after the failing one are missing while it fails; per-panel
    // recovery needs ImGui's internal state restore, which the binding does not expose.
    private void draw()
    {
        var io = ImGui.GetIO();
        var size = io.DisplaySize;
        if (size.X < 32 || size.Y < 32)
            return;
        try
        {
            drawPanels(io, size);
            _drawError = null;
        }
        catch (Exception error)
        {
            if (_drawError != error.Message)
                Console.Error.WriteLine($"Movie editor: a panel failed to draw: {error}");
            _drawError = error.Message;
            io.ConfigErrorRecoveryEnableAssert = false;
            io.ConfigErrorRecoveryEnableTooltip = false;
            var text = $"Editor error (Ctrl+Z undoes the last change, Ctrl+S saves): {error.GetType().Name}: {error.Message}";
            var at = new Vector2(8, size.Y - ImGui.GetTextLineHeight() - 8);
            var list = ImGui.GetForegroundDrawList();
            list.AddRectFilled(at - new Vector2(4), at + ImGui.CalcTextSize(text) + new Vector2(4), color(.35f, 0, 0));
            list.AddText(at, color(1, 1, 1), text);
        }
    }

    private void drawPanels(ImGuiIOPtr io, Vector2 size)
    {
        if (ImGui.GetFrameCount() == 120 && Environment.GetEnvironmentVariable("WADDAMBURO_EDITOR_FAIL") is not null)
            _failNow = true;
        pollAssetFile();
        refreshAssetSelection();
        pollMovieFiles();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false)) saveDocument();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.O, false)) pickMovie(false);
        if (!io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Space, false)) togglePlayback();
            // Delete (or Backspace) removes what was clicked last, wherever the pointer is: the selected keys, or the
            // selected clip. Carried out by the panel that owns them, this frame.
            _deleteAsked = _document is { ReadOnly: false } && _renaming is null
                && (ImGui.IsKeyPressed(ImGuiKey.Delete, false) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false));
            _deleteKeys = _deleteAsked && _timelineShown && _keysClickedLast && _keys.Count > 0;
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) navigateTimeline(1, io.KeyCtrl);
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) navigateTimeline(-1, io.KeyCtrl);
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
            {
                _assetGesture = _gesture = false;
                edited(() => { if (io.KeyShift) _document?.Redo(); else _document?.Undo(); });
            }
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y, false))
                edited(() => _document?.Redo());
        }
        else _deleteAsked = _deleteKeys = false;
        _timelineShown = false;
        var menuHeight = menuBar();
        movieDialogs();
        var player = _player();
        var clips = player.InspectDisplayList();
        _selection.RemoveWhere(path => clips.All(clip => clip.Path != path));
        if (_selected is not null && clips.All(clip => clip.Path != _selected))
            _selected = null;
        var layout = EditorLayout.Calculate(size.X, size.Y, _left, _right, _bottom, topHeight: menuHeight);
        _application.StageRegion = EditorLayout.Preview(layout.Stage).Normalized(size.X, size.Y);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        tree(layout.Targets, clips);
        if (_failNow) { _failNow = false; throw new InvalidOperationException("test failure"); }
        inspector(layout.Inspector, player, clips, clips.FirstOrDefault(clip => clip.Path == _selected));
        bottom(layout.Tracks, player, clips);
        stage(layout.Stage, player, clips);
        dividers(layout, size);
        ImGui.PopStyleVar();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _application.ConfirmClose = _previousClose;
        _layer.Dispose();
        _application.StageBox = null;
        _application.StageRegion = _previousRegion;
        _disposed = true;
    }
}
