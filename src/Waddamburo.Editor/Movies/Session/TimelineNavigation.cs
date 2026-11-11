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
        if (_timelineContext.Path.Length == 0) breadcrumb();
    }
}
