using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Scores;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow.Gameplay;

internal sealed partial class GameplayFlow
{
    // The play's mode: Prepare picks normal play, PrepareCourse a dan course.
    private PlayMode _mode { get => _chosenMode ??= new NormalPlay(this); set => _chosenMode = value; }
    private PlayMode? _chosenMode;

    /// <summary>
    /// What a play is for (docs/development/app-refactor.md): how its chart clock runs, how it starts, ends, saves and
    /// restarts. Normal play is the defaults; a mode overrides only what differs. Nested in the flow: a mode works on
    /// the play's shared machinery (the play's state, its clock, its music) without that becoming the flow's API.
    /// </summary>
    private abstract class PlayMode(GameplayFlow flow)
    {
        protected GameplayFlow Flow { get; } = flow;
        protected GameShell Shell => Flow.Shell;
        protected Play Play => Flow._play;

        /// <summary>
        /// Several songs played as one (a dan course): no single chart to hash, review, replay or lay out, the stage
        /// number always shows, and the play saves whole at its end (<see cref="Save"/>), not as a song.
        /// </summary>
        public virtual bool SeveralSongs => false;

        /// <summary>
        /// A player's play (the attract demo is not): it counts for the website's status, saves, pauses, restarts, takes
        /// the player's layout and modcharts' editors, and echoes online.
        /// </summary>
        public virtual bool RealPlay => true;

        public virtual IndicatorScene Indicators => IndicatorScene.Gameplay;

        /// <summary>What song_info's corner says (null: the play's own: autoplay, replay, nothing).</summary>
        public virtual string? ModeLabel => null;

        /// <summary>The lanes always play themselves, a hit every this often (null: only on autoplay).</summary>
        public virtual TimeSpan? AutoplayInterval => null;

        public virtual bool EndBanner => true;

        /// <summary>The player's own layout applies (a demo and Waiwai play the stock screen).</summary>
        public virtual bool UsesPlayerLayout => true;

        /// <summary>The keys the player's lanes see under the autoplay.</summary>
        public virtual SdlKeyboardSnapshot PlayerKeys(SdlKeyboardSnapshot keys) => keys;

        /// <summary>The mode takes the whole tick (true: the play does nothing else this tick).</summary>
        public virtual bool TakesTick(FlowInput input) => false;

        /// <summary>The gameplay scene is left.</summary>
        public virtual void Exit()
        {
        }

        /// <summary>The partner's lane is echoed (online play alone, WADDAMBURO_ONLINE_ECHO).</summary>
        public virtual bool EchoesPartner => RealPlay;

        /// <summary>The charts the autoplay plays (AI battle: the player's lane only).</summary>
        public virtual PlayableChart[] AutoplayCharts => Play.Charts;

        /// <summary>The play's seed for its random options (a replay's own).</summary>
        public virtual int Seed(PlayRequest request) => request.Seed ?? Random.Shared.Next();

        /// <summary>The charts played, from the songs' own (AI battle's support menu simplifies them).</summary>
        public virtual PlayableChart[] Sources(PlayableChart[] sources, PlayerChartRequest[] players, PlayRequest request) => sources;

        /// <summary>The presentation started on the charts: the mode adds its own lanes (AI-don's).</summary>
        public virtual void LanesStarted(LumenGameSceneInstance active, PlayerChartRequest[] players)
        {
        }

        /// <summary>The play is ending (an audio failure then lets it finish instead of abandoning it).</summary>
        public virtual bool Ending(TimeSpan elapsed, TimeSpan? bannerAt) => bannerAt is { } at && elapsed - Play.InputOffset >= at;

        /// <summary>
        /// The play is over and its shutter closes. Traced: the shutter closes 9 s after the end banner, by the chart, not
        /// the audio (whose end only freezes the chart clock).
        /// </summary>
        public virtual bool Finished(TimeSpan elapsed, TimeSpan? bannerAt) =>
            bannerAt is { } endBanner && elapsed >= endBanner + TaikoResultBanner.ShutterDelay;

        /// <summary>The results cut in at once instead of 70 ticks after the shutter closed.</summary>
        public virtual bool ResultsCutIn => false;

        /// <summary>The battle a lane's play saves with (null: a song's play).</summary>
        public virtual Waddamburo.Services.Scores.AiBattlePlay? Battle(int lane, ScoreProfile profile, ScoreStore scores, string sha,
            bool cleared, PlayableChart played, PlayerChartRequest player) => null;

        /// <summary>The Waiwai layout both lanes play on (null: not Waiwai).</summary>
        public virtual WaiwaiComposition? Composition => null;

        /// <summary>A finished play saves each lane's song (Waiwai doesn't yet).</summary>
        public virtual bool SavesSongs => true;

        /// <summary>The Discord status while it plays; null: the song's own.</summary>
        public virtual string? Presence => null;

        /// <summary>The play starts (each Enter): the mode's per-play state goes back to its start.</summary>
        public virtual void Begin()
        {
        }

        public virtual GameplayTimeline Timeline() => new(Play.Charts[0].AuthoredOffset, TimeSpan.FromSeconds(3));

        /// <summary>The chart position from the song clock.</summary>
        public virtual TimeSpan ChartTime(TimeSpan songTime) => songTime;

        /// <summary>The rainbow opens on the play: false when its first song waits (a course's card), true when it starts now.</summary>
        public virtual bool Reveal() => true;

        public virtual string RevealLabel => RainbowTransitionComposition.RevealLabel;

        /// <summary>Each tick once revealed, before the play's own checks (a course's card ends, its next song starts).</summary>
        public virtual void BeforeTick()
        {
        }

        /// <summary>Each tick of the play itself; returns the chart time after it (a course moves on to its next song).</summary>
        public virtual TimeSpan AdvanceSong(TimeSpan elapsed) => elapsed;

        /// <summary>When the end banner shows; null: the charts' own.</summary>
        public virtual TimeSpan? EndsAt => null;

        /// <summary>The shutter that closes on the play (null: none).</summary>
        public virtual SceneDefinition? Shutter => FlowScenes.Shutter;

        /// <summary>The shutter starts closing.</summary>
        public virtual void ShutterClosing()
        {
        }

        /// <summary>The song's music: the request's audio for the chart.</summary>
        public virtual AudioStreamTransport? OpenAudio(PlayRequest request) =>
            Flow.openAudio(request.AudioAsset, Play.Charts[0].Duration);

        /// <summary>The song's music was opened (a course decodes its next song ahead).</summary>
        public virtual void MusicOpened()
        {
        }

        /// <summary>The song starts playing (a course shows its card and song_info for it).</summary>
        public virtual void SongStarts()
        {
        }

        /// <summary>The play is over (finished: to its results). Runs before the presentation stops.</summary>
        public virtual void End(bool finished)
        {
        }

        /// <summary>A finished play saves (after the songs' own save, which a <see cref="SeveralSongs"/> play skips).</summary>
        public virtual void Save(PlayRequest played)
        {
        }

        /// <summary>The results scene for a finished play; null: the song's (normal, AI battle or Waiwai).</summary>
        public virtual SceneDefinition? Results => null;

        /// <summary>Restart plays the same again: what the mode puts back first.</summary>
        public virtual void Restarting()
        {
        }
    }

    private sealed class NormalPlay(GameplayFlow flow) : PlayMode(flow);
}
