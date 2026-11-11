using System.Collections.Immutable;
using System.Globalization;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Lumen.Runtime;

public enum LumenInstanceKind { Sprite, Shape, Text, Unknown }

/// <summary>
/// One clip of the display list as the movie inspector shows it. <see cref="Path"/> names it for the other
/// Inspect calls: its depths from the root ("16385/3"), which stay the same while it is on stage.
/// </summary>
public sealed record LumenInstanceInfo(string Path, int Level, string Name, uint CharacterId, LumenInstanceKind Kind,
    long Depth, int Frame, int FrameCount, string? Label, bool Playing, bool Visible, bool HiddenByHost,
    LumenMatrix Transform, LumenRenderColor Multiply, LumenRenderColor Add, int BlendMode, string? ClassName);

/// <summary>
/// The movie inspector's view of a running movie (docs/development/free-movies-plan.md). Unlike the host's own
/// calls these leave no trace in the edge log: looking at a movie is not part of its contract.
/// </summary>
public sealed partial class LumenPlayer
{
    /// <summary>Every clip on stage, parents before children, in drawing order.</summary>
    public IReadOnlyList<LumenInstanceInfo> InspectDisplayList()
    {
        var list = new List<LumenInstanceInfo>();
        void visit(DisplayInstance instance, string path, int level)
        {
            SpriteTimeline? timeline = _sprites.TryGetValue(instance.CharacterId, out var found) ? found : null;
            list.Add(new LumenInstanceInfo(path, level, instance.Name, instance.CharacterId,
                timeline is not null ? LumenInstanceKind.Sprite
                    : _shapes.ContainsKey(instance.CharacterId) ? LumenInstanceKind.Shape
                    : _texts.ContainsKey(instance.CharacterId) ? LumenInstanceKind.Text : LumenInstanceKind.Unknown,
                instance.Depth, instance.Frame, timeline?.Frames.Length ?? 0,
                timeline?.Labels.Where(pair => pair.Value <= instance.Frame).OrderByDescending(static pair => pair.Value)
                    .Select(static pair => pair.Key).FirstOrDefault(),
                instance.Playing, instance.Visible, instance.HiddenByHost, instance.Transform, instance.Color.Multiply,
                instance.Color.Add, instance.BlendMode, timeline?.ExportName));
            foreach (var (depth, child) in instance.Children)
                if (!child.Removed)
                    visit(child, path.Length == 0 ? depth.ToString(CultureInfo.InvariantCulture) : $"{path}/{depth}", level + 1);
        }
        visit(_root, "", 0);
        return list;
    }

    private DisplayInstance? inspected(string path)
    {
        var instance = _root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
                || !instance.Children.TryGetValue(depth, out instance) || instance.Removed)
                return null;
        return instance;
    }

    /// <summary>The frame labels of a clip's timeline, by frame.</summary>
    public IReadOnlyList<(string Label, int Frame)> InspectLabels(string path) =>
        inspected(path) is { } instance && _sprites.TryGetValue(instance.CharacterId, out var timeline)
            ? [.. timeline.Labels.OrderBy(static pair => pair.Value).Select(static pair => (pair.Key, pair.Value))]
            : [];

    /// <summary>A clip's script variables as text (objects by kind only).</summary>
    public IReadOnlyList<(string Name, string Value)> InspectVariables(string path) =>
        inspected(path) is { } instance
            ? [.. instance.Variables.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => (pair.Key, pair.Value switch
            {
                null => "null",
                Avm1Undefined => "undefined",
                string text => $"\"{text}\"",
                bool flag => flag ? "true" : "false",
                double number => number.ToString("0.####", CultureInfo.InvariantCulture),
                Avm1FunctionValue or Avm1NativeFunction => "function",
                Avm1ArrayObject => "array",
                DisplayInstance => "clip",
                _ => "object",
            }))]
            : [];

    /// <summary>What a clip and everything in it covers on stage, in movie coordinates; false when it draws nothing.</summary>
    public bool TryInspectBounds(string path, out LumenNativeSurfacePlacement bounds)
    {
        bounds = default;
        if (inspected(path) is not { } instance)
            return false;
        var transform = LumenMatrix.Identity;
        for (var parent = instance.Parent; parent is not null; parent = parent.Parent)
            transform = transform.Then(parent.Transform);
        var quads = ImmutableArray.CreateBuilder<LumenRenderQuad>();
        appendInstance(instance, transform, ColorState.Identity, LumenRenderBlend.Normal, 1, quads);
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (var quad in quads)
        {
            if (quad.MaskOperation != LumenRenderMaskOperation.Draw)
                continue;
            foreach (var vertex in (ReadOnlySpan<LumenRenderVertex>)[quad.TopLeft, quad.TopRight, quad.BottomLeft, quad.BottomRight])
            {
                left = Math.Min(left, vertex.X);
                top = Math.Min(top, vertex.Y);
                right = Math.Max(right, vertex.X);
                bottom = Math.Max(bottom, vertex.Y);
            }
        }
        if (left > right)
            return false;
        bounds = new(left, top, right - left, bottom - top);
        return true;
    }

    /// <summary>
    /// The clip under a stage point: the smallest one that draws there, so a click reaches the part and not the
    /// screen-sized clip that holds it. Null when nothing is there.
    /// ponytail: by bounding box, every clip measured on each call; fine for a click, not for every frame.
    /// </summary>
    public string? InspectPick(float x, float y)
    {
        string? best = null;
        var bestArea = float.MaxValue;
        foreach (var info in InspectDisplayList())
        {
            if (info.Level == 0 || !TryInspectBounds(info.Path, out var box)
                || x < box.X || y < box.Y || x > box.X + box.Width || y > box.Y + box.Height)
                continue;
            var area = box.Width * box.Height;
            if (area <= bestArea)
                (best, bestArea) = (info.Path, area);
        }
        return best;
    }

    /// <summary>Moves a clip's playhead (zero-based frame), as its script's gotoAndPlay / gotoAndStop would.</summary>
    public bool InspectGoto(string path, int frame, bool play)
    {
        if (inspected(path) is not { } instance || !_sprites.TryGetValue(instance.CharacterId, out var timeline)
            || frame < 0 || frame >= timeline.Frames.Length)
            return false;
        jumpInstance(instance, frame, play);
        drainActions();
        resetInterpolation(instance);
        return true;
    }

    /// <summary>Plays or stops a clip's timeline where it is.</summary>
    public bool InspectSetPlaying(string path, bool playing)
    {
        if (inspected(path) is not { } instance)
            return false;
        instance.Playing = playing;
        return true;
    }

    /// <summary>Hides a clip from drawing whatever its script does (the host's own switch), or shows it again.</summary>
    public bool InspectSetHidden(string path, bool hidden)
    {
        if (inspected(path) is not { } instance)
            return false;
        instance.HiddenByHost = hidden;
        return true;
    }
}
