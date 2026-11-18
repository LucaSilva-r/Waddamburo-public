using Waddamburo.App.Gameplay;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Flow;

internal sealed partial class GameplayFlow
{
    /// <summary>
    /// An AI battle song: the player against AI-don on a second lane playing the same chart (or the support menu's
    /// simpler one), scored by sections; no end banner or shutter, its own results, its battle saved with the play.
    /// </summary>
    private sealed class BattlePlay(GameplayFlow flow) : PlayMode(flow)
    {
        private bool _intermediate, _uploads;
        private int _supportLevel;
        private (double Median, double Variance)? _prior;

        public override void Begin() => (_intermediate, _uploads, _supportLevel, _prior) = (false, false, 0, null);

        public override bool EndBanner => false;

        public override bool EchoesPartner => false;

        public override PlayableChart[] AutoplayCharts => [Play.Charts[0]];

        public override int Seed(PlayRequest request) => Flow.AiSession?.Seed ?? base.Seed(request);

        // The support menu: a level below いつもの is the course's chart simplified (both lanes play it). A server setup
        // binds that exact chart and options; anything else plays without it.
        public override PlayableChart[] Sources(PlayableChart[] sources, PlayerChartRequest[] players, PlayRequest request)
        {
            var pick = Shell.Loaded.SongSelect?.SupportLevel ?? 0;
            var offered = Shell.Loaded.SongSelect?.ShownSupportCounts.ElementAtOrDefault((int)players[0].Course) ?? 0;
            var supportLevel = Waddamburo.Game.Lumen.SongSelectHostBinding.SupportPick(ChartSimplifier.LevelOf(players[0].Course), offered, pick);
            _intermediate = pick > 0 && ChartSimplifier.SourceCourse(supportLevel) == players[0].Course;
            if (_intermediate)
                Console.WriteLine($"AI battle: level {supportLevel} (pick {pick} of {offered}).");
            PlayableChart[] played = [.. sources.Select(source => _intermediate ? ChartSimplifier.Simplify(source, supportLevel) : source)];
            _supportLevel = _intermediate ? supportLevel : 0;
            if (Flow.AiSession is { } setup && (setup.ChartSha256 != ChartHash.Compute(played[0], players[0].Course)
                || setup.Course != (int)players[0].Course || setup.Options != players[0].Options.Bits || setup.SupportLevel != _supportLevel))
            {
                Flow.AiSession = null;
                Play.Seed = request.Seed ?? Random.Shared.Next();
            }
            return played;
        }

        // The second lane is AI-don's, drummed by the bot on the second drum's keys.
        public override void LanesStarted(LumenGameSceneInstance active, PlayerChartRequest[] players)
        {
            var battle = Play.Lanes.Battle = new AiBattlePresentation(active, Play.Charts[0], players[0].Course,
                Shell.Sounds is { } battleSounds ? battleSounds.Frontend.PlayCue : null);
            if (Play.Review is not null)
                return;
            // A signed-in player's battle uploads, with or without a server setup (offline): the server replays it, so
            // it plays the frozen online tuning. Local guests may use the installation's.
            _uploads = Flow.AiSession is not null
                || TaikoGuest.Profiles[(int)players[0].Player] is { } signedIn && signedIn.Baid != ScoreProfile.LocalGuestBaid;
            _prior = Flow.AiSession is { } session ? session.Timing : progress(players[0].Player).Timing;
            var simulation = new AiBattleSimulation(Play.Charts[0], players[0].Course, Play.Seed, _prior,
                _uploads ? AiBattleSimulation.OnlineLevel : Shell.GhostLevels.Count > 0 ? Shell.GhostLevels[0] : null);
            Play.Lanes.AiBot = new AiDrummer(simulation, () => Shell.Gameplay.Replays[0].Inputs);
            battle.Simulation = simulation;
            Shell.Gameplay.FollowBranch(1, 0);
        }

        // By the battle result, which comes after the last section (traced, no end banner nor shutter).
        public override bool Ending(TimeSpan elapsed, TimeSpan? bannerAt) => Play.Lanes.Battle?.ResultShownAt.HasValue == true;

        public override bool Finished(TimeSpan elapsed, TimeSpan? bannerAt) =>
            Play.Lanes.Battle?.ResultShownAt is { } shown && elapsed - Play.InputOffset >= shown + AiBattlePresentation.ResultsDelay;

        // Traced (every AI battle): no shutter; the results cut in.
        public override SceneDefinition? Shutter => null;

        public override bool ResultsCutIn => true;

        public override SceneDefinition? Results => Shell.Credit.AiOutcome is not null ? FlowScenes.AiResults : null;

        // The player's lane: its battle, and the rank points it earned on the standing before it.
        public override Game.Scores.AiBattlePlay? Battle(int lane, ScoreProfile profile, ScoreStore scores, string sha,
            bool cleared, PlayableChart played, PlayerChartRequest player)
        {
            if (lane != 0 || Play.Lanes.Battle?.Outcome is not { Sections.Count: > 0 } outcome)
                return null;
            var ranks = _uploads ? AiBattleRanks.Green : Shell.AiRanks;
            var session = Flow.AiSession;
            var before = session is { } setup ? new AiProgress(setup.Points, setup.Wins, setup.Timing) : scores.AiProgress(profile.Baid);
            // Offline, a signed-in player's battle counts provisionally; the server verifies it on upload.
            var ranked = session?.Ranked ?? _uploads;
            var award = ranks.Award(before.Points, player.Course, played.Level ?? 0, outcome.Won, cleared,
                usual: !_intermediate && ranked, firstWin: !scores.HasAiWin(profile.Baid, sha));
            var battle = new Game.Scores.AiBattlePlay(outcome.Won, string.Concat(outcome.Sections.Select(static section => section.Won ? 'W' : 'L')),
                _supportLevel, award.Points)
            {
                SessionId = session?.Id, Ranked = ranked,
                BaseSha256 = ChartHash.Compute(Flow._sourceCharts[lane], player.Course), Prior = _prior,
                SectionCounts = Waddamburo.Game.Lumen.AiSectionCounts.Format(outcome.Sections.Select(static section => section.Player)),
            };
            Shell.Credit.AiSectionBest = scores.AiSectionBest(profile.Baid, sha);
            var rank = ranks.RankOf(before.Points);
            Shell.Credit.AiNumbers = new(rank, ranks.RankOf(before.Points + award.Points), ranks.PointsIntoRank(before.Points),
                award.Points, award.FirstWin, award.HighLevel, ranks.StepSize(rank), before.Wins, AiBattleRanks.NextRewardWins(before.Wins))
                { ShowProgress = ranked };
            Console.WriteLine($"AI battle saved: {(outcome.Won ? "won" : "lost")} {battle.Sections}, {award.Points} rank point(s).");
            return battle;
        }

        /// <summary>A drum's player's AI battle standing (rank points, wins, last timing); none without a profile or store.</summary>
        private AiProgress progress(LocalPlayerSlot slot) =>
            (TaikoGuest.Profiles[(int)slot] ?? (Shell.Arcade.Home ? ScoreProfile.LocalGuest : null)) is { } profile
                && Shell.Sync.Scores is { } scores ? scores.AiProgress(profile.Baid) : AiProgress.None;
    }
}
