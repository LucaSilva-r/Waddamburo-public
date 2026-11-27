using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using Waddamburo.Formats.Text;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Editor;

/// <summary>
/// Text authoring: the skin home's Text tab (text.json's fonts and styles, each style shown on sample texts), the
/// text field inspector (its own text, its style, its bind, its own settings over the style) and the style settings
/// both share. Previews are drawn as the game draws text fields (the host's <see cref="TextTexture"/>), so what is
/// seen here is what the game shows.
/// </summary>
public sealed partial class MovieInspectorSession
{
    /// <summary>The skin's text.json (null: no skin).</summary>
    public TextDocument? TextStyles
    {
        get => _textStyles;
        set
        {
            _textStyles = value;
            if (value is not null)
                value.Changed += () =>
                {
                    // Unsaved styles are drawn at once, on the stage and in every preview.
                    if (value.Parsed is { } parsed) SkinText.Current = parsed;
                };
        }
    }

    /// <summary>A text field's texture as the game draws it (the host's text renderer), for previews.</summary>
    public Func<LumenNativeSurfaceKey, RenderTextureId?>? TextTexture { get; set; }

    private TextDocument? _textStyles;
    private string? _textStyle;
    private string _textSample = "";
    private string _newFontName = "";
    private string _bindSearch = "";
    private uint _styleGesture;

    private static readonly string[] Samples = ["1,002,340", "プレイヤーエントリー", "Senbonzakura 千本桜", "The quick brown fox jumps over the lazy dog"];
    private static readonly string[] LookKeys = ["font", "size", "spacing", "lineHeight", "slant", "align", "valign", "fit", "vertical", "layers", "color"];

    // An edit of one control: a new undo step when the control starts, the same one while it is dragged.
    private bool styleContinues()
    {
        var id = ImGui.GetItemID();
        var continues = id == _styleGesture && !ImGui.IsItemActivated();
        _styleGesture = ImGui.IsItemActive() ? id : 0;
        return continues;
    }

    private void textTab()
    {
        if (_textStyles is not { } document)
        {
            ImGui.TextDisabled("No skin folder.");
            return;
        }
        if (!ImGui.IsAnyItemActive())
            _styleGesture = 0; // a new drag is a new undo step
        var styles = document.Json["styles"] as JsonObject;
        if (_textStyle is not null && styles?[_textStyle] is null)
            _textStyle = null;
        _textStyle ??= styles?.FirstOrDefault().Key;
        if (ImGui.Button(document.IsDirty ? "Save text.json*" : "Saved") && document.IsDirty)
            document.Save();
        ImGui.SameLine();
        ImGui.BeginDisabled(!document.CanUndo);
        if (ImGui.Button("Undo##text")) document.Undo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!document.CanRedo);
        if (ImGui.Button("Redo##text")) document.Redo();
        ImGui.EndDisabled();
        if (document.Problem is { } problem)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, .45f, .35f, 1), $"The game ignores text.json: {problem}");
        }
        if (ImGui.BeginTable("##text tab", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthFixed, 280);
            ImGui.TableSetupColumn("style", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextColumn();
            styleList(document);
            fontList(document);
            ImGui.TableNextColumn();
            if (_textStyle is { } name && styles?[name] is JsonObject style)
            {
                // Settings beside the samples: every change is seen at once.
                ImGui.BeginChild("##style settings", new Vector2(Math.Min(460, ImGui.GetContentRegionAvail().X * .45f), 0));
                ImGui.SeparatorText(name);
                ImGui.PushItemWidth(-150);
                styleSettings("style", style, null, (change, continues) => document.Edit(_ => change(), continues));
                ImGui.PopItemWidth();
                ImGui.EndChild();
                ImGui.SameLine();
                ImGui.BeginChild("##style samples");
                ImGui.SeparatorText("Samples");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##sample", "Your own text to try", ref _textSample, 400);
                var settings = new JsonObject { ["style"] = name }.ToJsonString();
                var vertical = style["vertical"]?.GetValueKind() == JsonValueKind.True;
                var size = (float)(TextStyleJson.Number(style["size"]) ?? 24);
                foreach (var sample in _textSample.Length > 0 ? [_textSample, .. Samples] : Samples)
                {
                    var box = vertical ? new Vector2(size * 1.6f, 420) : new Vector2(Math.Max(120, ImGui.GetContentRegionAvail().X - size * 1.2f), size * 1.6f);
                    textPreview(sample, settings, box, size, 'l', 0xffffff);
                    if (vertical) ImGui.SameLine();
                }
                ImGui.EndChild();
            }
            else
                ImGui.TextDisabled("No style yet: make one on the left.");
            ImGui.EndTable();
        }
    }

    private void styleList(TextDocument document)
    {
        ImGui.SeparatorText("Styles");
        var styles = document.Json["styles"] as JsonObject;
        foreach (var (name, _) in styles ?? [])
            if (ImGui.Selectable(name, name == _textStyle))
                _textStyle = name;
        if (ImGui.Button("New"))
            document.Edit(root =>
            {
                var list = root["styles"] as JsonObject ?? (JsonObject)(root["styles"] = new JsonObject());
                var name = free(list, "style");
                list[name] = new JsonObject
                {
                    ["size"] = 32,
                    ["layers"] = new JsonArray(new JsonObject { ["grow"] = 4, ["color"] = "#203080" }, new JsonObject { ["color"] = "#ffffff" }),
                };
                _textStyle = name;
            });
        ImGui.SameLine();
        ImGui.BeginDisabled(_textStyle is null);
        if (ImGui.Button("Duplicate") && _textStyle is { } copied)
            document.Edit(root =>
            {
                var list = (JsonObject)root["styles"]!;
                var name = free(list, copied);
                list[name] = list[copied]!.DeepClone();
                _textStyle = name;
            });
        ImGui.SameLine();
        if (ImGui.Button("Delete") && _textStyle is { } deleted)
            document.Edit(root => ((JsonObject)root["styles"]!).Remove(deleted));
        ImGui.EndDisabled();
        if (_textStyle is { } current)
        {
            var renamed = current;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##rename", ref renamed, 64, ImGuiInputTextFlags.EnterReturnsTrue) && renamed.Length > 0
                && renamed != current && styles?[renamed] is null)
                document.Edit(root =>
                {
                    var list = (JsonObject)root["styles"]!;
                    var entries = list.ToList();
                    list.Clear();
                    foreach (var (key, value) in entries)
                        list[key == current ? renamed : key] = value;
                    _textStyle = renamed;
                });
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rename (Enter). Text fields naming the old name keep it: rename them too.");
        }

        static string free(JsonObject list, string name)
        {
            var made = name;
            for (var n = 2; list[made] is not null; n++)
                made = $"{name}-{n}";
            return made;
        }
    }

    // The skin's fonts: a name for each font file, which styles pick by name. Files are the skin's own (fonts/).
    private void fontList(TextDocument document)
    {
        ImGui.SeparatorText("Fonts");
        ImGui.TextDisabled($"{SkinText.GameFont}: the game's own");
        var fonts = document.Json["fonts"] as JsonObject;
        foreach (var (name, file) in fonts?.ToList() ?? [])
        {
            ImGui.PushID(name);
            var missing = !File.Exists(Path.Combine(document.Root, (string?)file ?? ""));
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            ImGui.TextColored(missing ? new Vector4(1, .45f, .35f, 1) : new Vector4(.6f, .62f, .66f, 1), (string?)file ?? "");
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) document.Edit(root => ((JsonObject)root["fonts"]!).Remove(name));
            ImGui.PopID();
        }
        var files = Directory.EnumerateFiles(document.Root, "*.*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc")
            .Select(file => Path.GetRelativePath(document.Root, file).Replace('\\', '/'))
            .Where(file => fonts?.All(font => (string?)font.Value != file) ?? true).Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            ImGui.TextWrapped("Put a font file (.ttf, .otf) in the skin's fonts/ folder to use it. Ship only fonts whose licence lets others share them.");
            return;
        }
        ImGui.SetNextItemWidth(100);
        ImGui.InputTextWithHint("##font name", "name", ref _newFontName, 32);
        ImGui.SameLine();
        if (ImGui.BeginCombo("##font file", "Add a font...", ImGuiComboFlags.NoArrowButton))
        {
            foreach (var file in files)
                if (ImGui.Selectable(file))
                {
                    var name = _newFontName.Length > 0 && _newFontName != SkinText.GameFont ? _newFontName : Path.GetFileNameWithoutExtension(file);
                    document.Edit(root => (root["fonts"] as JsonObject ?? (JsonObject)(root["fonts"] = new JsonObject()))[name] = file);
                    _newFontName = "";
                }
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// A style's settings, edited in <paramref name="settings"/> (changed in place inside <paramref name="edit"/>). With
    /// <paramref name="inherited"/> (a text field over its style): a setting the field does not set shows the style's,
    /// and setting it makes it the field's own (shown in the accent colour, with x to give it back to the style).
    /// </summary>
    private void styleSettings(string id, JsonObject settings, JsonObject? inherited, Action<Action, bool> edit)
    {
        ImGui.PushID(id);
        JsonNode? value(string key) => settings[key] ?? inherited?[key];
        bool own(string key) => inherited is null || settings.ContainsKey(key);
        void set(string key, JsonNode? node, bool continues) => edit(() => settings[key] = node, continues);
        void label(string key, Action control)
        {
            var overridden = inherited is not null && settings.ContainsKey(key);
            if (overridden) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .7f, .35f, 1));
            control();
            if (overridden)
            {
                ImGui.PopStyleColor();
                ImGui.SameLine();
                if (ImGui.SmallButton($"x##{key}")) edit(() => settings.Remove(key), false);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back to the style's");
            }
        }
        float number(string key, float otherwise) => TextStyleJson.Number(value(key)) is { } n ? (float)n : otherwise;
        string text(string key, string otherwise) => value(key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : otherwise;
        void numberControl(string key, string name, float otherwise, float speed, float min, float max, string format = "%.1f") => label(key, () =>
        {
            var current = number(key, otherwise);
            if (ImGui.DragFloat(name, ref current, speed, min, max, format))
                set(key, Math.Round(current, 3), styleContinues());
        });
        void choice(string key, string name, string otherwise, string[] options) => label(key, () =>
        {
            var current = text(key, otherwise);
            if (ImGui.BeginCombo(name, current))
            {
                foreach (var option in options)
                    if (ImGui.Selectable(option, option == current))
                        set(key, option, false);
                ImGui.EndCombo();
            }
        });

        string[] fonts = [SkinText.GameFont, .. (_textStyles?.Json["fonts"] as JsonObject ?? []).Select(static font => font.Key)];
        choice("font", "Font", SkinText.GameFont, fonts);
        numberControl("size", "Size", 24, .2f, 1, 512);
        numberControl("spacing", "Letter spacing", 0, .1f, -64, 64);
        numberControl("lineHeight", "Line height (0: the font's)", 0, .01f, 0, 4, "%.2f");
        numberControl("slant", "Slant", 0, .005f, -1, 1, "%.3f");
        choice("align", "Align", "left", ["left", "center", "right"]);
        choice("valign", "Vertical align", "center", ["top", "center", "bottom"]);
        choice("fit", "Too long", "shrink", ["shrink", "squeeze", "wrap", "clip"]);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("shrink: the whole text gets smaller. squeeze: narrower along the line only (as the game's titles).\nwrap: onto more lines, then smaller. clip: runs out of its box.");
        label("vertical", () =>
        {
            var vertical = value("vertical")?.GetValueKind() == JsonValueKind.True;
            if (ImGui.Checkbox("Vertical (top to bottom)", ref vertical))
                set("vertical", vertical, false);
        });

        // The look: layers, back to front.
        ImGui.SeparatorText("Look (back to front)");
        if (inherited is not null && !settings.ContainsKey("layers"))
        {
            label("color", () =>
            {
                var fill = colours(value("color") ?? (inherited["layers"] as JsonArray)?.LastOrDefault()?["color"]);
                if (ImGui.ColorEdit4("Fill colour", ref fill.Top, ImGuiColorEditFlags.AlphaBar))
                    set("color", hex(fill.Top), styleContinues());
            });
            ImGui.TextDisabled($"{(inherited["layers"] as JsonArray)?.Count ?? 1} layer(s) from the style.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Make them this field's own"))
                edit(() => settings["layers"] = inherited["layers"]?.DeepClone() ?? new JsonArray(new JsonObject { ["color"] = "#ffffff" }), false);
            ImGui.PopID();
            return;
        }
        if (inherited is not null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, .7f, .35f, 1));
            ImGui.TextUnformatted("This field's own layers");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.SmallButton("x##layers")) edit(() => { settings.Remove("layers"); settings.Remove("color"); }, false);
        }
        var layers = settings["layers"] as JsonArray;
        if (layers is null)
        {
            if (ImGui.Button("Add layers")) edit(() => settings["layers"] = new JsonArray(new JsonObject { ["color"] = "#ffffff" }), false);
            ImGui.PopID();
            return;
        }
        var reach = layers.OfType<JsonObject>().Select(static layer => TextStyleJson.Number(layer["grow"]) ?? 0).DefaultIfEmpty(0).Max();
        if (ImGui.Button("+ Border"))
            edit(() => layers.Insert(0, new JsonObject { ["grow"] = reach + 4, ["color"] = "#000000" }), false);
        ImGui.SameLine();
        if (ImGui.Button("+ Shadow"))
            edit(() => layers.Insert(0, new JsonObject { ["grow"] = reach, ["offset"] = new JsonArray(3, 4), ["blur"] = 4, ["color"] = "#00000090" }), false);
        ImGui.SameLine();
        if (ImGui.Button("+ Fill"))
            edit(() => layers.Add(new JsonObject { ["color"] = "#ffffff" }), false);
        for (var index = 0; index < layers.Count; index++)
        {
            if (layers[index] is not JsonObject layer)
                continue;
            ImGui.PushID(index);
            var grow = (float)(TextStyleJson.Number(layer["grow"]) ?? 0);
            var kind = index == layers.Count - 1 ? "front" : index == 0 ? "back" : "";
            var title = (grow > 0 ? $"Border {grow:0.#}" : "Fill") + (layer["offset"] is not null || layer["blur"] is not null ? " (shadow)" : "");
            var open = ImGui.TreeNodeEx($"{index + 1}. {title} {kind}###layer", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);
            ImGui.SameLine();
            ImGui.BeginDisabled(index == 0);
            if (ImGui.SmallButton("^")) edit(() => { layers.RemoveAt(index); layers.Insert(index - 1, layer); }, false);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == layers.Count - 1);
            if (ImGui.SmallButton("v")) edit(() => { layers.RemoveAt(index); layers.Insert(index + 1, layer); }, false);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(layers.Count == 1);
            if (ImGui.SmallButton("x")) edit(() => layers.RemoveAt(index), false);
            ImGui.EndDisabled();
            if (open)
            {
                if (ImGui.DragFloat("Grow", ref grow, .1f, 0, 128, "%.1f"))
                    edit(() => layer["grow"] = Math.Round(grow, 2), styleContinues());
                var offset = layer["offset"] is JsonArray { Count: 2 } o
                    ? new Vector2((float)(TextStyleJson.Number(o[0]) ?? 0), (float)(TextStyleJson.Number(o[1]) ?? 0)) : Vector2.Zero;
                if (ImGui.DragFloat2("Offset", ref offset, .1f, -128, 128, "%.1f"))
                    edit(() => layer["offset"] = offset == Vector2.Zero ? null : new JsonArray(Math.Round(offset.X, 2), Math.Round(offset.Y, 2)), styleContinues());
                var blur = (float)(TextStyleJson.Number(layer["blur"]) ?? 0);
                if (ImGui.DragFloat("Blur", ref blur, .1f, 0, 128, "%.1f"))
                    edit(() => layer["blur"] = blur > 0 ? Math.Round(blur, 2) : null, styleContinues());
                var (top, bottom, gradient) = colours(layer["color"]);
                if (ImGui.ColorEdit4(gradient ? "Top" : "Colour", ref top, ImGuiColorEditFlags.AlphaBar))
                    edit(() => layer["color"] = gradient ? new JsonArray(hex(top), hex(bottom)) : hex(top), styleContinues());
                if (ImGui.Checkbox("Gradient", ref gradient))
                    edit(() => layer["color"] = gradient ? new JsonArray(hex(top), hex(bottom)) : hex(top), false);
                if (gradient)
                {
                    if (ImGui.ColorEdit4("Bottom", ref bottom, ImGuiColorEditFlags.AlphaBar))
                        edit(() => layer["color"] = new JsonArray(hex(top), hex(bottom)), styleContinues());
                    // Its direction and where it changes: { angle, from, to, over } (left out while all default).
                    var settingsOf = layer["gradient"] as JsonObject;
                    var angle = (float)(TextStyleJson.Number(settingsOf?["angle"]) ?? 0);
                    var from = (float)(TextStyleJson.Number(settingsOf?["from"]) ?? 0);
                    var to = (float)(TextStyleJson.Number(settingsOf?["to"]) ?? 1);
                    var overText = (string?)settingsOf?["over"] == "text";
                    void gradientSet(Action<JsonObject> change, bool continues) => edit(() =>
                    {
                        var made = layer["gradient"] as JsonObject ?? new JsonObject();
                        change(made);
                        layer["gradient"] = made.Count > 0 ? made : null;
                        if (made.Count == 0) layer.Remove("gradient");
                    }, continues);
                    if (ImGui.SliderFloat("Angle", ref angle, -180, 180, "%.0f deg"))
                        gradientSet(g => { if (angle == 0) g.Remove("angle"); else g["angle"] = MathF.Round(angle); }, styleContinues());
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("0: top to bottom. 90: left to right. Ctrl+click to type.");
                    if (ImGui.DragFloatRange2("From / to", ref from, ref to, .005f, 0, 1, "%.2f", "%.2f"))
                        gradientSet(g =>
                        {
                            if (from == 0) g.Remove("from"); else g["from"] = Math.Round(from, 3);
                            if (to == 1) g.Remove("to"); else g["to"] = Math.Round(to, 3);
                        }, styleContinues());
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Where the top colour ends and the bottom one begins, across the line (0 to 1).\nThe same for both: a hard edge.");
                    if (ImGui.Checkbox("Over the whole text (not each line)", ref overText))
                        gradientSet(g => { if (overText) g["over"] = "text"; else g.Remove("over"); }, false);
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        ImGui.PopID();
    }

    private static (Vector4 Top, Vector4 Bottom, bool Gradient) colours(JsonNode? node)
    {
        static Vector4 one(string? text)
        {
            try
            {
                var rgba = TextStyleJson.Colour(text ?? "#ffffff", "color");
                return new((rgba >> 24) / 255f, (rgba >> 16 & 255) / 255f, (rgba >> 8 & 255) / 255f, (rgba & 255) / 255f);
            }
            catch (FormatException)
            {
                return Vector4.One;
            }
        }
        return node switch
        {
            JsonArray { Count: 2 } pair => (one((string?)pair[0]), one((string?)pair[1]), true),
            JsonValue value when value.TryGetValue<string>(out var text) => (one(text), one(text), false),
            _ => (Vector4.One, Vector4.One, false),
        };
    }

    private static string hex(Vector4 colour)
    {
        static int b(float v) => (int)MathF.Round(Math.Clamp(v, 0, 1) * 255);
        return b(colour.W) == 255 ? $"#{b(colour.X):x2}{b(colour.Y):x2}{b(colour.Z):x2}" : $"#{b(colour.X):x2}{b(colour.Y):x2}{b(colour.Z):x2}{b(colour.W):x2}";
    }

    /// <summary>A text in a box as the game draws it (with these field settings), over a checkerboard, at one pixel a unit.</summary>
    private void textPreview(string text, string settings, Vector2 box, float size, char align, uint rgb, float scale = 1)
    {
        var id = LumenTextSurface.SettingsId(settings);
        var margin = LumenTextSurface.Margin?.Invoke(id) ?? 0;
        var origin = ImGui.GetCursorScreenPos();
        var whole = (box + new Vector2(margin * 2)) * scale;
        var draw = ImGui.GetWindowDrawList();
        for (var y = 0f; y < whole.Y; y += 12)
            for (var x = 0f; x < whole.X; x += 12)
                draw.AddRectFilled(origin + new Vector2(x, y), origin + Vector2.Min(new(x + 12, y + 12), whole),
                    ((int)(x / 12) + (int)(y / 12)) % 2 == 0 ? color(.24f, .25f, .27f) : color(.17f, .18f, .2f));
        draw.AddRect(origin + new Vector2(margin * scale), origin + new Vector2(margin * scale) + box * scale, color(.45f, .5f, .56f, .6f));
        if (text.Length > 0 && TextTexture?.Invoke(LumenTextSurface.Key(text, box.X, box.Y, size, align, rgb, id)) is { } texture)
            draw.AddImage((nint)texture.Value, origin, origin + whole);
        ImGui.Dummy(whole);
    }

    // The text field section of the asset inspector: what it shows, in which style, from which Wd name, and its own look.
    private void textFieldSettings(JsonObject entry)
    {
        if (!ImGui.IsAnyItemActive())
            _styleGesture = 0;
        var own = (JsonObject)entry.DeepClone();
        own.Remove("rect");
        var rect = entry["rect"] as JsonArray;
        var box = rect is { Count: 4 } ? new Vector2(assetNumber(rect[2]), assetNumber(rect[3])) : new Vector2(200, 48);
        var shown = (string?)entry["text"] is { Length: > 0 } initial ? initial
            : (string?)entry["bind"] is { } bound && WdNames.Find(bound) is { Sample.Length: > 0 } sampled ? sampled.Sample : "Text";
        var margin = LumenTextSurface.Margin?.Invoke(LumenTextSurface.SettingsId(own.ToJsonString())) ?? 0;
        var preview = Math.Min(1, Math.Max(40, ImGui.GetContentRegionAvail().X - 4) / Math.Max(1, box.X + margin * 2));
        ImGui.TextDisabled(preview < 1 ? $"Preview ({preview * 100:0}%%)" : "Preview");
        // Rendered at the box's own size (so it fits as in the game), shown smaller when the panel is narrower.
        textPreview(shown, own.ToJsonString(), box, assetNumber(entry["size"] ?? 24), alignOf(entry), 0xffffff, preview);

        var text = (string?)entry["text"] ?? "";
        if (ImGui.InputTextMultiline("Text", ref text, 2000, new Vector2(0, ImGui.GetTextLineHeight() * 3)))
            edited(() => _document!.Edit(_ => { if (text.Length > 0) entry["text"] = text; else entry.Remove("text"); }, styleContinues()));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shown in the editor and the game until a script or the engine (Bind) gives this field a text.\nTry long scores, Japanese and Latin, to see how it fits.");

        var style = (string?)entry["style"];
        var styles = SkinText.Current.Styles;
        if (ImGui.BeginCombo("Style", style ?? "(its own settings)"))
        {
            if (ImGui.Selectable("(its own settings)", style is null))
                edited(() => _document!.Edit(_ => entry.Remove("style")));
            foreach (var (name, _) in styles)
                if (ImGui.Selectable(name, name == style))
                    edited(() => _document!.Edit(_ =>
                    {
                        entry["style"] = name;
                        // The style's look, not the field's old one: its own settings go (set them again over the style).
                        foreach (var key in LookKeys)
                            entry.Remove(key);
                    }));
            ImGui.EndCombo();
        }
        if (style is not null && !styles.ContainsKey(style))
            ImGui.TextColored(new Vector4(1, .45f, .35f, 1), $"No style \"{style}\" in text.json.");

        var bind = (string?)entry["bind"];
        if (ImGui.BeginCombo("Bind", bind ?? "(none: a script sets it)", ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##bind search", "Search Wd names", ref _bindSearch, 64);
            if (ImGui.Selectable("(none: a script sets it)", bind is null))
                edited(() => _document!.Edit(_ => entry.Remove("bind")));
            foreach (var name in WdNames.All.Where(name => name.Kind == WdKind.State
                         && (_bindSearch.Length == 0 || name.Name.Contains(_bindSearch, StringComparison.OrdinalIgnoreCase))))
            {
                if (ImGui.Selectable(name.Name, name.Name == bind))
                    edited(() => _document!.Edit(_ => entry["bind"] = name.Name));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{name.Type}: {name.Description}" + (name.Sample.Length > 0 ? $"\ne.g. {name.Sample}" : ""));
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered() && bind is not null && WdNames.Find(bind) is { } described)
            ImGui.SetTooltip(described.Description);

        // Its own settings over the style's (the field's movie defaults under it).
        var inherited = new JsonObject { ["size"] = 24, ["align"] = "left", ["layers"] = new JsonArray(new JsonObject { ["color"] = "#000000" }) };
        if (style is not null && styles.TryGetValue(style, out var named))
            inherited = TextStyleJson.Merge(inherited, named);
        styleSettings("field", entry, inherited, (change, continues) => edited(() => _document!.Edit(_ => change(), continues)));
    }

    private static char alignOf(JsonObject entry) => (string?)entry["align"] switch { "right" => 'r', "center" => 'c', _ => 'l' };
}
