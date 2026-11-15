using Waddamburo.Game.Sound;

namespace Waddamburo.Game.Tests;

public sealed class SoundPackTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sound-pack").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private SoundPack load(string json, ISoundSet? fallback = null)
    {
        File.WriteAllText(Path.Combine(_directory, "sounds.json"), json);
        return SoundPack.Load(_directory, fallback)!;
    }

    private sealed class Below : ISoundSet
    {
        public IReadOnlyList<string> Tones { get; } = ["below"];
        public IReadOnlyList<SoundCue> Describe(SoundRequest request) => [new(new SoundSource("/below/" + request.Name), SoundBus.Menu)];
    }

    [Fact]
    public void ARequestTakesTheMostParticularEntryAndElseAsksTheSkinBelow()
    {
        var pack = load("""
            { "tones": ["Taiko", "High"], "sounds": {
                "drum.don": { "file": "don.wav" },
                "drum.don@tone1": { "file": "don.wav", "pitch": 500 },
                "drum.don@side1": { "file": "don.wav", "pan": 30 },
                "combo.voice@100": { "file": "100.wav" } } }
            """, new Below());
        string file(SoundRequest request) => Path.GetFileName(pack.Describe(request).Single().Source.Path);
        var plain = pack.Describe(new("drum.don")).Single();
        var high = pack.Describe(new("drum.don", Tone: 1)).Single();
        Assert.Equal(SoundBus.Drum, plain.Bus);
        Assert.Equal(500, high.Source.Settings!().PitchCents);
        // One file, two settings: decoded apart.
        Assert.NotEqual(plain.Source.Key, high.Source.Key);
        Assert.Equal(30, pack.Describe(new("drum.don", Side: 1)).Single().Source.Settings!().PanDegrees);
        Assert.Equal(500, pack.Describe(new("drum.don", Side: 1, Tone: 1)).Single().Source.Settings!().PitchCents);
        Assert.Equal("100.wav", file(new("combo.voice", Variant: 100)));
        Assert.Equal("banner.clear", file(new("banner.clear")));
        Assert.Equal(["Taiko", "High"], pack.Tones);
        Assert.Equal(["below"], load("""{ "sounds": {} }""", new Below()).Tones);
        Assert.Null(SoundPack.Load(Path.Combine(_directory, "none")));
    }

    [Fact]
    public void AFileOutsideThePacksFolderIsRefused() =>
        Assert.Throws<InvalidDataException>(() => load("""{ "sounds": { "ui.don": { "file": "../elsewhere.wav" } } }"""));
}
