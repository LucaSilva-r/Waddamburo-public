using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;

namespace Waddamburo.App.Flow;

/// <summary>
/// The boot screens and the attract loop (traced): kidou once, then logo_namco -> title -> keikoku ->
/// one attract CM -> logo_namco ... A drum hit (free play) or a coin starts the entry.
/// </summary>
internal sealed class AttractFlow(GameShell shell, string[] movies) : FlowScene(shell), IDisposable
{
    // movie.lm's full-screen video slot; every shipped CM is 1280x720, so its other sizes stay empty.
    private const string MovieFill = "movie1280x720";

    // Shuffled like the cabinet's rotation; refilled when every CM has played once.
    private readonly Queue<string> _queue = new();
    private string? _last;

    /// <summary>The attract CM playing in movie.lm, if any.</summary>
    public AttractMovie? Movie { get; private set; }

    public override IndicatorScene IndicatorsFor(SceneId scene) =>
        scene == FlowScenes.Boot ? IndicatorScene.Boot
        : scene == FlowScenes.Logo ? IndicatorScene.Attract
        : IndicatorScene.AttractPrompt;

    public override void Enter(SceneId scene)
    {
        if (scene == FlowScenes.Movie)
            startMovie();
    }

    public override void Exit(SceneId scene)
    {
        if (scene == FlowScenes.Title)
            Shell.Sounds?.Attract.StopAttractStream();
        Movie?.Dispose();
        Movie = null;
    }

    public override void Tick(FlowInput input)
    {
        if (Shell.Active.Id == FlowScenes.Boot)
        {
            // Space skips the boot screens (testing convenience, not cabinet behaviour).
            if (input.Skip || AttractHostBinding.IsBootEnd(Shell.Active.Player.Layers.Single().Player))
                Shell.Show(FlowScenes.Logo);
            return;
        }
        Movie?.Advance();
        // Free play: a drum hit starts. Coin mode: any credit does, at once (traced: entry loads right
        // after the first coin, even short of a full credit; coins inserted during the boot screens
        // start it as soon as the attract is reached). The drum does nothing there.
        var coinStart = Shell.Coins is { Credits: > 0 };
        // A card (paired from the website) starts the entry like a coin, in either mode (traced
        // session13-card): SCENE_TRIGGER_CARD = 4, nobody joined yet; the entry asks which drum takes it.
        if (Shell.TakeCard() is { } card)
        {
            Shell.Sounds?.StopAll();
            Shell.Hosts.EntryTrigger = 4;
            Shell.ResetPlayers();
            Shell.Hosts.EntryCard = card;
            Shell.Show(FlowScenes.Entry);
        }
        else if (input.DrumSide is not null && Shell.Coins is null || coinStart)
        {
            // The attract's voices, effects and music end with it (the coin channel plays on).
            Shell.Sounds?.StopAll();
            if (!coinStart)
                Shell.Sounds?.Attract.PlayExit();
            // SCENE_TRIGGER_COIN (3), or _DON_1P (0) / _DON_2P (1) for the drum hit: the entry then
            // joins that drum's player (the right one plays as P2).
            Shell.Hosts.EntryTrigger = coinStart ? 3 : input.DrumSide ?? 0;
            Shell.ResetPlayers();
            if (!coinStart)
                Shell.JoinPlayer(input.DrumSide ?? 0);
            Shell.Show(FlowScenes.Entry);
        }
        else if (Shell.Active.Id == FlowScenes.Movie)
        {
            if (Movie?.Finished != false)
                Shell.Show(FlowScenes.Logo);
        }
        else if (Shell.Hosts.Attract?.Finished == true)
        {
            var current = Shell.Active.Id;
            // Traced: keikoku is followed by one attract CM, then logo_namco again.
            Shell.Show(current == FlowScenes.Logo ? FlowScenes.Title
                : current == FlowScenes.Title ? FlowScenes.Caution
                : movies.Length != 0 ? FlowScenes.Movie : FlowScenes.Logo);
        }
    }

    private void startMovie()
    {
        if (_queue.Count == 0)
        {
            var order = movies.ToArray();
            Random.Shared.Shuffle(order);
            // No CM twice in a row across refills.
            if (order.Length > 1 && order[0] == _last)
                (order[0], order[^1]) = (order[^1], order[0]);
            foreach (var movie in order)
                _queue.Enqueue(movie);
        }
        var path = _last = _queue.Dequeue();
        try
        {
            Movie = new AttractMovie(Shell.Application, Shell.Audio, path);
            Shell.Active.Player.Layers.Single().Player.SetNativeFill(MovieFill, AttractMovie.Surface);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Attract movie {Path.GetFileName(path)} is unavailable: {exception.Message}");
        }
    }

    public void Dispose() => Movie?.Dispose();
}
