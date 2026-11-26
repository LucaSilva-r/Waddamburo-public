using Waddamburo.Lumen.Runtime;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Flow.Gameplay;

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
            var pick = Shell.Hosts.SongSelect?.SupportLevel ?? 0;
            var offered = Shell.Hosts.SongSelect?.ShownSupportCounts.ElementAtOrDefault((int)players[0].Course) ?? 0;
            var supportLevel = SupportInfo.SupportPick(ChartSimplifier.LevelOf(players[0].Course), offered, pick);
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
            var battle = Play.Lanes.Battle = new AiBattleProgress(Play.Charts[0], players[0].Course);
            Shell.Hosts.Gameplay(active).StartAiBattle(battle);
            publishBattle(battle);
            if (Play.Review is not null)
                return;
            // A signed-in player's battle uploads, with or without a server setup (offline): the server replays it, so
            // it plays the frozen online tuning. Local guests may use the installation's.
            _uploads = Flow.AiSession is not null
                || TaikoGuest.Profiles[(int)players[0].Player] is { } signedIn && signedIn.Baid != ScoreProfile.LocalGuestBaid;
            _prior = Flow.AiSession is { } session ? session.Timing : Shell.AiBattle.Standing((int)players[0].Player).Timing;
            var simulation = new AiBattleSimulation(Play.Charts[0], players[0].Course, Play.Seed, _prior,
                _uploads ? AiBattleSimulation.OnlineLevel : Shell.AiBattle.Levels.Count > 0 ? Shell.AiBattle.Levels[0] : null);
            Play.Lanes.AiBot = new AiDrummer(simulation, () => Shell.Gameplay.Replays[0].Inputs);
            battle.Simulation = simulation;
            Shell.Gameplay.FollowBranch(1, 0);
        }

        // AI battle's state for every movie of ours on the lanes (the game's movies are told by GameAiBattleMovies).
        private void publishBattle(AiBattleProgress battle)
        {
            static LumenHostValue n(double value) => LumenHostValue.FromNumber(value);
            var gameplay = Shell.Gameplay;
            gameplay.Publish("ai.sections", n(battle.Sections));
            gameplay.Publish("ai.section", n(1));
            gameplay.Publish("ai.needle", n(0));
            gameplay.Publish("ai.emotion", n(battle.Emotion));
            battle.ProgressChanged += percent => gameplay.Publish("ai.progress", n(percent));
            battle.SectionCue += middle => gameplay.Emit("ai.sectionCue", LumenHostValue.FromBoolean(middle));
            battle.CountsChanged += (side, counts) =>
            {
                gameplay.Publish("ai.good", n(counts.Great), side);
                gameplay.Publish("ai.ok", n(counts.Good), side);
                gameplay.Publish("ai.bad", n(counts.Miss), side);
                gameplay.Publish("ai.rolls", n(counts.Rolls), side);
                gameplay.Publish("ai.needle", n(battle.Needle));
            };
            battle.SectionEnded += (won, last) =>
            {
                var ended = battle.SectionsWon.Count - 1;
                gameplay.PublishItem("ai.won", ended, LumenHostValue.FromBoolean(won));
                gameplay.Publish("ai.section", last ? LumenHostValue.Undefined : n(ended + 2));
                gameplay.Emit("ai.sectionEnded", LumenHostValue.FromBoolean(won), LumenHostValue.FromBoolean(last));
            };
            battle.EmotionChanged += emotion => gameplay.Publish("ai.emotion", n(emotion));
            battle.ResultShown += won => gameplay.Emit("ai.result", LumenHostValue.FromBoolean(won));
        }

        // By the battle result, which comes after the last section (traced, no end banner nor shutter).
        public override bool Ending(TimeSpan elapsed, TimeSpan? bannerAt) => Play.Lanes.Battle?.ResultShownAt.HasValue == true;

        public override bool Finished(TimeSpan elapsed, TimeSpan? bannerAt) =>
            Play.Lanes.Battle?.ResultShownAt is { } shown && elapsed - Play.InputOffset >= shown + AiBattleProgress.ResultsDelay;

        // Traced (every AI battle): no shutter; the results cut in.
        public override SceneDefinition? Shutter => null;

        public override bool ResultsCutIn => true;

        public override SceneDefinition? Results => Shell.AiBattle.Outcome is not null ? Waddamburo.Skins.Green.GreenScenes.AiResults : null;

        // The player's lane: its battle, and the rank points it earned on the standing before it.
        public override Waddamburo.Services.Scores.AiBattlePlay? Battle(int lane, ScoreProfile profile, ScoreStore scores, string sha,
            bool cleared, PlayableChart played, PlayerChartRequest player)
        {
            if (lane != 0 || Play.Lanes.Battle?.Outcome is not { Sections.Count: > 0 } outcome)
                return null;
            var battle = Shell.AiBattle.Record(outcome, profile, scores, sha, cleared, player.Course, played.Level ?? 0, _uploads,
                usual: !_intermediate, Flow.AiSession, _supportLevel) with
            {
                BaseSha256 = ChartHash.Compute(Flow._sourceCharts[lane], player.Course), Prior = _prior,
            };
            Console.WriteLine($"AI battle saved: {(outcome.Won ? "won" : "lost")} {battle.Sections}, {battle.Points} rank point(s).");
            return battle;
        }

    }
}
