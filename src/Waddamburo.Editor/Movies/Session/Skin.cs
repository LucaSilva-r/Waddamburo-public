using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Editor;

/// <summary>
/// The skin around the component editor: the home page (every screen of the skin as a flow map, every package as a
/// component library), the scene view (a screen's layers placed, ordered, added by dragging a component in) and the
/// live scene window over the component editor, which follows each unsaved edit. Set up by the host with
/// <see cref="Skin"/> and <see cref="Scenes"/>; without them the editor is the single-package editor it was.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private enum Page { Home, Scene, Component }

    /// <summary>The skin's packages, played for previews (null: no skin, a single package).</summary>
    public SkinStage? Skin { get; set; }

    /// <summary>The skin's scenes.json.</summary>
    public SceneDocument? Scenes { get; set; }

    private Page _page = Page.Component;
    private string? _sceneKey;      // the screen in the scene view, and in the live window
    private int _sceneLayer = -1;   // its selected layer
    private bool _liveWindow;       // the live scene window over the component editor
    private string _componentSearch = "";
    private string? _dragPackage;   // a component being dragged from a list
    private int _thumbBudget;
    private readonly Dictionary<string, (RenderTextureId Texture, int Version)> _thumbs = new(StringComparer.Ordinal);
    private RenderTextureId? _sceneTexture;
    private Vector2 _sceneTextureSize;
    private SkinStage.Scene? _liveScene;
    private (string Key, int Version, string Text, PreviewScenario? Scenario)? _liveBuilt;
    private double _liveClock;
    private (int Version, IReadOnlyList<string> Folders)? _packages;
    private string? _skinName;

    private const string Decoration = "decoration";
    private static readonly uint Accent = color(1, .56f, .18f), Card = color(.13f, .14f, .16f), CardBorder = color(.25f, .27f, .3f),
        Edge = color(.45f, .5f, .56f), Good = color(.45f, .8f, .5f), Bad = color(1, .45f, .35f);

    // Where each screen sits on the flow map (column, row): the way through a credit left to right, the dan dojo
    // below it, the transitions in a row of their own.
    private static readonly (string Key, int Column, int Row)[] FlowNodes =
    [
        ("boot", 0, 0), ("entry", 1, 0), ("song-select", 2, 0), ("rainbow", 3, 0), ("gameplay", 4, 0), ("result", 5, 0),
        ("tutorial", 1, 1), ("dani-select", 2, 1), ("dan-cover", 3, 1), ("dani-result", 5, 1),
        ("fade", 0, 2), ("shutter", 1, 2), ("dan-exit-cover", 2, 2), ("dan-shutter", 3, 2),
    ];

    private static readonly (string From, string To)[] FlowEdges =
    [
        ("boot", "entry"), ("entry", "song-select"), ("entry", "tutorial"), ("entry", "dani-select"), ("song-select", "rainbow"),
        ("rainbow", "gameplay"), ("gameplay", "result"), ("dani-select", "dan-cover"), ("dan-cover", "gameplay"), ("gameplay", "dani-result"),
    ];

    /// <summary>Starts on the skin's home page.</summary>
    public void ShowHome() => _page = Page.Home;

    /// <summary>
    /// The game the editor was started from shows another screen: the scene view follows it (from the home page or the
    /// scene view; the component editor stays, its live window showing that screen when the package is on it).
    /// </summary>
    /// <summary>What the game says it could not do (a screen it cannot reach from where it is).</summary>
    public void GameNotice(string text) => _notice = text;

    public void FollowGame(string key)
    {
        if (!SkinScenes.Screens.ContainsKey(key))
            return;
        if (_page != Page.Component)
            openScene(key);
        else if (usedIn().GetValueOrDefault(relativeFolder() ?? "")?.Contains(key) == true)
            _sceneKey = key;
    }

    /// <summary>Starts on a screen's scene view (a key of <see cref="SkinScenes.Screens"/>).</summary>
    public void ShowScene(string key) => openScene(key);

    /// <summary>Starts on the home page's Text tab (text styles and fonts).</summary>
    public void ShowText()
    {
        _page = Page.Home;
        _openTextTab = true;
    }

    private bool _openTextTab;
    private bool _textTabOpen; // the home page's Text tab is the one shown (as of the last frame)

    private bool textTabShown => _page == Page.Home && _textTabOpen && _textStyles is not null;

    private void undoRedo(bool text, bool redo)
    {
        if (text) { if (redo) _textStyles!.Redo(); else _textStyles!.Undo(); }
        else if (redo) Scenes?.Redo();
        else Scenes?.Undo();
    }

    private int previewVersion => HashCode.Combine(Skin!.Version, Scenes?.Text, _scenarioVersion);

    private IReadOnlyList<string> packages()
    {
        if (_packages is { } known && known.Version == Skin!.Version)
            return known.Folders;
        var folders = Skin!.Packages();
        _packages = (Skin.Version, folders);
        return folders;
    }

    private string skinName()
    {
        if (_skinName is not null)
            return _skinName;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Skin!.Root, "skin.json")));
            _skinName = manifest.RootElement.GetProperty("name").GetString() ?? "Skin";
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _skinName = Path.GetFileName(Skin!.Root);
        }
        return _skinName;
    }

    // Which screens use each package.
    private Dictionary<string, List<string>> usedIn()
    {
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (Scenes is null)
            return used;
        foreach (var key in SkinScenes.Screens.Keys)
            foreach (var layer in Scenes.Layers(key))
                if (!(used.TryGetValue(layer.Movie, out var list) ? list : used[layer.Movie] = []).Contains(key))
                    used[layer.Movie].Add(key);
        return used;
    }

    // A thumbnail drawn once per change of the skin (a few per frame, the rest wait); null until its first drawing.
    private nint? thumbnail(string id, Func<SkinStage.Scene> play)
    {
        var known = _thumbs.TryGetValue(id, out var thumb);
        if (known && thumb.Version == previewVersion)
            return (nint)thumb.Texture.Value;
        if (_thumbBudget <= 0)
            return known ? (nint)thumb.Texture.Value : null;
        var texture = known ? thumb.Texture : _application.CreatePreview(320, 180);
        _thumbs[id] = (texture, known ? thumb.Version : int.MinValue);
        if (!_application.DrawPreview(texture, play().Frame()))
            return known ? (nint)texture.Value : null;
        _thumbBudget--;
        _thumbs[id] = (texture, previewVersion);
        return known ? (nint)texture.Value : null; // a new one is drawn with this frame: shown from the next
    }

    private SkinStage.Scene playScene(string key, int ticks)
    {
        var scene = Skin!.PlayScreen(key, Scenes!.Layers(key), scenario: scenario(key));
        scene.Advance(ticks);
        return scene;
    }

    // The screen playing live (scene view, live window): rebuilt when the skin or the screen changes, its unchanged
    // players carrying on; drawn each frame into a texture of the given pixel size showing the given part of the stage
    // (null: the screen), so what a layer draws past the screen's edges shows too.
    private nint? liveScene(string key, (float X, float Y, float Width, float Height)? view = null, Vector2? pixels = null)
    {
        var chosen = scenario(key);
        if (_liveBuilt is not { } built || built.Key != key || built.Version != Skin!.Version || built.Text != Scenes!.Text || built.Scenario != chosen)
        {
            _liveScene = Skin!.PlayScreen(key, Scenes!.Layers(key), _liveBuilt?.Key == key ? _liveScene : null, chosen);
            _liveBuilt = (key, Skin.Version, Scenes.Text, chosen);
        }
        _liveClock = Math.Min(_liveClock + ImGui.GetIO().DeltaTime * 60, 4);
        for (; _liveClock >= 1; _liveClock--)
            _liveScene!.Advance();
        var size = Vector2.Clamp(Vector2.Round(pixels ?? new(1280, 720)), new(16), new(4096));
        if (_sceneTextureSize != size && _sceneTexture is { } old)
        {
            _application.ReleasePreview(old);
            _sceneTexture = null;
        }
        _sceneTexture ??= _application.CreatePreview((int)size.X, (int)size.Y);
        _sceneTextureSize = size;
        _application.DrawPreview(_sceneTexture.Value, _liveScene!.Frame(view: view));
        return (nint)_sceneTexture.Value.Value;
    }

    // A screen picked by the user (the home page, the live window): the game it was started from goes there too.
    private void pickScene(string key)
    {
        openScene(key);
        Skin?.Live?.Show(key);
    }

    private void openScene(string key)
    {
        _sceneKey = key;
        select(-1, 0);
        _page = Page.Scene;
    }

    private void openPackage(string folder)
    {
        if (!Skin!.Exists(folder))
        {
            // A slot the screens name with no package yet: an empty one, to draw in.
            var directory = Path.Combine(Skin.Root, folder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, Formats.Package.MoviePackage.FileName),
                "{\n  \"format\": 2,\n  \"frameRate\": 60,\n  \"root\": \"main\",\n  \"sprites\": { \"main\": { \"frames\": 1, \"layers\": [] } }\n}\n");
            Skin.Reload();
        }
        _sceneKey ??= usedIn().GetValueOrDefault(folder)?.FirstOrDefault();
        _openMoviePath = Path.Combine(Skin.Root, folder, Formats.Package.MoviePackage.FileName);
        _openArchiveMovie = null;
        requestOpenOrClose(false);
    }

    private void editScene(Action<List<SkinScenes.Layer>> change, bool continues = false)
    {
        try
        {
            Scenes!.Edit(_sceneKey!, change, continues);
            _notice = null;
        }
        catch (InvalidOperationException refused)
        {
            _notice = refused.Message;
        }
    }

    private bool saveScenes()
    {
        try { Scenes?.Save(); _notice = null; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _notice = "Could not save scenes.json: " + error.Message; return false; }
    }

    // The home page and the scene view take the whole window; the component editor is the panels as before.
    private void skinPages(ImGuiIOPtr io, Vector2 size)
    {
        _thumbBudget = 2;
        pollProblems();
        _application.StageRegion = new RenderRectangle(0, 0, .0001f, .0001f); // the package's own stage hides under the page
        if (!io.WantTextInput)
        {
            // On the Text tab the keys work on text.json, elsewhere on scenes.json.
            var text = textTabShown;
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false)) { if (text) _textStyles?.Save(); else saveScenes(); }
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false)) undoRedo(text, io.KeyShift);
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y, false)) undoRedo(text, true);
        }
        var top = skinMenuBar();
        movieDialogs();
        if (_page == Page.Home) homePage(top, size);
        else scenePage(top, size);
        dragGhost();
    }

    private float skinMenuBar()
    {
        var height = ImGui.GetFrameHeight();
        if (!ImGui.BeginMainMenuBar()) return height;
        height = ImGui.GetWindowSize().Y;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Home", "", _page == Page.Home)) _page = Page.Home;
            if (ImGui.MenuItem("Save scenes", "Ctrl+S", false, Scenes?.IsDirty == true)) saveScenes();
            if (ImGui.MenuItem("Export skin (zip)")) exportSkin();
            if (ImGui.MenuItem("Copy assistant setup (MCP)")) copyMcpSetup();
            ImGui.Separator();
            if (ImGui.MenuItem("Close")) requestOpenOrClose(true);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            var text = textTabShown;
            if (ImGui.MenuItem(text ? "Undo (text.json)" : "Undo", "Ctrl+Z", false, text ? _textStyles?.CanUndo == true : Scenes?.CanUndo == true)) undoRedo(text, false);
            if (ImGui.MenuItem(text ? "Redo (text.json)" : "Redo", "Ctrl+Shift+Z", false, text ? _textStyles?.CanRedo == true : Scenes?.CanRedo == true)) undoRedo(text, true);
            ImGui.EndMenu();
        }
        if (relativeFolder() is not null && ImGui.MenuItem($"Back to {_title}")) _page = Page.Component;
        ImGui.TextDisabled(skinName() + (Scenes?.IsDirty == true ? "  (scenes.json *)" : ""));
        if (problemCount(SkinLint.Level.Error) is var errors and > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(Bad), $"{errors} problem{(errors == 1 ? "" : "s")}");
        }
        if (Skin?.Live is { } live)
        {
            ImGui.SameLine();
            ImGui.TextColored(live.Connected ? ImGui.ColorConvertU32ToFloat4(Good) : ImGui.ColorConvertU32ToFloat4(Bad),
                live.Connected ? $"game: {live.Screen ?? "..."}" : "game closed");
        }
        if (_notice is not null || Scenes?.Problem is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(new(1, .45f, .35f, 1), _notice ?? $"scenes.json: {Scenes!.Problem}");
        }
        ImGui.EndMainMenuBar();
        var title = $"Waddamburo — {skinName()}{(Scenes?.IsDirty == true ? " *" : "")}";
        if (_windowTitle != title) { _application.SetWindowTitle(title); _windowTitle = title; }
        return height;
    }

    // The component editor's Skin menu (in its own menu bar).
    private void skinMenu()
    {
        if (Skin is null || !ImGui.BeginMenu("Skin")) return;
        if (ImGui.MenuItem("Home")) _page = Page.Home;
        if (_sceneKey is not null && ImGui.MenuItem($"Scene: {_sceneKey}")) _page = Page.Scene;
        ImGui.MenuItem("Live scene window", "F4", ref _liveWindow, _sceneKey is not null || usedIn().Count > 0);
        ImGui.EndMenu();
    }

    private void homePage(float top, Vector2 size)
    {
        if (panel("##skin home", new(0, top, size.X, size.Y - top), ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.TextUnformatted(skinName());
            ImGui.SameLine();
            ImGui.TextDisabled(Skin!.Root);
            if (ImGui.BeginTabBar("##home tabs"))
            {
                if (ImGui.BeginTabItem("Scenes"))
                {
                    flowMap();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Components"))
                {
                    components(open: openPackage, columns: 0);
                    ImGui.EndTabItem();
                }
                var openText = _openTextTab;
                _openTextTab = false;
                var always = true;
                var textLabel = _textStyles?.IsDirty == true ? "Text*###text" : "Text###text";
                // Selected from outside (--inspect=text) through the overload that takes flags, for that one frame.
                _textTabOpen = false;
                if (openText ? ImGui.BeginTabItem(textLabel, ref always, ImGuiTabItemFlags.SetSelected) : ImGui.BeginTabItem(textLabel))
                {
                    _textTabOpen = true;
                    textTab();
                    ImGui.EndTabItem();
                }
                var errors = problemCount(SkinLint.Level.Error);
                if (ImGui.BeginTabItem(errors > 0 ? $"Problems ({errors})###problems" : "Problems###problems"))
                {
                    problemsTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private void flowMap()
    {
        ImGui.TextDisabled("Every screen of the skin, the way a credit goes through them. Click one to compose it.");
        ImGui.BeginChild("##flow", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(8, 8);
        var available = ImGui.GetContentRegionAvail();
        const float gap = 34;
        var width = Math.Clamp((available.X - 16 - gap * 5) / 6, 130, 300);
        var height = width * 9 / 16;
        var rowHeight = height + 46 + gap;
        Vector2 at(int column, int row) => origin + new Vector2(column * (width + gap), row * rowHeight + (row == 2 ? 26 : 0));
        var place = FlowNodes.ToDictionary(static node => node.Key, node => at(node.Column, node.Row));
        var rows = FlowNodes.ToDictionary(static node => node.Key, static node => node.Row);
        var list = ImGui.GetWindowDrawList();
        list.AddText(at(0, 2) - new Vector2(0, 22), Muted, "Transitions (over the screens, between them)");
        foreach (var (from, to) in FlowEdges)
        {
            var (a, b) = (place[from], place[to]);
            var (start, end, bend) = rows[to] > rows[from] ? (a + new Vector2(width / 2, height), b + new Vector2(width / 2, -4), new Vector2(0, gap))
                : rows[to] < rows[from] ? (a + new Vector2(width / 2, -4), b + new Vector2(width / 2, height + 40), new Vector2(0, -gap))
                : (a + new Vector2(width, height / 2), b + new Vector2(0, height / 2), new Vector2(gap / 2, 0));
            list.AddBezierCubic(start, start + bend, end - bend, end, Edge, 2);
            var direction = Vector2.Normalize(bend);
            var side = new Vector2(-direction.Y, direction.X);
            list.AddTriangleFilled(end, end - direction * 9 + side * 5, end - direction * 9 - side * 5, Edge);
        }
        foreach (var (key, _, _) in FlowNodes)
        {
            var corner = place[key];
            ImGui.SetCursorScreenPos(corner);
            ImGui.PushID(key);
            if (ImGui.InvisibleButton("node", new(width, height + 40)))
                pickScene(key);
            var hovered = ImGui.IsItemHovered();
            ImGui.PopID();
            list.AddRectFilled(corner, corner + new Vector2(width, height), Card, 4);
            if (thumbnail("scene:" + key, () => playScene(key, 90)) is { } texture)
                list.AddImageRounded(texture, corner, corner + new Vector2(width, height), Vector2.Zero, Vector2.One, uint.MaxValue, 4);
            list.AddRect(corner, corner + new Vector2(width, height), hovered ? Accent : CardBorder, 4, ImDrawFlags.None, hovered ? 2 : 1);
            var layers = Scenes!.Layers(key);
            list.AddText(corner + new Vector2(2, height + 4), Text, key);
            var engine = Skin!.Fixed?.Invoke(key).Count ?? 0;
            list.AddText(corner + new Vector2(2, height + 21), Muted, $"{layers.Count} layer{(layers.Count == 1 ? "" : "s")}"
                + (engine > 0 ? $" over the layout's {engine}" : Scenes.Composes(key) ? "" : " · built-in"));
            if (hovered)
                ImGui.SetTooltip(string.Join('\n', layers.Select(static layer => $"{layer.Movie}  ({layer.Role})")) is { Length: > 0 } names ? names : "No layers");
        }
        ImGui.SetCursorScreenPos(at(0, 3));
        ImGui.Dummy(new(6 * width + 5 * gap, 1));
        ImGui.EndChild();
    }

    // The component library: every package, grouped by its area, with a thumbnail; slots the screens name without a
    // package are dimmed (a click makes one). Columns 0: as many as fit; a press starts a drag (to a scene's stage).
    private void components(Action<string> open, int columns)
    {
        ImGui.SetNextItemWidth(Math.Min(320, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##component search", "Search components", ref _componentSearch, 128);
        if (columns == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Click one to edit it. In a scene, drag one onto the stage to add it.");
        }
        var used = usedIn();
        var all = packages().Union(used.Keys).Where(folder => _componentSearch.Length == 0
            || folder.Contains(_componentSearch, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        ImGui.BeginChild("##components");
        var cardWidth = columns == 0 ? 196f : (ImGui.GetContentRegionAvail().X - 8) / columns;
        var thumbHeight = cardWidth * 9 / 16;
        foreach (var area in all.GroupBy(static folder => folder.Split('/')[0]))
        {
            ImGui.SeparatorText(area.Key);
            var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            var first = true;
            foreach (var folder in area)
            {
                if (!first && ImGui.GetItemRectMax().X + 8 + cardWidth <= right)
                    ImGui.SameLine(0, 8);
                first = false;
                var corner = ImGui.GetCursorScreenPos();
                ImGui.PushID(folder);
                var clicked = ImGui.InvisibleButton("card", new(cardWidth, thumbHeight + 38));
                var hovered = ImGui.IsItemHovered();
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 6) && Skin!.Exists(folder))
                    _dragPackage = folder;
                else if (clicked && _dragPackage is null)
                    open(folder);
                ImGui.PopID();
                var list = ImGui.GetWindowDrawList();
                var exists = Skin!.Exists(folder);
                list.AddRectFilled(corner, corner + new Vector2(cardWidth, thumbHeight), Card, 4);
                if (exists && thumbnail("package:" + folder, () => Skin.PlayFitted(folder, 90)) is { } texture)
                    list.AddImageRounded(texture, corner, corner + new Vector2(cardWidth, thumbHeight), Vector2.Zero, Vector2.One, uint.MaxValue, 4);
                else if (!exists)
                    list.AddText(corner + new Vector2(8, thumbHeight / 2 - 8), Muted, "no package yet: click to make one");
                list.AddRect(corner, corner + new Vector2(cardWidth, thumbHeight), hovered ? Accent : CardBorder, 4, ImDrawFlags.None, hovered ? 2 : 1);
                var name = folder[(folder.LastIndexOf('/') + 1)..];
                list.AddText(corner + new Vector2(2, thumbHeight + 3), exists ? Text : Muted, name);
                var screens = used.GetValueOrDefault(folder);
                list.AddText(corner + new Vector2(2, thumbHeight + 20), Muted, screens is null ? "in no screen" : string.Join(", ", screens));
                if (hovered)
                    ImGui.SetTooltip(folder);
            }
        }
        ImGui.EndChild();
    }

    private void dragGhost()
    {
        if (_dragPackage is null)
            return;
        var mouse = ImGui.GetMousePos();
        var list = ImGui.GetForegroundDrawList();
        var size = ImGui.CalcTextSize(_dragPackage);
        list.AddRectFilled(mouse + new Vector2(12, 8), mouse + new Vector2(20, 14) + size, color(.1f, .1f, .12f, .9f), 3);
        list.AddText(mouse + new Vector2(16, 10), Accent, _dragPackage);
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _dragPackage = null; // dropped (a stage took it this frame, before this) or let go elsewhere
    }

    // The live scene window over the component editor: the screen with the package being edited, following each edit.
    private void liveWindow()
    {
        if (Skin is null)
            return;
        if (!ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F4, false))
            _liveWindow = !_liveWindow;
        if (!_liveWindow)
            return;
        var screens = _document is null ? [] : usedIn().GetValueOrDefault(relativeFolder() ?? "") ?? [];
        if (_sceneKey is null || !SkinScenes.Screens.ContainsKey(_sceneKey))
            _sceneKey = screens.FirstOrDefault() ?? "entry";
        ImGui.SetNextWindowSize(new(520, 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize - new Vector2(560, 400), ImGuiCond.FirstUseEver);
        if (ImGui.Begin($"Scene: {_sceneKey}###live scene", ref _liveWindow, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.SetNextItemWidth(180);
            if (ImGui.BeginCombo("##live screen", _sceneKey))
            {
                foreach (var key in screens.Concat(SkinScenes.Screens.Keys).Distinct())
                    if (ImGui.Selectable(key + (screens.Contains(key) ? "" : "  (does not use this)"), key == _sceneKey))
                        _sceneKey = key;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Open scene")) _page = Page.Scene;
            if (Skin.Live is { Connected: true } live && live.Screen != _sceneKey)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Show in game")) live.Show(_sceneKey);
            }
            scenarioPicker(_sceneKey);
            var area = ImGui.GetContentRegionAvail();
            var scale = Math.Max(.01f, Math.Min(area.X / 1280, area.Y / 720));
            if (liveScene(_sceneKey) is { } texture)
            {
                ImGui.SetCursorPos(ImGui.GetCursorPos() + (area - new Vector2(1280, 720) * scale) / 2);
                ImGui.Image(texture, new Vector2(1280, 720) * scale);
            }
        }
        ImGui.End();
    }

    // The package being edited, as a folder of the skin (null: none, or one from elsewhere).
    private string? relativeFolder() => _document is { ReadOnly: false } document && Skin is not null
        ? Path.GetRelativePath(Skin.Root, Path.GetDirectoryName(document.Path)!).Replace('\\', '/') : null;
}
