using Waddamburo.App.Flow.SongSelect;
using Waddamburo.App.Online;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.App.Hosting;

/// <summary>
/// What the next Song Select lists (the library, the osu! grouping and order, a search, an online song, the folders the
/// credit is offered) and where its cursor starts. Engine side: every skin's song select is built from it.
/// </summary>
internal sealed class SongSelectSetup(
    CreditState credit,
    SongCatalogSnapshot catalog,
    ISongBoardTextureService textures,
    ISongPreviewController previews,
    IPlayRequestSink playRequests,
    Func<ArcadeSettings> arcadeSettings)
{
    private CreditState Credit => credit;
    private ArcadeSettings arcade => arcadeSettings();

    /// <summary>A drum's play history as chart keys, newest first (this PC's plays and the server's); null: none.</summary>
    public Func<int, IReadOnlyList<string>?>? RecentCharts { get; init; }

    /// <summary>The settings' random-song spine (the movie's roll folder) is on.</summary>
    public Func<bool>? RandomSongFolder { get; init; }

    /// <summary>Home: the tutorial was watched once (kept in config.cfg); song select no longer offers it.</summary>
    public Func<bool>? HomeTutorialSeen { get; init; }

    /// <summary>Whether Song Select offers the Online folder.</summary>
    public Func<bool>? OnlineAvailable { get; init; }

    /// <summary>The player is in an online room: songs are played together, so no dan dojo.</summary>
    public Func<bool>? InOnlineRoom { get; init; }

    /// <summary>A song another player sent this session (Song Select lists it while it's the online song).</summary>
    public Func<SongKey, SongDescriptor?>? Received { get; init; }

    /// <summary>The songs marked favourite (a folder per library, a red coin icon on their boards).</summary>
    public SongFavourites? Favourites { get; init; }

    /// <summary>The dan dojo's courses (null: no such folder in this run).</summary>
    public DanCourses? Dan { get; set; }

    /// <summary>TaikOnline's running event, for Song Select's event folder (null: none).</summary>
    public Func<SongSelectEvent?>? Event { get; init; }

    /// <summary>What the next Song Select starts on (the library, the osu! grouping, a search, an online song).</summary>
    public SongSelectMemory Memory { get; } = new(catalog, arcadeSettings);

    /// <summary>A new cabinet credit: Song Select no longer starts on the previous credit's song.</summary>
    public void ForgetLastSong()
    {
        Memory.ForgetLastSong();
        _current?.ForgetPicked();
    }

    // "Only what I earned": the stock songs locked for every joined player (a guest, or a player who unlocks everything, has
    // none locked, so a song shows when anyone at the drums may play it).
    private HashSet<SongKey> lockedSongs()
    {
        int[] sides = Credit.TwoPlayers ? [0, 1] : [Credit.PlayerSide];
        var locked = sides.Select(side => TaikoGuest.Profiles[side]?.Locked?.Songs ?? []).Aggregate(
            static (shared, songs) => [.. shared.Intersect(songs)]);
        return [.. locked.Select(static musicId => new SongKey(SongSourceKind.Stock, musicId))];
    }

    private SongSelectSession? _current;

    /// <summary>
    /// The next Song Select's session: its catalog view for the credit (mode, libraries, folders offered) and where
    /// its cursor starts (the online song, the search, the spine or library left, else the last song played).
    /// </summary>
    public SongSelectSession CreateSession(bool counting)
    {
        var memory = Memory;
        var linked = Credit.Waiwai ? Array.Empty<SongSourceKind>() : memory.Libraries();
        var view = new SongSelectCatalogView(catalog.Without(lockedSongs()), Credit.Waiwai ? SongSelectMode.Waiwai : SongSelectMode.Normal,
            modeSwitch: Credit.TwoPlayers, library: linked.Length > 0 && memory.OnlineSong is null ? memory.Library : null,
            // AI battle too: its song select takes the custom folders (SongSelectGenrePatch), the other libraries' among them.
            links: linked.Where(library => library != memory.Library), favourites: Favourites?.Songs,
            tutorial: HomeTutorialSeen?.Invoke() != true && !(Credit.AiBattle ? Credit.AiTutorialSeen : Credit.Waiwai ? Credit.WaiwaiTutorialSeen : Credit.TutorialSeen),
            browse: memory.OsuBrowse, search: memory.Search, // As on the game: no dan dojo for two players, or without an account (it works offline once its list is saved).
            danDojo: !Credit.AiBattle && Dan?.Available == true && !Credit.TwoPlayers && TaikoGuest.Profiles[Credit.PlayerSide] is not null
                && InOnlineRoom?.Invoke() != true,
            danSeries: arcade.Home ? Dan?.Series : null,
            online: !Credit.Waiwai && !Credit.AiBattle && !Credit.TwoPlayers && OnlineAvailable?.Invoke() == true, onlySong: memory.OnlineSong, received: Received,
            courseStar: memory.OnlineSong is null, rollFolder: RandomSongFolder?.Invoke() == true,
            eventFolder: memory.OnlineSong is null ? Event?.Invoke() : null, byGame: memory.WiiByGame,
            recentCharts: Credit.TwoPlayers || memory.OnlineSong is not null ? null : RecentCharts?.Invoke(Credit.PlayerSide));
        var (start, startSong) = memory.Start(view, _current?.Picked);
        return _current = new SongSelectSession(view, textures, previews, playRequests)
        {
            Counting = counting,
            StartCategory = start,
            StartSong = startSong,
            ForcePick = memory.OnlineSong is not null,
        };
    }
}
