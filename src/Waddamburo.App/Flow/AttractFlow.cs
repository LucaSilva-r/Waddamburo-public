using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>
/// The boot screens and the attract loop (traced): kidou once, then logo_namco -> title -> keikoku ->
/// one attract CM -> logo_namco ... A drum hit (free play) or a coin starts the entry.
/// </summary>
internal sealed class AttractFlow(GameShell shell, GameplayFlow gameplay, string[] movies, string? opening = null) : FlowScene(shell), IDisposable
{
    // movie.lm's full-screen video slot; every shipped CM is 1280x720, so its other sizes stay empty.
    private const string MovieFill = "movie1280x720";

    // Shuffled like the cabinet's rotation; refilled when every CM has played once.
    private readonly Queue<string> _queue = new();
    private string? _last;
    // The movie scene is showing the opening (OP.pam) as the title rather than a CM.
    private bool _openingAsTitle;
    // After keikoku the loop alternates its CM and a demo play (a random song on autoplay; not traced, the
    // cabinet's demo screen was missing, #2). Without CMs every turn is a demo.
    private bool _demoTurn;

    /// <summary>The attract CM playing in movie.lm, if any.</summary>
    public AttractMovie? Movie { get; private set; }

    public override IndicatorScene IndicatorsFor(SceneId scene) =>
        scene == FlowScenes.Boot ? IndicatorScene.Boot
        : scene == FlowScenes.Logo ? IndicatorScene.Attract
        : IndicatorScene.AttractPrompt;

    public override void Enter(SceneId scene)
    {
        // A drum hit leads to Entry: it loads in the background once per loop (loading it on the hit
        // froze the attract ~0.6 s).
        if (Shell.Prefetched != FlowScenes.Entry)
            Shell.Prefetch(Shell.Catalog.GetDefinition(FlowScenes.Entry));
        if (scene == FlowScenes.Movie)
            startMovie(_openingAsTitle ? opening : null);
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
            // Any key, drum hit or controller button skips the boot screens (the copyright notice); the
            // function keys (coins, back to the attract, ...) do not. Not cabinet behaviour.
            if (input.Skip || input.DrumSide is not null
                || input.Keys.Presses.Any(static press => press.Key is < SdlKeyboardKey.F1 or > SdlKeyboardKey.F8)
                || AttractHostBinding.IsBootEnd(Shell.Active.Player.Layers.Single().Player))
                Shell.Show(FlowScenes.Logo);
            return;
        }
        Movie?.Advance();
        if (TryStart(input))
            return;
        if (Shell.Active.Id == FlowScenes.Movie)
        {
            if (Movie?.Finished != false)
            {
                var next = _openingAsTitle ? FlowScenes.Caution : FlowScenes.Logo;
                _openingAsTitle = false;
                Shell.Show(next);
            }
        }
        else if (Shell.Hosts.Attract?.Finished == true)
        {
            var current = Shell.Active.Id;
            // Releases with an opening movie play it as the title (their exe's attract table).
            if (current == FlowScenes.Logo && opening is not null)
            {
                _openingAsTitle = true;
                Shell.Show(FlowScenes.Movie);
                return;
            }
            // Traced: keikoku is followed by one attract CM, then logo_namco again.
            if (current == FlowScenes.Caution && (_demoTurn = !_demoTurn || movies.Length == 0) && startDemo())
                return;
            Shell.Show(current == FlowScenes.Logo ? FlowScenes.Title
                : current == FlowScenes.Title ? FlowScenes.Caution
                : movies.Length != 0 ? FlowScenes.Movie : FlowScenes.Logo);
        }
    }

    // A random stock (Green) song with an Oni or Hard chart, from the game's own demo list (musicinfo demoplay,
    // 114 songs in Green) when it has one; no TJA, osu! or Nijiiro picks.
    private bool startDemo()
    {
        var songs = Shell.SongCatalog.Categories
            .SelectMany(category => category.Songs.Select(song => (category.Name, Song: song)))
            .Where(static entry => entry.Song.Descriptor.Key.Source == SongSourceKind.Stock
                && (entry.Song.HasCourse(TaikoCourse.Oni) || entry.Song.HasCourse(TaikoCourse.Hard)))
            .ToArray();
        // Mostly songs with their own gameplay skin (collabs, Vocaloid...), as the cabinet seemed to favour:
        // three demos in four from those (the skin table, no archive loaded), the rest from any song.
        if (songs.Where(static entry => entry.Song.Descriptor.DemoPlay).ToArray() is { Length: > 0 } listed)
            songs = listed;
        var themed = songs.Where(entry => Shell.Skins.HasTheme(entry.Song.Descriptor, entry.Name)).ToArray();
        Console.WriteLine($"Demo picks: {themed.Length} themed of {songs.Length} songs.");
        if (themed.Length != 0 && Random.Shared.Next(4) != 0)
            songs = themed;
        for (var attempt = 0; attempt < 3 && songs.Length != 0; attempt++)
        {
            var (category, song) = songs[Random.Shared.Next(songs.Length)];
            if (gameplay.StartDemo(song, category, TryStart))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A drum hit, coin or card starts a credit (the attract's screens and its demo play); true when one did.
    /// </summary>
    public bool TryStart(FlowInput input)
    {
        // Free play: a drum hit starts. Coin mode: any credit does, at once (traced: entry loads right
        // after the first coin, even short of a full credit; coins inserted during the boot screens
        // start it as soon as the attract is reached). The drum does nothing there.
        var coinStart = Shell.Coins is { Credits: > 0 };
        // A card (paired from the website) starts the entry like a coin, in either mode (traced
        // session13-card): SCENE_TRIGGER_CARD = 4, nobody joined yet; the entry asks which drum takes it.
        // A rejected card still opens the entry, like a coin, to show the red band there.
        // ponytail: untraced; the game's reaction to an unregistered card at the attract is unknown.
        if (Shell.TakeCardFailure())
        {
            Shell.Sounds?.StopAll();
            Shell.Hosts.EntryTrigger = 3;
            Shell.ResetPlayers();
            Shell.Hosts.EntryCardRejected = true;
            Shell.Show(FlowScenes.Entry);
        }
        else if (Shell.TakeCard() is { } card)
        {
            Shell.Sounds?.StopAll();
            Shell.Hosts.EntryTrigger = 4;
            Shell.ResetPlayers();
            Shell.Hosts.EntryCard = card;
            Shell.Show(FlowScenes.Entry);
        }
        // Home: the drum opens the player setup, which starts the entry itself.
        else if (input.DrumSide is { } setupSide && Shell.OpenPlayerSetup(setupSide))
        {
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
        else
            return false;
        return true;
    }

    private void startMovie(string? path)
    {
        if (path is null && _queue.Count == 0)
        {
            var order = movies.ToArray();
            Random.Shared.Shuffle(order);
            // No CM twice in a row across refills.
            if (order.Length > 1 && order[0] == _last)
                (order[0], order[^1]) = (order[^1], order[0]);
            foreach (var movie in order)
                _queue.Enqueue(movie);
        }
        path ??= _last = _queue.Dequeue();
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
