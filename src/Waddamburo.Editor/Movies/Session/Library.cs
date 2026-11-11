using System.Text.Json.Nodes;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>
/// The library: every sprite the movie defines, on stage or not. Choosing one plays it alone, with its layers and
/// keys in the Timeline tab; this is how a big movie is taken apart to see what it is made of.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private string _libraryFilter = "";
    private string? _shown;

    // The sprites entered one inside the other (Flash's "edit in place" trail); empty: the whole movie.
    private readonly List<string> _trail = [];

    /// <summary>Goes into the sprite a clip is an instance of: it plays alone, and Back returns to where it was entered from.</summary>
    private bool enter(LumenInstanceInfo clip)
    {
        if (ShowSprite is null || clip.Kind != LumenInstanceKind.Sprite || clip.Level == 0
            || _document?.CharacterName(clip.CharacterId) is not { } name)
            return false;
        show(name, keepTrail: true);
        _trail.Add(name);
        return true;
    }

    /// <summary>Back to trail entry <paramref name="count"/> (0: the whole movie).</summary>
    private void leaveTo(int count)
    {
        var target = count <= 0 ? null : _trail[count - 1];
        _trail.RemoveRange(Math.Max(count, 0), _trail.Count - Math.Max(count, 0));
        show(target, keepTrail: true);
    }

    // The trail as buttons: movie > sprite > sprite.
    private void breadcrumb()
    {
        if (_trail.Count == 0)
            return;
        if (ImGui.SmallButton("movie"))
            leaveTo(0);
        for (var index = 0; index < _trail.Count; index++)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(">");
            ImGui.SameLine();
            if (ImGui.SmallButton($"{_trail[index]}##trail{index}") && index + 1 < _trail.Count)
            {
                leaveTo(index + 1);
                break;
            }
        }
    }

    private void show(string? sprite, bool keepTrail = false)
    {
        if (!keepTrail)
        {
            _trail.Clear();
            if (sprite is not null)
                _trail.Add(sprite);
        }
        _shown = sprite;
        _timelineContext.Reset();
        _timelineAssetId = null;
        _key = null;
        selectClip(null);
        _open.Clear();
        ShowSprite!(sprite);
        Paused = false;
    }

    /// <summary>Shows the first sprite whose name contains the text (for screenshots and scripted runs).</summary>
    public bool ShowSpriteNamed(string contains)
    {
        var name = (_document?.Root["sprites"] as JsonObject)?.Select(static entry => entry.Key)
            .FirstOrDefault(key => key.Contains(contains, StringComparison.OrdinalIgnoreCase));
        if (name is null || ShowSprite is null)
            return false;
        show(name);
        return true;
    }

    private void library()
    {
        var sprites = _document!.Root["sprites"] as JsonObject ?? [];
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##library filter", "filter: name or label", ref _libraryFilter, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(_shown is null);
        if (ImGui.Button("Whole movie"))
            show(null);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"{sprites.Count} sprites. Click one to play it alone; its layers are in Timeline.");
        if (!ImGui.BeginTable("##library", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("sprite", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("frames", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("layers", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("scripts", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("labels", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 4);
        ImGui.TableSetupColumn("used in", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 3);
        ImGui.TableHeadersRow();
        var rows = rowsOf(sprites);
        var sort = ImGui.TableGetSortSpecs();
        if (sort.SpecsCount > 0)
        {
            var spec = sort.Specs;
            Comparison<LibraryRow> order = spec.ColumnIndex switch
            {
                1 => static (a, b) => a.Frames.CompareTo(b.Frames),
                2 => static (a, b) => a.Layers.CompareTo(b.Layers),
                3 => static (a, b) => a.Scripts.CompareTo(b.Scripts),
                _ => static (a, b) => string.CompareOrdinal(a.Name, b.Name),
            };
            rows.Sort(spec.SortDirection == ImGuiSortDirection.Descending ? (a, b) => order(b, a) : order);
        }
        foreach (var row in rows)
        {
            if (_libraryFilter.Length > 0 && !row.Name.Contains(_libraryFilter, StringComparison.OrdinalIgnoreCase)
                && !row.Labels.Contains(_libraryFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable(row.Name, _shown == row.Name, ImGuiSelectableFlags.SpanAllColumns))
                show(row.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Layers.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Scripts == 0 ? "" : row.Scripts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Labels);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Users);
        }
        ImGui.EndTable();
    }

    private sealed record LibraryRow(string Name, int Frames, int Layers, int Scripts, string Labels, string Users);

    // Built once per document state: "used in" walks every layer of the movie.
    private List<LibraryRow>? _libraryRows;
    private JsonObject? _libraryOf;

    private List<LibraryRow> rowsOf(JsonObject sprites)
    {
        if (_libraryRows is not null && ReferenceEquals(_libraryOf, sprites) && _libraryRows.Count == sprites.Count)
            return _libraryRows;
        var users = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (parent, sprite) in sprites)
            foreach (var layer in sprite?["layers"] as JsonArray ?? [])
                if ((string?)layer?["character"] is { } character)
                    (users.TryGetValue(character, out var set) ? set : users[character] = new(StringComparer.Ordinal)).Add(parent);
        _libraryOf = sprites;
        return _libraryRows = [.. sprites.Select(entry => new LibraryRow(entry.Key, (int?)entry.Value?["frames"] ?? 1,
            (entry.Value?["layers"] as JsonArray)?.Count ?? 0, (entry.Value?["scripts"] as JsonObject)?.Count ?? 0,
            string.Join(", ", (entry.Value?["labels"] as JsonObject ?? []).OrderBy(static label => (int)label.Value!).Select(static label => label.Key)),
            (string?)_document!.Root["root"] == entry.Key ? "(root)" : users.TryGetValue(entry.Key, out var used) ? string.Join(", ", used) : ""))];
    }
}
