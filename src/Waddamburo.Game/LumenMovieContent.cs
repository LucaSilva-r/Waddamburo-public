using System.Collections.Immutable;
using Waddamburo.Formats;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats.IO;
using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Nut;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game;

/// <summary>Source-independent, CPU-owned content for one archive movie.</summary>
public sealed class LumenMovieContent
{
    private LumenMovieContent(
        string name,
        LmbMovieDefinition definition,
        ImmutableArray<LumenTextureContent> textures,
        ImmutableArray<ParseDiagnostic> diagnostics)
    {
        Name = name;
        Definition = definition;
        Textures = textures;
        Diagnostics = diagnostics;
    }

    public string Name { get; }

    public LmbMovieDefinition Definition { get; }

    public ImmutableArray<LumenTextureContent> Textures { get; }

    public ImmutableArray<ParseDiagnostic> Diagnostics { get; }

    public static LumenMovieContent Load(DdpMovieView movie, ParserLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var parserLimits = limits ?? ParserLimits.Default;
        var textures = ImmutableArray.CreateBuilder<LumenTextureContent>(movie.Textures.Length);
        foreach (var view in movie.Textures)
        {
            var pack = NutFile.Parse(view.Data, parserLimits);
            if (pack.Textures.Length != 1)
            {
                throw new FormatReadException(
                    $"Movie texture entry {view.Entry.Index} contains {pack.Textures.Length} NTP3 textures; the initial Green profile requires exactly one",
                    view.Entry.Offset);
            }
            var texture = pack.Textures[0];
            textures.Add(new LumenTextureContent(
                view.Entry.Index - movie.Entry.TextureBegin,
                texture.Width,
                texture.Height,
                ImmutableArray.CreateRange(NutTextureDecoder.DecodeRgba8(texture, parserLimits))));
        }

        var file = LmbFile.Parse(movie.Data, parserLimits);
        var semantic = LmbSemanticReader.Read(
            file,
            parserLimits,
            new LmbSemanticValidationContext(textures.Count));
        return new LumenMovieContent(
            movie.Entry.Name,
            semantic.Value,
            textures.MoveToImmutable(),
            semantic.Diagnostics);
    }

    public LumenPlayer CreatePlayer(float stageWidth = 1280, float stageHeight = 720) =>
        new(Definition, stageWidth, stageHeight);
}

public sealed record LumenTextureContent(
    int Index,
    int Width,
    int Height,
    ImmutableArray<byte> Rgba8);
