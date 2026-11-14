using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Skins.Green;
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
        // The caution screen before a demo turn: the demo is picked now and its lane loads in the background
        // instead (starting it cold froze the attract for a moment); the demo prefetches Entry again once up.
        // Its skin is checked and its scene composed on a worker (a skin's first check read the whole archive:
        // 540 ms on a Celeron), and prefetched once ready (Tick).
        var demoTurn = !_demoTurn || movies.Length == 0;
        if (scene == FlowScenes.Caution && demoTurn && pickDemo() is { } demo)
        {
            _nextDemo = demo;
            _demoPrefetched = false;
        }
        else
        {
            // A CM turn: the CM opens on a worker meanwhile (opening it at its start froze ~0.9 s on a Celeron);
            // it needs no scene prefetch, so Entry keeps that.
            if (scene == FlowScenes.Caution && !demoTurn)
                prepareMovie();
            if (Shell.Scenes.Prefetched != FlowScenes.Entry)
                Shell.Scenes.Prefetch(Shell.Catalog.GetDefinition(FlowScenes.Entry));
        }
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
        prefetchDemo();
        if (Shell.Active.Id == FlowScenes.Boot)
        {
            // Home: any key, drum hit or controller button skips the boot screens (the copyright notice); the
            // function keys (coins, back to the attract, ...) do not. Not cabinet behaviour: a cabinet waits for them.
            if (Shell.Arcade.Home && (input.Skip || input.DrumSide is not null
                || input.Keys.Presses.Any(static press => press.Key is < SdlKeyboardKey.F1 or > SdlKeyboardKey.F8))
                || AttractHostBinding.IsBootEnd(Shell.Active.Player.Layers.Single().Player))
                Shell.Scenes.Show(FlowScenes.Logo);
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
                Shell.Scenes.Show(next);
            }
        }
        else if (Shell.Hosts.Attract?.Finished == true)
        {
            var current = Shell.Active.Id;
            // Releases with an opening movie play it as the title (their exe's attract table).
            if (current == FlowScenes.Logo && opening is not null)
            {
                _openingAsTitle = true;
                Shell.Scenes.Show(FlowScenes.Movie);
                return;
            }
            // Traced: keikoku is followed by one attract CM, then logo_namco again.
            if (current == FlowScenes.Caution && (_demoTurn = !_demoTurn || movies.Length == 0) && startDemo())
                return;
            Shell.Scenes.Show(current == FlowScenes.Logo ? FlowScenes.Title
                : current == FlowScenes.Title ? FlowScenes.Caution
                : movies.Length != 0 ? FlowScenes.Movie : FlowScenes.Logo);
        }
    }

    // A random stock (Green) song with a Normal chart (#19: the cabinet's demo plays Normal), from the game's own demo list (musicinfo demoplay,
    // 114 songs in Green) when it has one; no TJA, osu! or Nijiiro picks.
    private (string Category, SongSelectSong Song, Task<SceneDefinition> Scene)? _nextDemo;
    private bool _demoPrefetched;

    // The picked demo's lane starts loading in the background once its scene is composed.
    private void prefetchDemo()
    {
        if (_demoPrefetched || _nextDemo is not { Scene: { IsCompletedSuccessfully: true } scene })
            return;
        _demoPrefetched = true;
        Shell.Scenes.Prefetch(scene.Result);
    }

    private bool startDemo()
    {
        // Picked (and prefetched) as the caution screen began; picked now if that did not happen.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var demo = _nextDemo ?? pickDemo();
            _nextDemo = null;
            if (demo is not { } picked)
                return false;
            // Composed long before, unless the demo came early: then it waits for it here.
            if (gameplay.StartDemo(picked.Song, picked.Category, TryStart, picked.Scene.GetAwaiter().GetResult()))
                return true;
        }
        return false;
    }

    private (string Category, SongSelectSong Song, Task<SceneDefinition> Scene)? pickDemo()
    {
        var songs = Shell.SongCatalog.Categories
            .SelectMany(category => category.Songs.Select(song => (category.Name, Song: song)))
            .Where(static entry => entry.Song.Descriptor.Key.Source == SongSourceKind.Stock
                && entry.Song.HasCourse(TaikoCourse.Normal))
            .ToArray();
        // Mostly songs with their own gameplay skin (collabs, Vocaloid...), as the cabinet seemed to favour:
        // three demos in four from those (the skin table, no archive loaded), the rest from any song.
        if (songs.Where(static entry => entry.Song.Descriptor.DemoPlay).ToArray() is { Length: > 0 } listed)
            songs = listed;
        var themed = songs.Where(entry => Shell.Skins.HasTheme(entry.Song.Descriptor, entry.Name)).ToArray();
        if (themed.Length != 0 && Shell.Random.Next(4) != 0)
            songs = themed;
        if (songs.Length == 0)
            return null;
        var (category, song) = songs[Shell.Random.Next(songs.Length)];
        // The scene's own randomness is drawn here, so a benchmark's seeded runs still pick alike.
        var seed = Shell.Random.Next();
        return (category, song, Task.Run(() => gameplay.DemoScene(song, category, new Random(seed))));
    }

    /// <summary>
    /// A drum hit, coin or card starts a credit (the attract's screens and its demo play); true when one did.
    /// </summary>
    public bool TryStart(FlowInput input)
    {
        // Under the menu the attract only goes on; a coin starts the entry once it closes.
        if (Shell.HomeMenuOpen)
            return false;
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
            Shell.Players.Reset();
            Shell.Hosts.EntryCardRejected = true;
            Shell.Scenes.Show(FlowScenes.Entry);
        }
        else if (Shell.TakeCard() is { } card)
        {
            Shell.Sounds?.StopAll();
            Shell.Hosts.EntryTrigger = 4;
            Shell.Players.Reset();
            Shell.Hosts.EntryCard = card;
            Shell.Scenes.Show(FlowScenes.Entry);
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
            Shell.Players.Reset();
            if (!coinStart)
                Shell.Players.Join(input.DrumSide ?? 0);
            Shell.Scenes.Show(FlowScenes.Entry);
        }
        else
            return false;
        if (!Shell.Arcade.Home)
            Shell.Book.CountCredit(free: Shell.Coins is null);
        return true;
    }

    // The CM opened ahead (at the caution screen) and its path, or null.
    private (string Path, Task<AttractMovie.Prepared> Movie)? _preparedMovie;

    // The next CM in the shuffled queue (refilled, never the same twice in a row across refills).
    private string nextMoviePath()
    {
        if (_queue.Count == 0)
        {
            var order = movies.ToArray();
            Shell.Random.Shuffle(order);
            if (order.Length > 1 && order[0] == _last)
                (order[0], order[^1]) = (order[^1], order[0]);
            foreach (var movie in order)
                _queue.Enqueue(movie);
        }
        return _last = _queue.Dequeue();
    }

    private void prepareMovie()
    {
        dropPreparedMovie();
        var path = nextMoviePath();
        _preparedMovie = (path, AttractMovie.Prepare(path, Shell.Audio?.Mixer.Format));
    }

    // A CM opened ahead that will not play (a credit started, or another took its turn).
    private void dropPreparedMovie()
    {
        if (_preparedMovie is not { } prepared)
            return;
        _preparedMovie = null;
        prepared.Movie.ContinueWith(static task =>
        {
            if (task.IsCompletedSuccessfully)
                task.Result.Dispose();
        }, TaskScheduler.Default);
    }

    private void startMovie(string? path)
    {
        // The CM opened at the caution screen; the opening movie, or a CM not opened ahead, opens now (waiting for it).
        Task<AttractMovie.Prepared> opening;
        if (path is null && _preparedMovie is { } prepared)
        {
            (path, opening) = prepared;
            _preparedMovie = null;
        }
        else
        {
            path ??= nextMoviePath();
            opening = AttractMovie.Prepare(path, Shell.Audio?.Mixer.Format);
        }
        try
        {
            Movie = new AttractMovie(Shell.Application, Shell.Audio, opening.GetAwaiter().GetResult());
            Shell.Active.Player.Layers.Single().Player.SetNativeFill(MovieFill, AttractMovie.Surface);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Attract movie {Path.GetFileName(path)} is unavailable: {exception.Message}");
        }
    }

    public void Dispose()
    {
        Movie?.Dispose();
        dropPreparedMovie();
    }
}
