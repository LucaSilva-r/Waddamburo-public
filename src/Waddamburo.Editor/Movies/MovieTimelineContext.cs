using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>The explicitly opened timeline, independent of the selected layer or key.</summary>
public sealed class MovieTimelineContext
{
    public string Path { get; private set; } = "";
    public uint? CharacterId { get; private set; }
    public void Open(string path, uint? characterId = null) { Path = path; CharacterId = characterId; }
    public void Reset() => Open("");
    public void Parent() => Open(Path.LastIndexOf('/') is >= 0 and var cut ? Path[..cut] : "");
    public LumenInstanceInfo? Resolve(IReadOnlyList<LumenInstanceInfo> clips) =>
        clips.FirstOrDefault(clip => clip.Path == Path && clip.Kind == LumenInstanceKind.Sprite
            && (CharacterId is null || clip.CharacterId == CharacterId));

    public IReadOnlyList<string> Ancestors()
    {
        var paths = new List<string> { "" };
        var cursor = Path;
        while (cursor.Length > 0)
        {
            paths.Insert(1, cursor);
            cursor = cursor.LastIndexOf('/') is >= 0 and var cut ? cursor[..cut] : "";
        }
        return paths;
    }
}
