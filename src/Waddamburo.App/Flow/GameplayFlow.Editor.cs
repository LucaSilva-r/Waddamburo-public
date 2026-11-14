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

    public string? EditorTjaPath() => _play.LastRequest?.Players[0] is { } player ? Shell.Assets.ChartPath(player.ChartAsset) : null;

    public EffectEditorSource LoadEditorTja(string path, TaikoCourse? requestedCourse)
    {
        if (!CanEditEffects || _play.LastRequest is not { } oldRequest || _play.Training is null)
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
        return new(chart, _play.Training!, new(PlayerModcharts.PathFor(EditorModchartFolder, chart, course), course,
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
        var previous = _play.Training!;
        var at = previous.Clock.Position;
        var (paused, speed) = (previous.Clock.Paused, previous.Clock.Speed);
        var sameAudio = _play.LastRequest?.AudioAsset == request.AudioAsset;
        _play.LastRequest = request;
        _sourceCharts = _play.Charts = [chart];
        _song = song;
        _play.Timeline = new GameplayTimeline(chart.AuthoredOffset, TimeSpan.FromSeconds(3));
        var end = chartEnd() + TimeSpan.FromSeconds(2);
        at = at < -_play.Timeline.LeadIn ? -_play.Timeline.LeadIn : at > end ? end : at;
        if (sameAudio && _play.Review is { } review)
            review.ReloadChart(chart, request.Players[0].Course, _play.Timeline.LeadIn, _play.Timeline.AudioStart - _play.Timeline.LeadIn - _play.AudioOffset);
        else
        {
            _play.Review?.Dispose();
            _play.Review = reviewOf(request, [[]], end, _play.InputOffset);
        }
        showSongTitle(song);
        Shell.Gameplay.Options = [default];
        _play.Training = new TrainingSession(Shell, _play.Review!, chart);
        _play.Training.Begin(at);
        _play.Training.Clock.SetPaused(paused);
        if (keepLoop) _play.Training.RestoreLoop(previous);
        while (_play.Training.Clock.Speed < speed) _play.Training.Clock.Faster();
        while (_play.Training.Clock.Speed > speed) _play.Training.Clock.Slower();
        _play.Voided = true; Playing = false; PlayingChart = null;
        showMode("Effects editor - unranked");
    }
}
