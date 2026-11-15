using Waddamburo.Game.Dan;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Flow;

/// <summary>
/// The skin's hosts for the screens' movie layers, as the flows see them: the engine state of the screen on stage and
/// the few things a flow asks of its movies. Created per run by the composition root.
/// </summary>
public interface IScreenHosts : ILumenLayerHostFactory, IScreenEnds
{
    /// <summary>The loaded entry (null outside it).</summary>
    IEntryScreen? Entry { get; }

    /// <summary>The loaded Song Select (null before the first).</summary>
    ISongSelectScreen? SongSelect { get; }

    /// <summary>The course list's pick: back, or the board picked (null while it runs).</summary>
    DanSelectChoice? DanSelectChoice { get; }

    /// <summary>The course behind each of the course list's boards (null: the back board).</summary>
    IReadOnlyList<DanCourseStatus?> DanBoards { get; }

    /// <summary>The revival the game movie ran was won.</summary>
    bool RevivalSucceeded { get; }

    /// <summary>The loaded screen's hosts do their per-tick work (the flow calls it where the screen needs it).</summary>
    void Poll(SceneId screen);

    /// <summary>The gameplay screen's movies outside the lanes, for the scene on stage.</summary>
    IGameplayScreen Gameplay(LumenGameSceneInstance scene);

    /// <summary>The screen leaves the stage: what its movies started (their music) stops.</summary>
    void Left(SceneId screen);

    /// <summary>The attract goes silent under the menu (its movies' own sounds; the engine mutes its buses).</summary>
    void MuteAttract(bool muted);

    /// <summary>The screen waited too long for the players (the AI battle farewell): its movies leave.</summary>
    void TimeOut(SceneId screen);

    /// <summary>The cabinet's coins (null on free play); the service menu replaces them.</summary>
    CoinBank? Coins { get; set; }

    /// <summary>The credits changed (a coin) while the entry is on stage.</summary>
    void EntryCoinsChanged();

    /// <summary>A card read during entry; false when no entry runs or one is already pending.</summary>
    bool InsertEntryCard(ScoreProfile card);

    /// <summary>A card rejected during entry: the red band.</summary>
    void RejectEntryCard();

    /// <summary>The entry is reading a card or waiting for a drum to take it.</summary>
    bool EntryCardPending { get; }

    /// <summary>The card is already playing on this cabinet.</summary>
    bool RefuseEntryCard();

    /// <summary>What follows the results.</summary>
    TaikoCreditNext CreditNext { get; }
}

/// <summary>The course list's end: back to Song Select, or a board picked.</summary>
public readonly record struct DanSelectChoice(bool Back, int? Board);

/// <summary>The entry on stage.</summary>
public interface IEntryScreen
{
    EntrySession Session { get; }

    /// <summary>The entry movie (null before it loads).</summary>
    LumenPlayer? Movie { get; }

    /// <summary>Ticks since the entry movie started.</summary>
    int Ticks { get; }

    /// <summary>The home player setup holds the drums (the entry shows the choices on its stands).</summary>
    bool SetupMode { get; }

    /// <summary>The entry's parts are set up (a movie of ours starts then).</summary>
    bool PollReady();

    /// <summary>The players' panels and the coin state are reset (as the entry starts).</summary>
    void InitInfo();

    /// <summary>The setup's choice for a side: its stand shows the profile's name board.</summary>
    void SetSetupProfile(int side, ScoreProfile? profile);

    /// <summary>What the setup shows on a side's stand: Don-chan and the name board.</summary>
    void SetSetupSide(int side, bool don, bool board);

    /// <summary>A side's choice changed: the costume change (smoke and cue).</summary>
    void CostumeChanged(int side);

    /// <summary>The setup is confirmed: the chosen sides join in place.</summary>
    void FinishSetup(IReadOnlyList<bool> playing);
}

/// <summary>Song Select on stage.</summary>
public interface ISongSelectScreen
{
    SongSelectSession Session { get; }

    SongSelectCatalogView Catalog { get; }

    /// <summary>The catalog revision play requests are pinned to.</summary>
    long CatalogRevision { get; }

    /// <summary>The screen's parts are set up and the list is assigned (a movie of ours starts then).</summary>
    bool OwnReady();

    /// <summary>The fast song scroll over the list (null: none).</summary>
    ISongSelectScroll? Scroll { get; }

    /// <summary>The song the player is on (null: a folder or nothing).</summary>
    SongSelectSong? SongUnderCursor { get; }

    /// <summary>The cursor is on the online room's spine.</summary>
    bool OnlineSpineUnderCursor { get; }

    /// <summary>AI battle: the intermediate chart picked, in steps below the normal one.</summary>
    int SupportLevel { get; }

    /// <summary>AI battle: the intermediate levels offered per course for the open song.</summary>
    int[] ShownSupportCounts { get; }

    /// <summary>Song Select is being replaced by a reload (it leaves without its exit).</summary>
    bool Reloading { set; }

    /// <summary>An online partner on a side (null: gone).</summary>
    void ShowPartner(int? side);

    /// <summary>The player's options are kept for when Song Select comes back.</summary>
    void KeepOptions();

    /// <summary>Song Select comes back on the song the player is on.</summary>
    void RememberCurrentSong();

    /// <summary>The song under the cursor joins or leaves the favourites; false: no song there.</summary>
    bool ToggleFavourite();
}

/// <summary>A fast scroll over Song Select's list: it takes the input around the movie's tick.</summary>
public interface ISongSelectScroll
{
    LumenInputSnapshot Before(LumenInputSnapshot input, bool listActive, bool keepWheel = false);

    void After();
}

/// <summary>The gameplay screen's movies outside the lanes: the song's panel, a course's card, AI battle's.</summary>
public interface IGameplayScreen
{
    /// <summary>The panel's corner text in place of the song's number (a replay; null: none).</summary>
    void ShowMode(LumenNativeSurfaceKey? text);

    /// <summary>The song's title art on the panel (drawn only when the skin has a panel for it).</summary>
    void ShowTitle(Func<LumenNativeSurfaceKey> title);

    /// <summary>A course's next song begins: its genre (song_info's genre labels) and number.</summary>
    void ShowNextSong(string genre, Gameplay.TaikoSongInfo info);

    /// <summary>The card between a course's songs, with the next song's title.</summary>
    void ShowCourseCard(LumenNativeSurfaceKey title);

    /// <summary>Each frame: whether the stage number and the panel show.</summary>
    void UpdateVisibility(bool showStage, bool showInfo);

    /// <summary>An AI battle starts: its movies follow the battle.</summary>
    void StartAiBattle(Gameplay.AiBattleProgress battle);
}
