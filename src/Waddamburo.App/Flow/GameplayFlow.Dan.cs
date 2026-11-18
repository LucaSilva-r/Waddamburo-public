using Waddamburo.App.Audio;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

internal sealed partial class GameplayFlow
{
    /// <summary>The songs of the course the results are showing.</summary>
    public IReadOnlyList<SongSelectSong> CourseSongs { get; private set; } = [];

    /// <summary>A dan course is under way: it cannot be resumed once paused, so a break can't be taken inside a run.</summary>
    public bool InCourse => _mode.SeveralSongs;

    /// <summary>
    /// A dan course: its songs merged into one chart, played one after another under their cards, ended early when a
    /// requirement can no longer be met, saved whole, with its own results.
    /// </summary>
    private sealed class DanCoursePlay(GameplayFlow flow, DanCourseTimeline course, IReadOnlyList<SongSelectSong> songs, DanCourse plan)
        : PlayMode(flow)
    {
        // Traced session31: between's nextsong starts 61 ticks after the rainbow's out (163861 -> 164878 ms); the card
        // started with the reveal, so its doors closed (and their Close sound played) under the rainbow. Reported on Discord.
        private const int CardDelayTicks = 61;

        // song_info's genreArray: TaikoSongInfo.Genre's labels.
        private static readonly string[] GenreLabels = ["jpop", "anime", "vocaloid", "doyo", "variety", "classic", "game", "namco"];

        private int _song;
        private TimeSpan _hold;
        private int? _cardTick;
        private TimeSpan? _endsAt;
        // The next song decodes ahead, off this thread: starting it there would stall the game.
        private Task<BufferedAudioSource>? _nextAudio;

        public override bool SeveralSongs => true;

        public override string? Presence => $"Dan Dojo · {plan.Name}";

        public override void Begin()
        {
            dropNextAudio();
            (_song, _hold, _cardTick, _endsAt) = (0, TimeSpan.Zero, null, null);
            Shell.Gameplay.Dan = plan; // put back by a restart (the play's end took it off)
        }

        public override GameplayTimeline Timeline() => new(course.Songs[0].Chart.AuthoredOffset, DanCourseTimeline.CardLeadIn);

        // A new song's audio clock starts a little behind where the last one stopped: time holds until it catches up.
        public override TimeSpan ChartTime(TimeSpan songTime)
        {
            var time = course.Songs[_song].Zero + songTime;
            return time > _hold ? time : _hold;
        }

        // A course's first card (and its music) waits under the opening rainbow, as Green's does.
        public override bool Reveal()
        {
            _cardTick = Shell.Tick + CardDelayTicks;
            Play.StartTick = _cardTick.Value; // headless, the clock waits for the card as the real one does
            return false;
        }

        public override string RevealLabel => RainbowTransitionComposition.PlainRevealLabel;

        public override void BeforeTick()
        {
            if (_cardTick is not { } cardAt || Shell.Tick < cardAt)
                return;
            _cardTick = null;
            Play.Music = Flow.startAudio(Shell.PlayRequests.Active!);
            Flow.restartSongClock();
            ShowSong();
        }

        public override TimeSpan AdvanceSong(TimeSpan elapsed)
        {
            // Traced session31: a requirement that can no longer be met ends the course with the song being played.
            if (_endsAt is null && Shell.Gameplay.DanFailed)
            {
                var failed = course.Songs[_song];
                var banner = failed.Zero + TaikoResultBanner.Time(failed.Chart);
                _endsAt = banner > elapsed ? banner : elapsed;
                Shell.Gameplay.EndCourseAt(_endsAt.Value);
                dropNextAudio();
            }
            if (_endsAt is null && _song + 1 < course.Songs.Count && elapsed >= course.Songs[_song].End)
            {
                nextSong(Shell.PlayRequests.Active!);
                elapsed = Flow.chartTime();
            }
            return elapsed;
        }

        public override TimeSpan? EndsAt => _endsAt;

        public override SceneDefinition? Shutter => FlowScenes.DanShutter;

        // Traced sessions 30 and 31, as the course's shutter closes.
        public override void ShutterClosing()
        {
            if (Shell.Sounds is not { } sounds)
                return;
            sounds.Frontend.PlayCue("VO_RESULT", 19);
            sounds.Frontend.PlayCue("SE_GAME", 15);
        }

        public override AudioStreamTransport? OpenAudio(PlayRequest request)
        {
            var playing = course.Songs[_song];
            var asset = songs[_song].Descriptor.AudioAsset;
            if (_nextAudio is { } prepared && Shell.Audio is { } ready)
            {
                _nextAudio = null;
                return ready.PlayTransport(new ScheduledAudioSource(prepared.GetAwaiter().GetResult(),
                    Play.Timeline!.AudioStart, Play.Timeline.LeadIn + playing.Chart.Duration + TimeSpan.FromSeconds(1)),
                    volume: SongLoudness.Volume(Shell.Assets, asset!, ready.Mixer.Format));
            }
            return Flow.openAudio(asset, playing.Chart.Duration);
        }

        // The song's music started: the next one decodes ahead.
        public override void MusicOpened()
        {
            if (_song + 1 >= songs.Count || Shell.Audio is not { } audio || songs[_song + 1].Descriptor.AudioAsset is not { } asset)
                return;
            var next = songs[_song + 1];
            _ = Shell.Titles.Resolve(Shell.Titles.GetTransitionTitle(next));
            _ = Shell.Titles.Resolve(Shell.Titles.GetGameplayTitle(next));
            _nextAudio = Task.Run(async () =>
            {
                SongLoudness.Volume(Shell.Assets, asset, audio.Mixer.Format); // measured now, read back when it starts
                var stream = await Shell.Assets.OpenReadAsync(asset).ConfigureAwait(false);
                BufferedAudioSource source;
                try
                {
                    source = new BufferedAudioSource(stream, audio.Mixer.Format);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                await source.Ready.ConfigureAwait(false);
                return source;
            });
        }

        private void dropNextAudio()
        {
            _nextAudio?.ContinueWith(static task =>
            {
                if (task.IsCompletedSuccessfully)
                    task.Result.Dispose();
            }, TaskScheduler.Default);
            _nextAudio = null;
        }

        // The next song: its music, on the course's clock, from under its card.
        private void nextSong(PlayRequest request)
        {
            _hold = Flow.chartTime();
            if (Play.Music is { } music)
                Shell.Audio?.Mixer.Stop(music.Handle, TimeSpan.FromMilliseconds(20));
            _song++;
            Play.Timeline = new GameplayTimeline(course.Songs[_song].Chart.AuthoredOffset, DanCourseTimeline.CardLeadIn);
            Play.MusicEndPosition = null;
            Play.AfterMusic.Reset();
            Flow.restartSongClock();
            Play.Music = Flow.startAudio(request);
            ShowSong();
        }

        public override void SongStarts() => ShowSong();

        // Traced session30, at each song's start: between's clip nextsong goes to "start" (its sounds are its own),
        // with the song's title, and song_info shows the song's title, genre and number.
        private void ShowSong()
        {
            var song = songs[_song];
            var card = Shell.Titles.GetTransitionTitle(song);
            _ = Shell.Titles.Resolve(card);
            if (Flow.sceneLayer("between") is { } between)
            {
                between.SetNativeFill("next_song_name", card);
                if (!between.TryGotoLabel("nextsong", "start"))
                    Console.Error.WriteLine("between has no nextsong clip to start.");
            }
            // Each song's own genre, as the select screen shows it (the genre Song Select had set stayed for the whole
            // course; reported on Discord).
            Shell.SongInfo = Shell.SongInfo with
            {
                Genre = Shell.Hosts.Dan?.Genre(song.Descriptor) ?? Shell.SongInfo.Genre,
                Stage = _song + 1,
            };
            // song_info asks LumenSongInfo only as the song starts (main.genre.gotoAndStop(genreArray[GetGenrNumber()]),
            // main.stage.gotoAndStop(GetPlayCount())), so a course's next songs set its clips the same way here.
            if (Flow.sceneLayer("song_info") is { } info
                && !(info.TryGotoLabel("main/genre", GenreLabels[Math.Clamp(Shell.SongInfo.Genre, 0, GenreLabels.Length - 1)], play: false)
                    && info.TryGotoFrame("main/stage", Shell.SongInfo.StageFrame - 1, play: false)))
                Console.Error.WriteLine("song_info has no main.genre / main.stage clip to set.");
            Flow.showSongTitle(song);
        }

        public override void End(bool finished)
        {
            Shell.Credit.DanResult = finished ? result() : null;
            Flow.CourseSongs = finished ? songs : [];
            dropNextAudio();
        }

        private DanCourseResult result()
        {
            var total = Shell.Gameplay.DanTotal;
            var counts = Shell.Gameplay.DanSongs(course.Songs.Count);
            return new DanCourseResult(plan, [.. course.Songs.Select((song, index) => new DanSongResult(
                    Shell.Hosts.Dan?.Genre(songs[index].Descriptor) ?? 0, (int)song.Course, song.Chart.Level ?? 0, counts[index]))],
                _song + 1, total, Shell.Gameplay.DanFailed ? 0 : DanCourseResult.LevelOf(plan, total, Shell.Gameplay.DanSongEnds))
                { SongEnds = [.. Shell.Gameplay.DanSongEnds] };
        }

        // Offline too: nothing here waits for the server; it goes up with the next upload.
        public override void Save(PlayRequest played)
        {
            // Failed runs aren't kept, here or on the server.
            if (Shell.Credit.DanResult is not { } result || result.Level == 0 || Shell.Autoplay || Shell.DevAutoplay
                || Shell.Sync.Scores is not { } scores || TaikoGuest.Profiles[(int)played.Players[0].Player] is not { } profile)
                return;
            var replays = course.Split(Shell.Gameplay.Replays[0]);
            try
            {
                scores.SaveDan(new DanPlayRecord(Guid.NewGuid(), profile.Baid, result,
                    [.. course.Authored.Select(static song => (string?)ChartHash.Compute(song.Chart, song.Course))], DateTimeOffset.UtcNow,
                    [.. replays.Select(static replay => replay.Encode())])
                {
                    AudioOffsetMs = (int)Play.AudioOffset.TotalMilliseconds,
                    InputOffsetMs = (int)Play.InputOffset.TotalMilliseconds,
                });
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                // A lost score must not end the credit.
                Console.Error.WriteLine($"Error SCORE_SAVE: {exception.Message}");
            }
            var encoded = replays.Select(static replay => replay.Encode()).ToList();
            _ = Task.Run(() => checkScore(result, course, encoded));
            Shell.Sync.Upload(profile);
        }

        // The server scores a course from its songs' replays and stored charts: this play should score the same there.
        private static void checkScore(DanCourseResult result, DanCourseTimeline course, IReadOnlyList<byte[]> replays)
        {
            try
            {
                var stored = course.Authored.Select(static song => ChartHash.Deserialize(ChartHash.Serialize(song.Chart, song.Course), song.Chart.Key.Song.Source)).ToList();
                var first = stored[0];
                var again = PlayRescore.ScoreCourse(result.Course, DanCourseTimeline.Merge(stored), [.. replays.Select(TaikoReplay.Decode)],
                    Gameplay.TaikoGameplayPresentation.WindowsFor(first.Chart, first.Course));
                var same = again.Total == result.Total && again.Level == result.Level && again.SongsPlayed == result.SongsPlayed
                    && again.Songs.SequenceEqual(result.Songs.Select(static song => song.Counts));
                Console.WriteLine(same ? "Dan play rescored the same from its replays."
                    : $"Error DAN_RESCORE: played {result.Total} level {result.Level} songs {result.SongsPlayed}; rescored {again.Total} level {again.Level} songs {again.SongsPlayed}.");
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException or EndOfStreamException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Error DAN_RESCORE: {exception.Message}");
            }
        }

        public override SceneDefinition? Results => Shell.Credit.DanResult is not null ? FlowScenes.DaniResults : null;

        // A course starts over from its first song, also from its results.
        public override void Restarting() => Shell.Credit.DanResult = null;
    }
}
