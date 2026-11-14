using Waddamburo.App.Gameplay;
using Waddamburo.Game.Sound;

namespace Waddamburo.App.Audio;

/// <summary>
/// A song's sounds, asked of the skin by name: the drums' hit sounds in each player's tone and the event sounds
/// (combo voices, banners, balloons, kusudama, Waiwai's cut-ins and rare note, the song's end). The rules of when a
/// sound plays (muted demo, AI-don's silent lane, roll voices off) are here; which sound it is, the skin's.
/// </summary>
internal sealed class GameplaySounds(SoundBank bank, ISoundSet set)
{
    private readonly Dictionary<SoundRequest, IReadOnlyList<SoundCue>> _described = [];

    /// <summary>Waiwai gameplay plays its own drum tone.</summary>
    // ponytail: Waiwai song select's tone choice (AssignTone / RequestToneSE) is not offered yet.
    public bool Waiwai { get; set; }

    /// <summary>AI battle: AI-don's lane (1) makes no sound at all.</summary>
    public bool AiBattle { get; set; }

    /// <summary>
    /// Per player (lane 0/1; one player is 0): Don-chan's roll and balloon shout is off, as on normal
    /// and up (the game's rule; only easy keeps it). Every other voice plays on every course.
    /// </summary>
    public bool[] RollVoicesOff { get; set; } = [];

    /// <summary>False: two players' sounds are centred and shared (one of each at a time).</summary>
    public bool Panning { get; set; } = true;

    /// <summary>Each lane's drum sound (an index of the skin's tones); one player is lane 0.</summary>
    public int[] Tones { get; set; } = [];

    /// <summary>Silent (the attract's demo play under the menu).</summary>
    public bool Muted { get; set; }

    // A lane's request: its side when two players' sounds are kept apart, its player's tone.
    private SoundRequest request(string name, int? lane, int variant = 0) =>
        new(name, Panning ? lane : null, Tones.ElementAtOrDefault(lane ?? 0), variant, Waiwai ? "waiwai" : null);

    private IReadOnlyList<SoundCue> describe(SoundRequest request)
    {
        if (!_described.TryGetValue(request, out var cues))
            _described[request] = cues = set.Describe(request);
        return cues;
    }

    private void play(SoundRequest request)
    {
        foreach (var cue in describe(request))
            bank.Play(cue);
    }

    public void PrepareDrums(bool twoPlayers = false)
    {
        // Both sets for two players: panning may be switched in the pause menu.
        foreach (var lane in twoPlayers ? new int?[] { 0, 1, null } : [null])
            foreach (var name in new[] { "drum.don", "drum.ka" })
            {
                var tone = Tones.ElementAtOrDefault(lane ?? 0);
                foreach (var cue in describe(new(name, lane, tone, 0, Waiwai ? "waiwai" : null)))
                    bank.Preload(cue.Source);
            }
    }

    public void PlayDrum(int? lane, bool don)
    {
        if (!Muted && !(AiBattle && lane == 1))
            play(request(don ? "drum.don" : "drum.ka", lane));
    }

    /// <summary><paramref name="lane"/> is null for one player; two players: 0 left, 1 right.</summary>
    public void Play(int? lane, GameplaySoundEvent sound, int combo = 0)
    {
        // AI battle: AI-don's lane (1) is silent, drums included (PlayDrum).
        if (Muted || AiBattle && lane == 1) return;
        var name = sound switch
        {
            GameplaySoundEvent.LongNoteStarted => RollVoicesOff.ElementAtOrDefault(lane ?? 0) ? null : "roll.voice",
            GameplaySoundEvent.BalloonPopped => "balloon.popped",
            GameplaySoundEvent.KusudamaPopped => "kusudama.popped",
            GameplaySoundEvent.KusudamaFailed => "kusudama.failed",
            GameplaySoundEvent.Combo => "combo.voice",
            GameplaySoundEvent.FailBanner => "banner.fail",
            GameplaySoundEvent.ClearBanner => "banner.clear",
            GameplaySoundEvent.FullComboBanner => "banner.fullCombo",
            GameplaySoundEvent.RareHit => "waiwai.rare",
            GameplaySoundEvent.WaiwaiStart => "waiwai.start",
            GameplaySoundEvent.SynchroCutIn => "waiwai.synchro",
            GameplaySoundEvent.SoloCutIn => "waiwai.solo",
            GameplaySoundEvent.SongFinished => "song.finished",
            // The controllers' rumble (GameShell), not a sound.
            GameplaySoundEvent.BigHit or GameplaySoundEvent.DonHit or GameplaySoundEvent.KaHit or GameplaySoundEvent.LongDonHit
                or GameplaySoundEvent.LongKaHit or GameplaySoundEvent.BigLongHit => null,
            _ => throw new ArgumentOutOfRangeException(nameof(sound)),
        };
        if (name is null)
            return;
        // The rare note's sound is the player who hit it, panned or not.
        play(sound == GameplaySoundEvent.RareHit ? new(name, null, 0, lane ?? 0) : request(name, lane, combo));
    }
}
