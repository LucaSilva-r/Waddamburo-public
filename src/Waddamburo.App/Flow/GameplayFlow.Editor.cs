using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Editor;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.SongSelect;
using Waddamburo.Providers.Tja;

namespace Waddamburo.App.Flow;

internal sealed partial class GameplayFlow
{
    private readonly Dictionary<string, TjaCatalogProvider> _editorProviders = new(StringComparer.Ordinal);
    private string EditorModchartFolder => Shell.Modcharts ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waddamburo", "modcharts");

    public string? EditorTjaPath() => _lastRequest?.Players[0] is { } player ? Shell.Assets.ChartPath(player.ChartAsset) : null;

    public EffectEditorSource LoadEditorTja(string path, TaikoCourse? requestedCourse)
    {
        if (!CanEditEffects || _lastRequest is not { } oldRequest || _training is null)
            throw new InvalidOperationException("Open the editor during a local single-player song.");
        path = Path.GetFullPath(path);
        var previousTraining = _training;
        var sameFile = string.Equals(path, EditorTjaPath(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var root = Path.GetDirectoryName(path)!;
        if (!_editorProviders.TryGetValue(root, out var provider))
            _editorProviders.Add(root, provider = new TjaCatalogProvider(root, new("effects-editor-" + Guid.NewGuid().ToString("N"))));
        var contribution = provider.InspectFileAsync(path).AsTask().GetAwaiter().GetResult();
        var song = contribution.Songs.SingleOrDefault() ?? throw new InvalidDataException("The TJA has no supported course or is incomplete. "
            + string.Join(" ", contribution.Diagnostics.Select(d => d.Message)));
        var courses = song.Charts.Where(c => c.Course.HasValue).Select(c => c.Course!.Value).Distinct().Order().ToArray();
        var course = requestedCourse ?? (courses.Contains(oldRequest.Players[0].Course) ? oldRequest.Players[0].Course
            : courses.Contains(TaikoCourse.Oni) ? TaikoCourse.Oni : courses[0]);
        var descriptor = song.Charts.FirstOrDefault(c => c.Course == course)
            ?? throw new InvalidDataException($"The TJA no longer contains {course}. Choose another course or open the file again.");
        var chart = provider.LoadChartAsync(descriptor.Key, descriptor.ChartAsset).AsTask().GetAwaiter().GetResult();
        Shell.Assets.Register(provider, provider);
        var request = new PlayRequest(oldRequest.CatalogRevision, song.Key, song.AudioAsset,
            [new PlayerChartRequest(oldRequest.Players[0].Player, descriptor.Key, descriptor.ChartAsset, course)]);
        var timeline = new GameplayTimeline(chart.AuthoredOffset, TimeSpan.FromSeconds(3));
        var at = _training.Clock.Position;
        at = at < -timeline.LeadIn ? -timeline.LeadIn : at > chart.Duration + TimeSpan.FromSeconds(2) ? chart.Duration + TimeSpan.FromSeconds(2) : at;
        var paused = _training.Clock.Paused; var speed = _training.Clock.Speed;
        if (_review is { } review && oldRequest.AudioAsset == request.AudioAsset)
            review.ReloadChart(chart, course, timeline.LeadIn, chart.AuthoredOffset - _audioOffset);
        else
        {
            PlayableChart[] charts = [chart];
            var replacement = new ReviewSession(Shell, request, charts, _side, null, [[]], timeline.LeadIn,
                chart.Duration + TimeSpan.FromSeconds(2), _inputOffset, chart.AuthoredOffset - _audioOffset);
            _review?.Dispose(); _review = replacement; _charts = charts;
        }
        _lastRequest = request; _sourceCharts = [chart]; _timeline = timeline;
        _song = new(song, courses.Aggregate(SongCourseBits.None, (bits, c) => bits | (SongCourseBits)(1 << (int)c)),
            [.. Enum.GetValues<TaikoCourse>().Select(c => song.Charts.FirstOrDefault(d => d.Course == c)?.Level)]);
        var songInfo = Shell.Active.Layers.ToList().FindIndex(layer => Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == "song_info");
        if (songInfo >= 0)
        {
            var title = Shell.Titles.GetGameplayTitle(_song);
            _ = Shell.Titles.Resolve(title);
            Shell.Active.Player.Layers[songInfo].Player.SetNativeFill("song_name", title);
        }
        Shell.Gameplay.Options = [default];
        _training = new TrainingSession(Shell, _review!, chart);
        _training.Begin(at); _training.Clock.SetPaused(paused);
        if (sameFile) _training.RestoreLoop(previousTraining);
        while (_training.Clock.Speed < speed) _training.Clock.Faster();
        while (_training.Clock.Speed > speed) _training.Clock.Slower();
        _voided = true; Playing = false; PlayingChart = null;
        Shell.Gameplay.EndBanner = false;
        showMode("Effects editor - unranked");
        Console.WriteLine("Effects editor: loaded a TJA directly; chart changes remain unranked.");
        return new(chart, _training, new(PlayerModcharts.PathFor(EditorModchartFolder, chart, course), course, Path.ChangeExtension(path, ChartModFile.Extension)), path, courses);
    }
}
