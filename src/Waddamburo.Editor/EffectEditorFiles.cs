using Waddamburo.Catalog;

namespace Waddamburo.Editor;

/// <summary>Explicit save destinations supplied by the app, independent of the editor's preview chart options.</summary>
public sealed class EffectEditorFiles(string playerPath, TaikoCourse course, string? songPath = null)
{
    public string PlayerPath { get; } = playerPath;
    public string? SongPath { get; } = songPath;
    public TaikoCourse Course { get; } = course;
    /// <summary>
    /// The file that plays, so the one opened and saved: the player's own (by chart hash) when it exists, as gameplay
    /// applies it over the chart's; else the song file beside a TJA; else a new player file.
    /// </summary>
    public string PrimaryPath => SongPath is null || File.Exists(PlayerPath) ? PlayerPath : SongPath;

    public EffectDocument? Load()
    {
        var path = File.Exists(PrimaryPath) ? PrimaryPath : null;
        if (path is null) return null;
        if (new FileInfo(path).Length > ChartModFile.MaxBytes) throw new InvalidDataException("The modchart file is too large.");
        return EffectDocument.FromTracks(ChartModFile.ReadTracks(File.ReadAllBytes(path), Course));
    }

    public void Save(EffectDocument document)
    {
        if (PrimaryPath == PlayerPath) SavePlayer(document); else SaveSong(document);
    }

    public EffectDocument Import(string path)
    {
        if (new FileInfo(path).Length > ChartModFile.MaxBytes) throw new InvalidDataException("The modchart is too large.");
        return EffectDocument.FromTracks(ChartModFile.ReadTracks(File.ReadAllBytes(path), Course));
    }

    public static void Export(string path, EffectDocument document)
    {
        if (document.UsesEmbeddedEffects) throw new InvalidOperationException("Start a keyframe modchart before exporting chart-command effects.");
        AtomicWrite(path, ChartModFile.Write(document.Tracks));
    }

    public void SavePlayer(EffectDocument document)
    {
        if (document.UsesEmbeddedEffects) throw new InvalidOperationException("Start a keyframe modchart before saving chart-command effects.");
        AtomicWrite(PlayerPath, ChartModFile.Write(document.Tracks));
    }
    public void SaveSong(EffectDocument document)
    {
        if (document.UsesEmbeddedEffects) throw new InvalidOperationException("Start a keyframe modchart before saving chart-command effects.");
        if (SongPath is not { } path) throw new InvalidOperationException("This chart has no song modchart destination.");
        if (File.Exists(path) && new FileInfo(path).Length > ChartModFile.MaxBytes) throw new InvalidDataException("The song modchart is too large.");
        AtomicWrite(path, ChartModFile.WriteCourse(File.Exists(path) ? File.ReadAllBytes(path) : [], Course, document.Tracks));
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".wddmod-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
