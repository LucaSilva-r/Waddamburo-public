using System.Collections.Immutable;
using Waddamburo.Catalog;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Tests;

public sealed class SongSelectVoiceTests
{
    private static readonly string[] HostNames = ["", "Lumen"];
    [Fact]
    public async Task BackoutVoiceSurvivesTheClosingBoardsDuplicateDifficultyPrompts()
    {
        using var catalog = new GlobalSongCatalog([new Provider()]);
        var view = new SongSelectCatalogView(await catalog.RefreshAsync());
        var sounds = new Sounds();
        var direct = new List<(string Bank, int Cue)>();
        using var host = new SongSelectHostBinding(new SongSelectSession(view, new Textures(), new Previews(), new PlayRequestState()), sounds)
        {
            PlayCue = (bank, cue) => direct.Add((bank, cue)),
        };
        var definition = movie(
            ("NotifyBeginCourseSelect", [0, 0]),
            ("RequestSE", [5, 33]), // choose a difficulty while opening the real song's board
            ("RequestSE", [5, 32]), // backout: choose a song
            ("RequestSE_Save", [0, 5, 61]),
            ("RequestSE_Save", [0, 5, 61]),
            ("RequestSE", [5, 61]), // the ordinary request path follows the same closing policy
            ("RequestSE_Save", [0, 7, 2]), // tone previews keep their special route
            ("NotifyBeginCourseSelect", [0, 0]), // another real song's board, no longer closing
            ("RequestSE", [5, 33]),
            ("RequestSE_Save", [0, 5, 61]));
        var player = new LumenPlayer(definition, 1280, 720, rootCharacterId: 1, hostBinding: host);
        host.Attach(player);
        for (var frame = 0; frame < 7; frame++) player.Advance();
        Assert.True(host.LeavingCourseSelect);
        Assert.Equal([("VO_SELECT", 32)], direct);
        Assert.Equal(2, sounds.Requests.Count);
        Assert.Equal(33, sounds.Requests[0].Arguments[1].AsNumber());
        Assert.Equal(SongSelectSoundRequestKind.TonePreview, sounds.Requests[1].Kind);
        Assert.Equal([7d, 2d], sounds.Requests[1].Arguments.Select(static value => value.AsNumber()));

        for (var frame = 0; frame < 3; frame++) player.Advance();
        Assert.False(host.LeavingCourseSelect);
        Assert.Equal([33d, 61d], sounds.Requests.Skip(2).Select(static request => request.Arguments[1].AsNumber()));
    }

    [Fact]
    public async Task OrdinaryAndPerPlayerDifficultyPromptsRemainAvailableOutsideBackout()
    {
        using var catalog = new GlobalSongCatalog([new Provider()]);
        var sounds = new Sounds();
        using var host = new SongSelectHostBinding(new SongSelectSession(new SongSelectCatalogView(await catalog.RefreshAsync()),
            new Textures(), new Previews(), new PlayRequestState()), sounds);
        var player = new LumenPlayer(movie(("RequestSE", [5, 32]), ("RequestSE", [5, 61]), ("RequestSE_Save", [0, 5, 61])),
            1280, 720, rootCharacterId: 1, hostBinding: host);
        host.Attach(player);
        for (var frame = 0; frame < 3; frame++) player.Advance();
        Assert.Equal([32d, 61d, 61d], sounds.Requests.Select(static request => request.Arguments[1].AsNumber()));
    }

    private static LmbMovieDefinition movie(params (string Name, int[] Arguments)[] calls)
    {
        var names = HostNames.Concat(calls.Select(static call => call.Name)).Distinct().ToArray();
        Avm1Instruction op(byte code, Avm1Operand? operand = null) => new(code, 0, 1, [], operand, null, null);
        Avm1Instruction push(params Avm1PushValue[] values) => op(0x96, new Avm1PushOperand([.. values]));
        Avm1PushStringValue name(string value) => new(8, (ushort)Array.IndexOf(names, value));
        var actions = calls.Select((call, index) =>
        {
            var instructions = new[]
            {
                push([.. call.Arguments.Reverse().Select(static value => new Avm1PushIntegerValue(value))]),
                push(new Avm1PushIntegerValue(call.Arguments.Length), name("Lumen")), op(0x1C),
                push(name(call.Name)), op(0x52), op(0x17), op(0),
            };
            return new LmbAction(index, [], 0)
            {
                Code = new Avm1CodeBlock(0, instructions.Length, [.. instructions.Select((instruction, offset) => instruction with { Offset = offset })]),
            };
        }).ToImmutableArray();
        var frames = new List<LmbTimelineCommand> { new LmbShowFrameCommand(0, 0, [], null!) };
        for (var index = 0; index < calls.Length; index++)
        {
            frames.Add(new LmbShowFrameCommand((uint)index + 1, 1, [], null!));
            frames.Add(new LmbDoActionCommand((uint)index, 0, [], null!));
        }
        return new LmbMovieDefinition(null!, null, [.. names.Select((value, index) => new LmbString(index, value, 0))],
            [], [], [], [], [], actions, [], [new LmbSpriteDefinition(1, 0, (uint)calls.Length + 1, 0, 0, [], [.. frames], null!)], []);
    }

    private sealed class Sounds : ISongSelectSoundController
    {
        public List<SongSelectSoundRequest> Requests { get; } = [];
        public void RequestSound(SongSelectSoundRequest request) => Requests.Add(request);
        public void SelectCategoryVoice(string category) { }
        public void StopVoice() { }
    }

    private sealed class Provider : ISongCatalogProvider
    {
        public CatalogProviderId Id { get; } = new("synthetic-voices");
        public SongSourceKind Source => SongSourceKind.Stock;
        public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
        {
            var key = new SongKey(Source, "synthetic");
            var chart = new SongChartDescriptor(new ChartKey(key, "oni"), "Oni", new CatalogAssetKey(Id, "chart"), TaikoCourse.Oni, 7);
            return ValueTask.FromResult(new SongCatalogContribution(Id, Source,
                [new SongDescriptor(key, new SongTitle("Synthetic"), null, [chart])],
                [new SongCategoryDescriptor(new CategoryKey(Source, "anime"), "Anime", 0, [key])]));
        }
    }

    private sealed class Previews : ISongPreviewController
    {
        public void SetPreview(SongPreviewRequest? request) { }
        public void Silence() { }
        public void Hold() { }
    }

    private sealed class Textures : ISongBoardTextureService
    {
        public LumenNativeSurfaceKey GetSongTitle(SongSelectSong song, SongBoardTextureStyle style, SongBoardTextureKind kind) => new("synthetic");
        public LumenNativeSurfaceKey GetFolderName(string name, SongBoardTextureKind kind, uint outlineRgb = 0) => new("synthetic");
        public LumenNativeSurfaceKey GetImage(string path) => new("synthetic");
        public LumenNativeSurfaceKey GetFolderArt(FolderArt art, FolderArtPart part) => new("synthetic");
        public LumenNativeSurfaceKey GetFolderDescription(string lines, uint outlineRgb) => new("synthetic");
    }
}
