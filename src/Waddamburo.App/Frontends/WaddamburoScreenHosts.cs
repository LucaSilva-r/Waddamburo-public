using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Frontends;

/// <summary>
/// Waddamburo's frontend's screens. Every movie is ours and reads the provider, so no layer gets a binding of the game's
/// kind: the hosts only keep the engine's sessions a screen runs on (the entry's joins, Song Select's list) and say when
/// a screen has ended. Home only: no coins, cards, revival or Green's credit ends; every song leads to the next one.
/// </summary>
/// <param name="counting">Whether screens count down (Song Select's time limit), as on Green's.</param>
internal sealed class WaddamburoScreenHosts(CreditState credit, Func<bool, SongSelectSession> createSelectSession,
    Action<int> playerJoined, Func<bool> counting) : IScreenHosts
{
    private OwnEntry? _entry;
    private OwnSongSelect? _songSelect;

    public IEntryScreen? Entry => _entry;

    public ISongSelectScreen? SongSelect => _songSelect;

    // Our dan select asks for its course itself (select requests); nothing chosen through the hosts.
    public DanSelectChoice? DanSelectChoice => null;

    public IReadOnlyList<Game.Dan.DanCourseStatus?> DanBoards => [];

    public bool RevivalSucceeded => false;

    public CoinBank? Coins { get; set; }

    public bool EntryCardPending => false;

    public TaikoCreditNext CreditNext => TaikoCreditNext.NextSong;

    public LumenLayerHost Create(SceneLayerDefinition layer)
    {
        if (layer.HostId == "player-entry")
        {
            var session = new EntrySession { SetupMode = credit.EntrySetup, Card = credit.EntryCard };
            session.Joined += playerJoined;
            session.Entered += playerJoined;
            credit.EntryCard = null;
            _entry = new OwnEntry(session);
            return new LumenLayerHost(null, _entry.Attach);
        }
        if (layer.HostId == "song-select")
        {
            _songSelect = new OwnSongSelect(createSelectSession(counting()));
            return new LumenLayerHost(_songSelect, _songSelect.Attach);
        }
        return new LumenLayerHost(null);
    }

    // Our movies end themselves the way the small callback-era packages do (boot: _global.is_end; the tutorial:
    // _global.isAllEnd); the rest end through their requests (result.continue, select ...).
    public bool Ended(SceneId screen, IReadOnlyList<LumenPlayer> movies)
    {
        if (movies.Count == 0)
            return false;
        if (screen == FlowScenes.Boot)
            return movies[0].StandIn || movies[0].ReadScriptValue("_global.is_end") is true;
        if (screen == FlowScenes.Tutorial)
            return movies[0].StandIn || movies[0].ReadScriptValue("_global.isAllEnd") is true;
        return false;
    }

    public void Poll(SceneId screen)
    {
        if (screen == FlowScenes.Entry)
            _entry?.Tick();
    }

    public IGameplayScreen Gameplay(LumenGameSceneInstance scene) => NoGameplayScreen.Instance;

    public void Left(SceneId screen)
    {
    }

    public void MuteAttract(bool muted)
    {
    }

    public void TimeOut(SceneId screen)
    {
    }

    public void EntryCoinsChanged()
    {
    }

    public bool InsertEntryCard(ScoreProfile card) => false;

    public void RejectEntryCard()
    {
    }

    public bool RefuseEntryCard() => false;

    /// <summary>The entry over its session: our movie reads player.joined and entry.setup and asks to join.</summary>
    private sealed class OwnEntry(EntrySession session) : IEntryScreen
    {
        public EntrySession Session => session;

        public LumenPlayer? Movie { get; private set; }

        public int Ticks { get; private set; }

        public bool SetupMode => session.SetupMode;

        public void Attach(LumenPlayer movie) => Movie = movie;

        public void Tick() => Ticks++;

        public bool PollReady() => Movie is not null;

        public void InitInfo()
        {
        }

        // The home player setup's look is told to our movie through the provider.
        public void SetSetupProfile(int side, ScoreProfile? profile)
        {
        }

        public void SetSetupSide(int side, bool don, bool board)
        {
        }

        public void CostumeChanged(int side)
        {
        }

        public void FinishSetup(IReadOnlyList<bool> playing)
        {
            if (!session.SetupMode)
                return;
            session.SetupMode = false;
            for (var side = 0; side < 2; side++)
                if (playing[side])
                    session.Enter(side);
                else
                    session.Leave(side);
        }
    }

    /// <summary>Song Select over its session: our movie walks the library tree through select requests.</summary>
    private sealed class OwnSongSelect(SongSelectSession session) : ISongSelectScreen, ILumenHostBinding, IDisposable
    {
        private bool _attached;

        public SongSelectSession Session => session;

        public SongSelectCatalogView Catalog => session.Catalog;

        public long CatalogRevision => session.Catalog.Revision;

        public void Attach(LumenPlayer movie) => _attached = true;

        public void Install(LumenHostContext context)
        {
        }

        public bool OwnReady() => _attached;

        public ISongSelectScroll? Scroll => null;

        public SongSelectSong? SongUnderCursor => session.CourseSelectSong
            ?? (session.Catalog.TryGetSong(session.StartCategory, session.StartSong, out var song) ? song : null);

        public bool OnlineSpineUnderCursor => false;

        public int SupportLevel => 0;

        public int[] ShownSupportCounts => [0, 0, 0, 0];

        public bool Reloading { get; set; }

        public void ShowPartner(int? side)
        {
        }

        // Our movie's options are the engine's already (TaikoGuest.Options).
        public void KeepOptions()
        {
        }

        public void RememberCurrentSong()
        {
            if (SongUnderCursor is not null && session.CourseSelect is { } at)
                session.Remember(at.Category, at.Song);
        }

        public (SongSelectSong Song, bool Added)? ToggleFavourite() => null;

        public void Dispose()
        {
            session.Timer.Stop();
            session.RecordPick();
            // A reload (library, grouping, order, search) keeps the music playing.
            if (!Reloading)
                session.StopMusic();
        }
    }

    /// <summary>Gameplay's screen calls are the game's movies' (Green); ours read the same through the provider.</summary>
    private sealed class NoGameplayScreen : IGameplayScreen
    {
        public static NoGameplayScreen Instance { get; } = new();

        public void ShowMode(LumenNativeSurfaceKey? text)
        {
        }

        public void ShowTitle(Func<LumenNativeSurfaceKey> title)
        {
        }

        public void ShowNextSong(string genre, TaikoSongInfo info)
        {
        }

        public void ShowCourseCard(LumenNativeSurfaceKey title)
        {
        }

        public void UpdateVisibility(bool showStage, bool showInfo)
        {
        }

        public void StartAiBattle(AiBattleProgress battle)
        {
        }
    }
}
