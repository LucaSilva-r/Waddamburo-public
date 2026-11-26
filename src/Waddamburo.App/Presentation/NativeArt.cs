using Waddamburo.Lumen.Rendering;
using Waddamburo.Skins.Green.Art;
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
    private readonly NutData _nut = new(texture => SceneTextures.UploadOne(application, texture), dataRoot);
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

    // The art decoded from NUTs by key (dan plates, seasons, copyright, feature boards, costumes, Waiwai, tones): each part
    // parses its keys (strings) and the frame asks every frame. Text is drawn at the window's scale, so it asks each time.
    // ponytail: never evicted; the parts keep their textures for good anyway.
    private readonly Dictionary<LumenNativeSurfaceKey, RenderTextureId> _art = [];

    /// <summary>Whether this Green has a feature folder art ("release:index"); see <see cref="FeatureBoardArt.Has"/>.</summary>
    public bool HasFeatureBoard(string art) => featureBoards.Has(art);

    public RenderTextureId? Resolve(LumenNativeSurfaceKey surface)
    {
        if (_art.TryGetValue(surface, out var known))
            return known;
        if ((danArt.Resolve(surface) ?? seasonArt.Resolve(surface) ?? copyright.Resolve(surface) ?? featureBoards.Resolve(surface)
            ?? costumeIcons.Resolve(surface) ?? waiwaiResults.Resolve(surface) ?? tones.Resolve(surface)) is { } art)
            return _art[surface] = art;
        return _textFields.Resolve(surface) ?? _nameTexts.Resolve(surface);
    }

    public void Dispose()
    {
        _textFields.Dispose();
        _nameTexts.Dispose();
    }
}
