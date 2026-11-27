using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private readonly MovieTimelineContext _timelineContext = new();
    private string? _timelineAssetId;

    private LumenInstanceInfo? resolveTimeline(IReadOnlyList<LumenInstanceInfo> clips)
    {
        if (_timelineAssetId is not null)
        {
            var asset = _document!.Assets().FirstOrDefault(asset => asset.Id == _timelineAssetId);
            if (asset is null || _document.CharacterId(asset.Name) is not { } character) return null;
            // Package character indices can change when artwork is added. The editor identity stays stable.
            _timelineContext.Open(_timelineContext.Path, character);
        }
        return _timelineContext.Resolve(clips);
    }

    private void openInstanceTimeline(string path)
    {
        var clip = _player().InspectDisplayList().FirstOrDefault(clip => clip.Path == path);
        if (clip?.Kind != LumenInstanceKind.Sprite || _document?.CharacterName(clip.CharacterId) is not { } name) return;
        _timelineAssetId = _document.AssetId("sprites", name);
        _timelineContext.Open(path, clip.CharacterId);
        _key = null;
        _length = null;
        Paused = true;
    }

    private bool nested => _timelineContext.Path.Length > 0 || _trail.Count > 0;

    /// <summary>One level up: out of the instance timeline being edited (to its parent's), else out of the sprite entered.</summary>
    private void goUp()
    {
        if (_timelineContext.Path.Length > 0)
        {
            var path = _timelineContext.Path;
            var parent = path.LastIndexOf('/') is >= 0 and var cut ? path[..cut] : "";
            if (parent.Length > 0) openInstanceTimeline(parent);
            else
            {
                _timelineContext.Reset();
                _timelineAssetId = null;
                _key = null;
            }
        }
        else if (_trail.Count > 0)
            leaveTo(_trail.Count - 1);
    }

    // The way back on the stage itself, while inside a nested movie (the Timeline tab has the same; it may be hidden).
    private void stageBreadcrumb(EditorPanel area, IReadOnlyList<LumenInstanceInfo> clips)
    {
        if (!ImGui.GetIO().WantTextInput && ImGui.GetIO().KeyAlt && ImGui.IsKeyPressed(ImGuiKey.UpArrow, false) && nested)
            goUp();
        if (!nested)
            return;
        ImGui.SetCursorScreenPos(area.Position + new System.Numerics.Vector2(RulerSize + 8, RulerSize + 8));
        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(.15f, .16f, .19f, .92f));
        if (ImGui.SmallButton("Up")) goUp();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("One level up (Alt+Up)");
        ImGui.SameLine();
        timelineNavigation(clips);
        ImGui.PopStyleColor();
    }

    private void timelineNavigation(IReadOnlyList<LumenInstanceInfo> clips)
    {
        ImGui.TextDisabled("Editing timeline:");
        foreach (var path in _timelineContext.Ancestors())
        {
            var clip = clips.FirstOrDefault(clip => clip.Path == path);
            if (path == _timelineContext.Path && resolveTimeline(clips) is null) clip = null;
            var name = path.Length == 0 ? _shown is null ? "Movie" : "Isolated " + _shown
                : clip is null ? "Absent instance" : clip.Name.Length > 0 ? clip.Name : $"Sprite #{clip.CharacterId}";
            ImGui.SameLine();
            if (path.Length > 0) { ImGui.TextDisabled(">"); ImGui.SameLine(); }
            var clock = clip is null ? "" : $"  [{clip.Frame}]";
            ImGui.BeginDisabled(clip is null);
            if (ImGui.SmallButton(name + clock + "##timeline path " + path)) openInstanceTimeline(path);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("This sprite's own frame counter. Click to edit this timeline.");
        }
        if (_timelineContext.Path.Length == 0 && _trail.Count > 0)
        {
            ImGui.SameLine(0, 16);
            ImGui.TextDisabled("Entered:");
            ImGui.SameLine();
            breadcrumb();
        }
    }
}
