using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Editor;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.SongSelect;
using Waddamburo.Providers.Tja;

namespace Waddamburo.App.Flow;

/// <summary>
/// The effects editor's hold on a song in practice: opening a TJA from anywhere (an outside editor's file, reloaded
/// on each of its saves) puts its chart under the running practice, unranked.
/// </summary>
internal sealed partial class GameplayFlow
{
    // The provider for the TJA the editor opened, kept while files come from the same folder.
    private TjaCatalogProvider? _editorProvider;
    private string EditorModchartFolder => Shell.Modcharts ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waddamburo", "modcharts");

    public string? EditorTjaPath() => _lastRequest?.Players[0] is { } player ? Shell.Assets.ChartPath(player.ChartAsset) : null;

    public EffectEditorSource LoadEditorTja(string path, TaikoCourse? requestedCourse)
    {
        if (!CanEditEffects || _lastRequest is not { } oldRequest || _training is null)
            throw new InvalidOperationException("Open the editor during a local single-player song.");
        path = Path.GetFullPath(path);
        var sameFile = string.Equals(path, EditorTjaPath(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var (song, descriptor, chart, courses) = openEditorTja(path, requestedCourse ?? oldRequest.Players[0].Course, requestedCourse is not null);
        var request = new PlayRequest(oldRequest.CatalogRevision, song.Key, song.AudioAsset,
            [new PlayerChartRequest(oldRequest.Players[0].Player, descriptor.Key, descriptor.ChartAsset, descriptor.Course!.Value)]);
        switchPracticeChart(request, chart, new(song, courses.Aggregate(SongCourseBits.None, (bits, c) => bits | (SongCourseBits)(1 << (int)c)),
            [.. Enum.GetValues<TaikoCourse>().Select(c => song.Charts.FirstOrDefault(d => d.Course == c)?.Level)]), keepLoop: sameFile);
        Console.WriteLine("Effects editor: loaded a TJA directly; chart changes remain unranked.");
        var course = descriptor.Course!.Value;
        return new(chart, _training!, new(PlayerModcharts.PathFor(EditorModchartFolder, chart, course), course,
            Path.ChangeExtension(path, ChartModFile.Extension)), path, courses);
    }

    // The song in one TJA file (no library scan) and the course to play: the one asked for, else the one playing when the
    // file has it, else Oni, else its first. Its provider resolves its audio and chart through the router's editor slot.
    private (SongDescriptor Song, SongChartDescriptor Chart, PlayableChart Playable, TaikoCourse[] Courses) openEditorTja(
        string path, TaikoCourse preferred, bool required)
    {
        var root = Path.GetDirectoryName(path)!;
        if (_editorProvider?.Root != root)
            _editorProvider = new TjaCatalogProvider(root, new("effects-editor-" + Guid.NewGuid().ToString("N")));
        var provider = _editorProvider;
        var contribution = provider.InspectFileAsync(path).AsTask().GetAwaiter().GetResult();
        var song = contribution.Songs.SingleOrDefault() ?? throw new InvalidDataException("The TJA has no supported course or is incomplete. "
            + string.Join(" ", contribution.Diagnostics.Select(d => d.Message)));
        var courses = song.Charts.Where(c => c.Course.HasValue).Select(c => c.Course!.Value).Distinct().Order().ToArray();
        var course = required || courses.Contains(preferred) ? preferred : courses.Contains(TaikoCourse.Oni) ? TaikoCourse.Oni : courses[0];
        var descriptor = song.Charts.FirstOrDefault(c => c.Course == course)
            ?? throw new InvalidDataException($"The TJA no longer contains {course}. Choose another course or open the file again.");
        var chart = provider.LoadChartAsync(descriptor.Key, descriptor.ChartAsset).AsTask().GetAwaiter().GetResult();
        Shell.Assets.UseEditorSource(provider, provider);
        return (song, descriptor, chart, courses);
    }

    // The running practice goes on with another chart: the same state a song start sets up for it (request, charts,
    // timeline, review, title, training), keeping the playhead, pause and speed, and the loop when it is the same file.
    private void switchPracticeChart(PlayRequest request, PlayableChart chart, SongSelectSong song, bool keepLoop)
    {
        var previous = _training!;
        var at = previous.Clock.Position;
        var (paused, speed) = (previous.Clock.Paused, previous.Clock.Speed);
        var sameAudio = _lastRequest?.AudioAsset == request.AudioAsset;
        _lastRequest = request;
        _sourceCharts = _charts = [chart];
        _song = song;
        _timeline = new GameplayTimeline(chart.AuthoredOffset, TimeSpan.FromSeconds(3));
        var end = chartEnd() + TimeSpan.FromSeconds(2);
        at = at < -_timeline.LeadIn ? -_timeline.LeadIn : at > end ? end : at;
        if (sameAudio && _review is { } review)
            review.ReloadChart(chart, request.Players[0].Course, _timeline.LeadIn, _timeline.AudioStart - _timeline.LeadIn - _audioOffset);
        else
        {
            _review?.Dispose();
            _review = reviewOf(request, [[]], end, _inputOffset);
        }
        showSongTitle(song);
        Shell.Gameplay.Options = [default];
        _training = new TrainingSession(Shell, _review!, chart);
        _training.Begin(at);
        _training.Clock.SetPaused(paused);
        if (keepLoop) _training.RestoreLoop(previous);
        while (_training.Clock.Speed < speed) _training.Clock.Faster();
        while (_training.Clock.Speed > speed) _training.Clock.Slower();
        _voided = true; Playing = false; PlayingChart = null;
        showMode("Effects editor - unranked");
    }
}
