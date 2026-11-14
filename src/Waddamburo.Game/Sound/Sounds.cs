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
/// The engine caches a sound by <see cref="Path"/> and <see cref="Stream"/>; <see cref="Settings"/> is asked once, when
/// the sound is first decoded (off the frame, so it may read the file).
/// </summary>
public sealed class SoundSource(string path, int stream = 0, string? label = null)
{
    public string Path { get; } = path;

    public int Stream { get; } = stream;

    /// <summary>What the logs call it (a bank cue's "SE_GAME#5").</summary>
    public string Label { get; } = label ?? (stream == 0 ? System.IO.Path.GetFileName(path) : $"{System.IO.Path.GetFileName(path)}#{stream}");

    public Func<SoundSettings>? Settings { get; init; }

    public (string Path, int Stream) Key => (Path, Stream);
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
