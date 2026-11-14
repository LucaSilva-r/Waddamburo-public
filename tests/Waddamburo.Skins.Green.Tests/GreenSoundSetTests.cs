using Waddamburo.Game.Sound;
using Waddamburo.Skins.Green.Sound;

namespace Waddamburo.Skins.Green.Tests;

public sealed class GreenSoundSetTests
{
    private readonly GreenSoundSet _set = new(Path.Combine(Path.GetTempPath(), "no-sound-tree"));

    private string describe(SoundRequest request) =>
        string.Join(" + ", _set.Describe(request).Select(static cue => $"{cue.Source.Label} {cue.Bus}"));

    [Fact]
    public void ADrumSoundsInItsPlayersToneOnItsSide()
    {
        Assert.Equal("SE_GAME_NEIRO_000_C#0 Drum", describe(new("drum.don")));
        Assert.Equal("SE_GAME_NEIRO_003_R#1 Drum", describe(new("drum.ka", Side: 1, Tone: 3)));
        Assert.Equal("SE_GAME_NEIRO_100_L#0 Drum", describe(new("drum.don", Side: 0, Tone: 2, Mode: "waiwai")));
    }

    [Fact]
    public void APlayersOwnCuesFollowTheOnePlayerCue()
    {
        Assert.Equal("VO_GAME#0 Voice", describe(new("combo.voice", Variant: 50)));
        Assert.Equal("VO_GAME#4 Voice", describe(new("combo.voice", Side: 0, Variant: 100)));
        Assert.Equal("SE_GAME#14 Menu + VO_GAME#155 Voice", describe(new("banner.fullCombo", Side: 1)));
        Assert.Equal("", describe(new("no.such.sound")));
    }
}
