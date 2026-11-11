using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

/// <summary>
/// "What do I do here": the movie's job list. The states the game jumps to, the clips it addresses and the calls it
/// makes, each with how far it is; a click goes there.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private IReadOnlyList<MovieTodo> _todo = [];
    private long _todoRevision = -1;
    private bool _todoAll;

    private void todo()
    {
        var document = _document!;
        if (_todoRevision != document.Revision || ImGui.IsWindowAppearing())
        {
            _todo = document.Todo();
            _todoRevision = document.Revision;
        }
        var left = _todo.Count(static item => item.State != MovieTodoState.Done);
        ImGui.TextUnformatted(_todo.Count == 0 ? "This movie has no states, named clips or calls yet."
            : left == 0 ? $"All {_todo.Count} done." : $"{left} of {_todo.Count} left.");
        ImGui.SameLine();
        ImGui.Checkbox("Show finished", ref _todoAll);
        ImGui.SameLine();
        ImGui.TextDisabled("Click a line to go there.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A movie is driven by the game: it jumps to states (labels), fills or moves clips by name, and calls functions.\n"
                + "Draw each state on its frames in the Timeline, replace the placeholder boxes, write the calls in the script.");
        ImGui.BeginChild("##todo");
        void section(string kind, string title, string hint)
        {
            var items = _todo.Where(item => item.Kind == kind && (_todoAll || item.State != MovieTodoState.Done)).ToArray();
            if (items.Length == 0 || !ImGui.CollapsingHeader($"{title}  ({items.Length})###{kind}", ImGuiTreeNodeFlags.DefaultOpen))
                return;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
            foreach (var item in items)
            {
                var colour = item.State switch
                {
                    MovieTodoState.Missing => new Vector4(1, .45f, .35f, 1),
                    MovieTodoState.Placeholder => new Vector4(1, .78f, .3f, 1),
                    _ => new Vector4(.45f, .8f, .5f, 1),
                };
                ImGui.PushID($"{item.Kind}/{item.Sprite}/{item.Name}/{item.Line}");
                ImGui.TextColored(colour, item.State switch { MovieTodoState.Missing => "to do", MovieTodoState.Placeholder => "placeholder", _ => "done" });
                ImGui.SameLine(110);
                var root = item.Sprite == (string?)document.Root["root"];
                if (ImGui.Selectable($"{(item.Sprite.Length == 0 || root ? "" : item.Sprite + " › ")}{item.Name}", false, ImGuiSelectableFlags.None, new(320, 0)))
                {
                    if (item.File is { } file) openCode(file, item.Line);
                    else if (root)
                    {
                        _timelineContext.Reset();
                        _player().Seek(item.Frame);
                        Paused = true; // the stage shows the state's first frame; the Timeline tab has its layers
                    }
                    else selectAsset("sprites", item.Sprite);
                }
                ImGui.SameLine(440);
                ImGui.TextDisabled(item.Detail);
                ImGui.PopID();
            }
        }
        section("state", "States", "Labels the game (or a script) jumps to. Each plays from its frame until a script stops it.");
        section("clip", "Clips", "Sprites the game finds by name: it moves them, hides them or draws into them.");
        section("call", "Calls", "Functions the game calls on this movie. Opens the script in VS Code.");
        ImGui.EndChild();
    }
}
