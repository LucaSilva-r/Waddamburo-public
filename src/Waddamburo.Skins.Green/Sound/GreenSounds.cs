using System.Collections.Immutable;
using Waddamburo.Game.Sound;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Sound;

/// <summary>
/// The game's sounds as its movies and screens ask for them (bank cues, bgm/nub tracks), played by the engine's
/// <see cref="ISoundPlayer"/>.
/// </summary>
public sealed class GreenSounds(GreenSoundSet set, ISoundPlayer player)
{
    public GreenSoundSet Set { get; } = set;

    public ISoundPlayer Player { get; } = player;

    public bool TryGetBankName(int bankId, out string name) => Set.TryGetBankName(bankId, out name);

    /// <summary>Plays a bank cue: VO_ banks on the voice bus (replacing the current voice), others as menu sounds.</summary>
    public void Play(string bank, int cue, SoundBus? bus = null, bool loop = false, bool trace = true) =>
        Player.Play(new(Set.Cue(bank, cue), bus ?? GreenSoundSet.BusOf(bank)), loop, trace);

    /// <summary>Starts a bgm/nub track; null when it is unavailable.</summary>
    public SoundHandle? PlayMusic(string name, bool loop) => Player.PlayMusic(Set.Music(name), loop);

    /// <summary>A tutorial's music, decoded now (JINGLE_SILENCE plays once: it is the silence between tracks).</summary>
    public Action? PrepareTutorialMusic(string name) =>
        Path.GetFileName(name) != name ? null : Player.PrepareMusic(Set.Music(name), loop: name != "JINGLE_SILENCE");

    /// <summary>Every cue of <paramref name="bank"/> still sounding stops.</summary>
    public void StopBank(string bank) => Player.StopFile(Set.Cue(bank, 0).Path);

    /// <summary>The movies' StopVoice(cue): stops the voice when the latest is that cue (of any bank: the call has none).</summary>
    public void StopVoice(int cue) => Player.StopVoiceIf(latest => latest.Stream - 1 == cue);

    public void StopVoice() => Player.StopVoice();

    public void ReplayLatestVoice() => Player.ReplayLatestVoice();

    public bool IsPlaying(SoundHandle? handle) => Player.IsPlaying(handle);

    public void Stop(SoundHandle? handle, TimeSpan? fade = null) => Player.Stop(handle, fade);

    /// <summary>A whole-number argument of a movie's sound call.</summary>
    public static bool TryInteger(ImmutableArray<LumenHostValue> arguments, int index, out int result)
    {
        result = 0;
        if ((uint)index >= (uint)arguments.Length || arguments[index].Kind != LumenHostValueKind.Number)
            return false;
        var value = arguments[index].AsNumber();
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
            return false;
        result = (int)value;
        return true;
    }

    public static void TraceUnmapped(string kind, ImmutableArray<LumenHostValue> arguments) =>
        Console.WriteLine($"Lumen.{kind}({string.Join(", ", arguments.Select(HostValueText.Format))}) [unmapped]");
}
