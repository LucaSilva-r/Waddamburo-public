using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

/// <summary>The bars between the panels: drag to resize. The sizes are kept for the next time the editor opens.</summary>
public sealed partial class MovieInspectorSession
{
    private static readonly string LayoutFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Waddamburo", "movie-editor-layout.txt");
    private float _left = .2f, _right = .24f, _bottom = .34f;
    private int _divider;
    private float _dividerOrigin;
    private bool _layoutLoaded;

    private void dividers(EditorLayout layout, Vector2 size)
    {
        if (!_layoutLoaded)
        {
            _layoutLoaded = true;
            try
            {
                var saved = File.ReadAllText(LayoutFile).Split(' ').Select(static each => float.Parse(each, CultureInfo.InvariantCulture)).ToArray();
                if (saved.Length == 3 && saved.All(float.IsFinite))
                    (_left, _right, _bottom) = (saved[0], saved[1], saved[2]);
            }
            catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
            {
                // No saved sizes yet (or unreadable ones): the defaults.
            }
        }
        divider(1, new(layout.Stage.X - 6, layout.Targets.Y, 8, layout.Targets.Height), size);
        divider(2, new(layout.Inspector.X - 6, layout.Targets.Y, 8, layout.Targets.Height), size);
        divider(3, new(0, layout.Tracks.Y - 6, size.X, 8), size);
        if (_divider != 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _divider = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
                File.WriteAllText(LayoutFile, string.Create(CultureInfo.InvariantCulture, $"{_left} {_right} {_bottom}"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _notice = "Panel sizes were not saved: " + exception.Message;
            }
        }
    }

    private void divider(int id, EditorPanel rect, Vector2 size)
    {
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (panel($"##movie divider{id}", rect, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.InvisibleButton("Resize", Vector2.Max(Vector2.One, rect.Size));
            var hot = ImGui.IsItemHovered() || _divider == id;
            if (hot)
            {
                ImGui.SetMouseCursor(id == 3 ? ImGuiMouseCursor.ResizeNS : ImGuiMouseCursor.ResizeEW);
                var centre = rect.Position + rect.Size / 2;
                ImGui.GetWindowDrawList().AddLine(id == 3 ? new(rect.X, centre.Y) : new(centre.X, rect.Y),
                    id == 3 ? new(rect.X + rect.Width, centre.Y) : new(centre.X, rect.Y + rect.Height), Outline, 2);
            }
            if (ImGui.IsItemActivated())
            {
                _divider = id;
                _dividerOrigin = id == 1 ? _left : id == 2 ? _right : _bottom;
            }
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                (_left, _right, _bottom) = (id == 1 ? .2f : _left, id == 2 ? .24f : _right, id == 3 ? .34f : _bottom);
            else if (_divider == id && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                if (id == 1) _left = Math.Clamp(_dividerOrigin + delta.X / size.X, .08f, .45f);
                else if (id == 2) _right = Math.Clamp(_dividerOrigin - delta.X / size.X, .1f, .45f);
                else _bottom = Math.Clamp(_dividerOrigin - delta.Y / size.Y, .12f, .75f);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }
}
