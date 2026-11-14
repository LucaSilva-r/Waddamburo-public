using Waddamburo.App.Audio;
using System.Diagnostics;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Editor;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Online;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

internal sealed partial class GameplayFlow
{
    // What one play (one Enter: a song, a restart, a dan course) knows: made fresh at each Enter, so nothing carries over.
    private Play _play = new();

    private sealed class Play
    {
        // The charts as played (each player's options applied) and as authored (hashed, uploaded, saved under).
        public PlayableChart[] Charts = [];
        // AI battle on a level below いつもの (a simplified chart).
        public bool Intermediate;
        // Each lane's chart as played, before the player's modchart and options: an AI battle's intermediate level is its own
        // simplified chart (saved under its own hash, so it never takes the official chart's crown).
        public PlayableChart[] PlayedSources = [];
        // The player's hit offsets this play (ms, late positive): the play keeps their median and variance for AI-don.
        public readonly List<double> HitOffsets = [];
        public int Seed;
        public bool SongInfoMode;
        public AudioStreamTransport? Music;
        public GameplayTimeline? Timeline;
        public GameplayAutoplay? Autoplay;
        // AI battle: AI-don's lane, and the battle's own movies.
        public AiDrummer? AiBot;
        public int SupportLevel;
        // The battle as the server replays it: AI-don's starting timing, and whether the play uploads (a signed-in player).
        public (double Median, double Variance)? AiPrior;
        public bool AiUploads;
        public AiBattlePresentation? Battle;
        // Home, autoplay: a reviewed play (pause, seek, speed); its clock replaces the music's.
        public ReviewSession? Review;
        // Home, training: attempts on the review (Review is its playback).
        public TrainingSession? Training;
        // An instant replay made this play void: it is never saved.
        public bool Voided;
        public readonly Stopwatch Clock = new();
        public int StartTick;
        public int ShutterStartTick = -1;
        public bool ShutterClosing;
        public bool Paused;
        public PlayRequest? LastRequest;
        public HashSet<ChartKey> UploadProtectedCharts = [];
        public bool CustomUploadsAtStart;
        public int PausedAtTick;
        public int PausedTicks;
        public float PauseInterpolation;
        // The player's offsets, fixed for a play (judgement time must not step back mid-song).
        public TimeSpan AudioOffset;
        public TimeSpan InputOffset;
        // The music's position stops with the music: after it, the song's time goes on by a stopwatch.
        public TimeSpan? MusicEndPosition;
        public bool ResultSent;
        public readonly Stopwatch AfterMusic = new();
        public int CourseSong;
        public OnlineEcho? Echo;
        public TimeSpan CourseHold;
        public int? CourseCardTick;
        public TimeSpan? CourseEndsAt;
        // The player's layout is showing (the chart has no modchart of its own).
        public bool LayoutShown;
        // Escape after the last note: the shutter closes now rather than after the banner and the outro.
        public bool SkipToShutter;
    }
}
