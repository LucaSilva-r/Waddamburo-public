using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The game's own art and text the movies show in their native fills: name and text-field boards, drum tones, costume
/// icons, dan challenge plates, the boot's music credits, Waiwai's results art. The hosts name a fill by its key
/// (<c>DanChallengeArt.Key</c>, <c>CopyrightArt.Key</c>...); the frame asks <see cref="Resolve"/> for its texture, which
/// each part decodes from the user's NUT archives or draws the first time it is asked.
/// </summary>
internal sealed class NativeArt(SdlApplication application, string fontPath, string dataRoot) : IDisposable
{
    private readonly NutData _nut = new(application, dataRoot);
    private readonly TextFieldTextures _textFields = new(application, fontPath);
    private readonly NameTextTextures _nameTexts = new(application, fontPath);
    private ToneArt? _tones;
    private CostumeIconTextures? _costumeIcons;
    private DanChallengeArt? _danArt;
    private CopyrightArt? _copyright;
    private FeatureBoardArt? _featureBoards;
    private SeasonArt? _seasonArt;
    private WaiwaiResultTextures? _waiwaiResults;

    private ToneArt tones => _tones ??= new ToneArt(_nut);
    private CostumeIconTextures costumeIcons => _costumeIcons ??= new CostumeIconTextures(application, _nut, dataRoot);
    private DanChallengeArt danArt => _danArt ??= new DanChallengeArt(_nut, application, fontPath);
    private CopyrightArt copyright => _copyright ??= new CopyrightArt(_nut);
    private FeatureBoardArt featureBoards => _featureBoards ??= new FeatureBoardArt(_nut);
    private SeasonArt seasonArt => _seasonArt ??= new SeasonArt(_nut);
    private WaiwaiResultTextures waiwaiResults => _waiwaiResults ??= new WaiwaiResultTextures(_nut);

    /// <summary>The boot's music credit pages (the copyright NUT's).</summary>
    public int CopyrightPages => copyright.Pages;

    public RenderTextureId? Resolve(LumenNativeSurfaceKey surface) =>
        danArt.Resolve(surface) ?? seasonArt.Resolve(surface) ?? copyright.Resolve(surface) ?? featureBoards.Resolve(surface) ?? costumeIcons.Resolve(surface) ?? waiwaiResults.Resolve(surface)
        ?? _textFields.Resolve(surface) ?? _nameTexts.Resolve(surface) ?? tones.Resolve(surface);

    public void Dispose()
    {
        _textFields.Dispose();
        _nameTexts.Dispose();
    }
}
