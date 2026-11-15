namespace Waddamburo.Game.Sound;

/// <summary>Where a sound plays: each bus has its own volume setting and its own stopping rules.</summary>
public enum SoundBus
{
    Music,
    Menu,
    /// <summary>One voice at a time per side: a new one replaces that side's and the centred one.</summary>
    Voice,
    Drum,
    /// <summary>The cabinet's coin channel: never stopped with the scene.</summary>
    Coin,
}

/// <summary>How a skin authored a sound: gain, pan (degrees, negative left), pitch (cents), silence before it.</summary>
public readonly record struct SoundSettings(float Gain = 1, float PanDegrees = 0, int PitchCents = 0, TimeSpan Delay = default);

/// <summary>
/// One sound file, or one stream of a container, that the engine decodes (any format its decoders read: ATRAC in NUB
/// banks, ADPCM, Ogg, WAV, ...). <paramref name="Stream"/> is 1-based within a container, 0 for a file's only stream.
/// The engine caches a sound by <see cref="Key"/>; <see cref="Settings"/> is asked once, when
/// the sound is first decoded (off the frame, so it may read the file).
/// </summary>
public sealed class SoundSource(string path, int stream = 0, string? label = null)
{
    public string Path { get; } = path;

    public int Stream { get; } = stream;

    /// <summary>What the logs call it (a bank cue's "SE_GAME#5").</summary>
    public string Label { get; } = label ?? (stream == 0 ? System.IO.Path.GetFileName(path) : $"{System.IO.Path.GetFileName(path)}#{stream}");

    public Func<SoundSettings>? Settings { get; init; }

    /// <summary>Tells apart sources of one file decoded with different settings (a pack's pitched copy of a sound).</summary>
    public string Variant { get; init; } = "";

    public (string Path, int Stream, string Variant) Key => (Path, Stream, Variant);
}

/// <summary>A sound to play and the bus it plays on.</summary>
public readonly record struct SoundCue(SoundSource Source, SoundBus Bus);

/// <summary>
/// A sound the engine asks a skin for, by the engine's name (<c>drum.don</c>, <c>combo.voice</c>, ...).
/// <paramref name="Side"/>: the drum it belongs to (0 left, 1 right) when two players' sounds are kept apart; null when
/// centred (one player, or panning off). <paramref name="Tone"/>: the player's chosen drum sound (an index of
/// <see cref="ISoundSet.Tones"/>). <paramref name="Variant"/>: a number the name takes (combo.voice: the combo).
/// <paramref name="Mode"/>: the mode being played when it changes the sound (<c>waiwai</c>), else null.
/// </summary>
public readonly record struct SoundRequest(string Name, int? Side = null, int Tone = 0, int Variant = 0, string? Mode = null);

/// <summary>
/// A skin's sounds. The game's comes from its sound banks (Waddamburo.Skins.Green); a skin of our own lists files and
/// their settings. The engine decides when a sound plays and decodes and mixes it; the skin says which sound it is.
/// </summary>
public interface ISoundSet
{
    /// <summary>The drum sounds a player can choose (their names, for a selector); a tone is an index into it.</summary>
    IReadOnlyList<string> Tones { get; }

    /// <summary>What plays for <paramref name="request"/>: none (silence), one or several sounds at once.</summary>
    IReadOnlyList<SoundCue> Describe(SoundRequest request);
}

/// <summary>A sound or track the player started, to ask about or stop.</summary>
public readonly record struct SoundHandle(long Value);

/// <summary>
/// The engine's sound player, for the skin's adapter: it decodes and mixes, keeps one voice at a time per side
/// (a centred voice replaces them all), and a sound cuts off its own previous play.
/// </summary>
public interface ISoundPlayer
{
    /// <summary>Plays <paramref name="cue"/>; <paramref name="trace"/> logs it (default: all but drum sounds).</summary>
    void Play(SoundCue cue, bool loop = false, bool? trace = null);

    /// <summary>Decodes a sound ahead of time (drum hits must not stall on first use).</summary>
    void Preload(SoundSource source);

    /// <summary>Streams a track on the music bus; null when it is unavailable.</summary>
    SoundHandle? PlayMusic(SoundSource music, bool loop);

    /// <summary>Decodes a track now and returns what starts it (replacing the last one started so); null when unavailable.</summary>
    Action? PrepareMusic(SoundSource music, bool loop);

    bool IsPlaying(SoundHandle? handle);

    /// <summary>Fades a sound or track out.</summary>
    void Stop(SoundHandle? handle);

    /// <summary>Every sound of <paramref name="path"/> still sounding stops (all the cues of one bank).</summary>
    void StopFile(string path);

    /// <summary>A voice is sounding, or waits to be decoded.</summary>
    bool IsVoicePlaying { get; }

    void StopVoice();

    /// <summary>Stops the voices when the latest one is the sound <paramref name="latest"/> picks.</summary>
    void StopVoiceIf(Func<SoundSource, bool> latest);

    /// <summary>The latest voice once more, unless a voice is sounding.</summary>
    void ReplayLatestVoice();

    /// <summary>Everything but the coin channel stops.</summary>
    void StopAll();
}

/// <summary>No sounds at all (no game sound data and no pack).</summary>
public sealed class SilentSoundSet : ISoundSet
{
    public IReadOnlyList<string> Tones { get; } = [];

    public IReadOnlyList<SoundCue> Describe(SoundRequest request) => [];
}
