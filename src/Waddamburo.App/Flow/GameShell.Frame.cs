using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.App.Home;

namespace Waddamburo.App.Flow;

// One displayed frame: the scene, the overlays drawn with it, and what is drawn on top of everything.
internal sealed partial class GameShell
{
    /// <summary>
    /// A moved camera's surroundings (TJA #BORDERCOLOR, default black): one big band past each edge of the 1280x720
    /// stage, through the camera, so whatever lies off the stage stays hidden however it is turned or zoomed out.
    /// </summary>
    private IEnumerable<RenderQuad> CameraBorder(LumenMatrix camera, RenderColor colour)
    {
        const float far = 100_000;
        foreach (var (x, y, width, height) in (IEnumerable<(float, float, float, float)>)[
            (-far, -far, far, 720 + 2 * far), (1280, -far, far, 720 + 2 * far), (0, -far, 1280, far), (0, 720, 1280, far)])
        {
            var quad = Painter.Rect(x, y, width, height, colour);
            RenderVertex moved(RenderVertex vertex) => camera.Transform(vertex.X * 1280, vertex.Y * 720) is var point
                ? vertex with { X = point.X / 1280, Y = point.Y / 720 } : vertex;
            yield return quad with { TopLeft = moved(quad.TopLeft), TopRight = moved(quad.TopRight),
                BottomRight = moved(quad.BottomRight), BottomLeft = moved(quad.BottomLeft) };
        }
    }

    private LumenRenderSnapshot? _inspected;

    // Inspect mode: every scene quad under the click, topmost first (the stage is assumed to fill the window).
    private void inspectAt(float x, float y)
    {
        if (!LumenPlayer.Inspect || _inspected is not { } scene)
            return;
        float px = x * scene.StageWidth, py = y * scene.StageHeight;
        static float cross(LumenRenderVertex a, LumenRenderVertex b, float px, float py) =>
            (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);
        var hits = scene.Quads.Where(quad => quad.MaskOperation == LumenRenderMaskOperation.Draw).Where(quad =>
        {
            var edges = new[]
            {
                cross(quad.TopLeft, quad.TopRight, px, py), cross(quad.TopRight, quad.BottomRight, px, py),
                cross(quad.BottomRight, quad.BottomLeft, px, py), cross(quad.BottomLeft, quad.TopLeft, px, py),
            };
            return edges.All(static edge => edge >= 0) || edges.All(static edge => edge <= 0);
        }).Reverse().ToArray();
        Console.WriteLine($"Inspect at stage ({px:0}, {py:0}): {hits.Length} quads, topmost first.");
        foreach (var quad in hits)
            Console.WriteLine($"  {quad.Source ?? "?"}{(quad.NativeSurface is { } surface ? $" -> surface {surface.Value}" : $" (scene texture {quad.TextureIndex})")}");
    }

    // A decoded movie: cached upscales, then its Lumen patch. The layout check comes first (upscales change
    // texture sizes); a patched movie keeps its upscales as RGBA (the patch derives textures from them) and
    // its derived textures are not upscaled again (a split layer upscaled alone gets sharpened edges).
    public RenderTextureId? ResolveSurface(LumenNativeSurfaceKey surface) =>
        _attract.Movie?.Resolve(surface) ?? Don?.Resolve(surface)
        ?? _art.Resolve(surface)
        ?? Titles.Resolve(surface);

    // One displayed frame. Depth order (traced): scene, msg_coins (-950), intermission (-2000),
    // network/card (-3000); then Waddamburo's own overlays and the home menu over everything.
    private readonly List<RenderQuad> _frameQuads = [];

    /// <summary>The displayed scene's Lumen quads, reused each frame (a snapshot lasts until the next).</summary>
    public LumenQuadBuffer SceneQuads { get; } = new();

    // The scene's Don-chans (their native surfaces) and its name board layers, on their own.
    private System.Collections.Immutable.ImmutableArray<RenderQuad> players(LumenRenderSnapshot scene, float interpolation)
    {
        var dons = scene.Quads.Where(quad => quad.NativeSurface is { } surface && Don?.Resolve(surface) is not null);
        var boards = Active.Layers.Select((layer, index) => (layer, index))
            .Where(entry => Path.GetFileNameWithoutExtension(entry.layer.Definition.MovieId) == "player_name")
            .Select(entry => Active.Player.Layers[entry.index]);
        var boardQuads = new LumenScenePlayer(scene.StageWidth, scene.StageHeight, boards).CreateRenderSnapshot(interpolation).Quads;
        return SceneTextures.Compose(new LumenRenderSnapshot(scene.StageWidth, scene.StageHeight, [.. dons, .. boardQuads]),
            _presenter.Textures, "Players", ResolveSurface).Quads;
    }

    private RenderFrame createFrame(double interpolationFraction)
    {
        if (_service is { IsOpen: true } service && !Arcade.Home)
        {
            // The menu's frame is the one shown: the switch under it lets go of its held frame, and the switch out
            // of it (EXIT) holds this one, not the scene from before the menu.
            _presenter.Held(Tick);
            var menu = service.Frame();
            _presenter.Presented(menu, withIntermission: false);
            return menu;
        }
        // Freeze animations (the editors): everything stands on one tick, Don and the overlays too.
        var interpolation = Editor.FreezeAnimations && Editor.IsOpen ? 1 : _home.Interpolation(interpolationFraction);
        if (_presenter.Held(Tick) is { } held)
        {
            _home.HoldBlack();
            // The rainbow stays up across its scene switch (only a fade is cleared with it).
            return _home.Draw(held.Black && Overlay.IsShown
                ? new RenderFrame(held.Frame.ClearColor,
                    [.. Overlay.Quads(interpolation, Titles.Resolve), .. held.Frame.Quads], held.Frame.ContentAspectRatio)
                : held.Frame);
        }
        Titles.UploadCompleted();
        _presenter.UploadAhead();
        if (SceneTextures.Upscaler is { } upscaler)
        {
            upscaler.Paused = Scenes.OnLane || Bake?.Running == true;
            upscaler.Apply(Application);
        }
        if (_libraries.MetadataCache is { } metadataCache)
            // Not during the attract demo: its notice shows there, and a held check stayed on screen the whole demo.
            metadataCache.Paused = Scenes.OnLane && !_gameplay.Demo;
        if (DonRenderer is not null)
            DonRenderer.Interpolation = interpolation;
        var scene = flowOf(Active.Id).CreateSnapshot(interpolation);
        if (LumenPlayer.Inspect)
            _inspected = scene.Detached(); // read at a click, frames later
        // The scene's quads go straight into the reused frame list (no frame of their own).
        var quads = _frameQuads;
        quads.Clear();
        SceneTextures.ComposeInto(scene, quads, _presenter.Textures, "Scene", ResolveSurface);
        IEnumerable<RenderQuad> indicatorQuads(bool overIntermission) => _indicators is null ? []
            : Unsqueezed(SceneTextures.Compose(_indicators.CreateSnapshot(overIntermission, interpolation), _indicatorTextures,
                "Indicator", Titles.Resolve).Quads);
        if (Active.Id == FlowScenes.Gameplay)
        {
            if (Gameplay.Camera is { } camera)
                quads.AddRange(CameraBorder(camera.Matrix, new RenderColor(camera.Red, camera.Green, camera.Blue, 1)));
            quads.AddRange(_timingMarks.Quads(Gameplay.TimingMarks, Arcade.TimingIndicator));
            if (Arcade.Lyrics && Gameplay.Lyric is { } lyric)
                quads.AddRange(LyricsOverlay.Quads(Painter, lyric, Arcade));
        }
        if (!Editor.IsOpen && _gameplay.Reviewing && _gameplay.Review is { } review)
            quads.AddRange(_reviewBar.Quads(review, _gameplay.Training, _gameplay.AutoplayReview && _gameplay.Training is null));
        if (_playerSetup is not null)
            quads.AddRange(_playerSetup.Quads(interpolation));
        quads.AddRange(indicatorQuads(false));
        quads.AddRange(Overlay.Quads(interpolation, Titles.Resolve));
        // The online lobby shades (frosts) Song Select, all but its Don-chans and name boards: those come again over it.
        var frosted = 0;
        if (Room.ShadesScene && Active.Id == FlowScenes.SongSelect)
        {
            frosted = quads.Count;
            quads.AddRange(Room.Shade());
            quads.AddRange(players(scene, interpolation));
        }
        foreach (var overlay in _inputOverlays)
            if (overlay != _search)
                quads.AddRange(overlay.Quads());
        quads.AddRange(indicatorQuads(true));
        quads.AddRange(pill());
        if (_notices is not null)
            // A replay being watched shows its toasts (a report's answer, a sanction); a song being played holds them.
            quads.AddRange(_notices.Quads(Scenes.OnLane && !_gameplay.Demo && !(_gameplay.Reviewing && !_gameplay.Attempting)));
        // Song search is a menu page: everything under it frosted, as the settings are.
        if (_search.IsOpen)
        {
            frosted = quads.Count;
            quads.AddRange(_search.Quads());
        }
        // Sign in's overlay as well: a menu page over the frosted entry.
        if (_playerSetup is { SignInOpen: true })
        {
            frosted = quads.Count;
            quads.AddRange(_playerSetup.SignInQuads());
        }
        // The frame reads the reused list without a copy: valid until the next one (the presenter detaches what it holds).
        var result = RenderFrame.Shared(RenderColor.Black, quads, (double)scene.StageWidth / scene.StageHeight,
            blurredQuads: frosted, backdropBrightness: HomeControls.BackdropBrightness);
        _presenter.Presented(result, Overlay.IsShown);
        Painter.EndFrame();
        return _home.Draw(result);
    }

    /// <summary>
    /// A 4:3 layout squeezes the stage to 3/4 of its width: what isn't part of the layout is widened back by 4/3 to keep
    /// its shape, from its side's edge (the corner indicators: network, coins) or about the centre (the menus).
    /// </summary>
    public IEnumerable<RenderQuad> Unsqueezed(IEnumerable<RenderQuad> quads, bool centred = false)
    {
        if (!Application.Squeeze43)
            return quads;
        RenderVertex v(RenderVertex vertex) => vertex with
        {
            X = centred ? .5f + (vertex.X - .5f) * 4 / 3 : vertex.X >= .5f ? 1 - (1 - vertex.X) * 4 / 3 : vertex.X * 4 / 3,
        };
        return quads.Select(quad => quad with
            { TopLeft = v(quad.TopLeft), TopRight = v(quad.TopRight), BottomRight = v(quad.BottomRight), BottomLeft = v(quad.BottomLeft) });
    }

    // Drawn last over every frame (the pause menu, the service menu, a held frame), never frosted with it.
    private RenderFrame onTop(RenderFrame frame)
    {
        var top = new List<RenderQuad>();
        if (BuildInfo.Nightly)
        {
            // A song too: a nightly's plays never reach TaikOnline (ScoreClient.IsDevVersion).
            top.Add(Painter.Rect(400, 0, 480, 26, new RenderColor(0.55f, 0.12f, 0.1f, 0.85f)));
            top.Add(Painter.Text(Strings.T("nightly.banner"), 640, 13, 468, 22));
        }
        var bannerY = BuildInfo.Nightly ? 28 : 0;
        // A phone build left behind by a release: off TaikOnline until updated (the page opens from Settings › Others).
        if (Online.UpdateGate.Newer is { } newer && Active.Id != FlowScenes.Gameplay)
        {
            top.Add(Painter.Rect(330, bannerY, 620, 26, new RenderColor(0.55f, 0.32f, 0.05f, 0.85f)));
            top.Add(Painter.Text(Strings.T("update.banner", newer.Tag), 640, bannerY + 13, 608, 22));
            bannerY += 28;
        }
        if (uploadBanner() is { } uploadMessage)
        {
            top.Add(Painter.Rect(380, bannerY, 520, 26, new RenderColor(0.55f, 0.12f, 0.1f, 0.85f)));
            top.Add(Painter.Text(Strings.T(uploadMessage), 640, bannerY + 13, 508, 22));
            bannerY += 28;
        }
        // A song without its music (a withdrawn one whose newer data kept only a placeholder): no preview, a silent play.
        if (Active.Id == FlowScenes.SongSelect
            && (Loaded.SongSelect?.CourseSelectSong ?? Loaded.SongSelect?.SongUnderCursor) is { Descriptor.AudioAsset: null })
        {
            top.Add(Painter.Rect(380, bannerY, 520, 26, new RenderColor(0.45f, 0.32f, 0.05f, 0.85f)));
            top.Add(Painter.Text(Strings.T("song_select.no_music"), 640, bannerY + 13, 508, 22));
        }
        top.AddRange(_performance.Quads());
        // Settings > Game Options > Lyrics: a sample line over the menu, as the settings stand.
        if (_service?.LyricsPreview is { } lyricsLook)
            top.AddRange(LyricsOverlay.Quads(Painter, Strings.T("service.lyrics.sample"), lyricsLook));
        // Touch screens: the menu's buttons while an overlay that takes the controls is open (settings, pause, search,
        // scores, an online room), else the drum and, in Song Select, the search button. The back button
        // always: touch_drum (auto: until a controller or keys play; off) governs the drum alone, so a player without a
        // controller still reaches every menu.
        Application.TouchDrumEnabled = Arcade.TouchDrum != TouchDrumMode.Off;
        // Player setup is played with the drum itself (rims choose, the face picks): not an overlay here.
        Application.OverlayOpen = HomeMenuOpen || _service is { IsOpen: true } || _inputOverlays.Any(static overlay => overlay.IsOpen)
            || _playerSetup is { SignInOpen: true };
        Application.TouchSearch = !Application.OverlayOpen && Active.Id == FlowScenes.SongSelect;
        // As the notices key: not over a lane (the attract's demo excepted), nor while search takes the letters.
        Application.TouchNotices = !Application.OverlayOpen && _notices is not null && (!Scenes.OnLane || _gameplay.Demo) && !_search.IsOpen;
        Application.TouchKeyboard = PlatformInfo.IsMobile && _search.IsOpen;
        if (PlatformInfo.IsMobile || Application.TouchActive)
        {
            var (hits, now) = (Application.TouchHits, SdlApplication.TicksNs);
            var attractOrBoot = _gameplay.Demo && Scenes.OnLane || Active.Id == FlowScenes.Boot || Active.Id == FlowScenes.Logo
                || Active.Id == FlowScenes.Title || Active.Id == FlowScenes.Caution || Active.Id == FlowScenes.Movie;
            if (Application.OverlayOpen)
                top.AddRange(TouchDrumOverlay.MenuQuads(Painter, hits, now));
            if (Application.TouchKeyboard)
                top.Add(TouchDrumOverlay.KeyboardQuad(Painter, hits, now));
            // Not under an overlay: its touches go to the menu buttons, the drum would only cover the page.
            // Not drawn over the boot and the attract (its demo play too): a touch anywhere there goes to the entry.
            else if (!Application.OverlayOpen && !attractOrBoot && (Arcade.TouchDrum == TouchDrumMode.On || Arcade.TouchDrum == TouchDrumMode.Auto && Application.TouchActive))
                top.AddRange(TouchDrumOverlay.Quads(Painter, hits, now));
            if (Application.TouchSearch)
                top.Add(TouchDrumOverlay.SearchQuad(Painter, hits, now));
            top.Add(TouchDrumOverlay.BackQuad(Painter, hits, now));
            if (Application.TouchNotices)
                top.Add(TouchDrumOverlay.NoticesQuad(Painter, hits, now));
        }
        if (top.Count == 0)
            return frame;
        return new RenderFrame(frame.ClearColor, frame.Quads.AddRange(top), frame.ContentAspectRatio)
        {
            BlurredQuads = frame.BlurredQuads,
            BackdropBrightness = frame.BackdropBrightness,
        };
    }

    private string? _uploadBanner;
    private string? _uploadBannerContext;
    private int _uploadBannerTick = -60;

    private string? uploadBanner()
    {
        var song = Active.Id == FlowScenes.SongSelect
            ? Loaded.SongSelect?.CourseSelectSong ?? Loaded.SongSelect?.SongUnderCursor : null;
        var request = Active.Id == FlowScenes.Gameplay || Active.Id == FlowScenes.Result ? _gameplay.CurrentRequest : null;
        var auto = (Autoplay || DevAutoplay) && (Active.Id == FlowScenes.SongSelect || Active.Id == FlowScenes.Gameplay || Active.Id == FlowScenes.Result);
        var context = $"{Active.Id}:{song?.Descriptor.Key}:{request?.Song}:{Arcade.UploadCustomSongs}:{Arcade.UploadBanner}:{auto}:{_gameplay.Demo}";
        // Marker checks touch the filesystem; refresh once a second and immediately when the context changes.
        if (_uploadBannerContext == context && Tick - _uploadBannerTick < 60) return _uploadBanner;
        (_uploadBannerContext, _uploadBannerTick) = (context, Tick);
        // The attract's demo play uploads nothing and says nothing about it.
        if (!Arcade.UploadBanner || _gameplay.Demo) return _uploadBanner = null;
        if (auto) return _uploadBanner = "custom_upload.banner_auto";
        var source = song?.Descriptor.Key.Source ?? request?.Song.Source
            ?? (Active.Id == FlowScenes.SongSelect ? SelectMemory.Library : (SongSourceKind?)null);
        if (source is not (SongSourceKind.Tja or SongSourceKind.OsuLazer or SongSourceKind.OsuStable))
            return _uploadBanner = null;
        if (!Arcade.UploadCustomSongs) return _uploadBanner = "custom_upload.banner_all";
        var blocked = request is not null ? _gameplay.UploadBlocked
            : song?.Descriptor.Charts.Any(chart => Assets.UploadBlocked(chart.ChartAsset)) == true;
        return _uploadBanner = blocked ? "custom_upload.banner_chart" : null;
    }

    private RenderFrame blackLoadingFrame()
    {
        var network = _indicators is null ? []
            : SceneTextures.Compose(_indicators.CreateNetworkSnapshot(1), _indicatorTextures,
                "Network", Titles.Resolve).Quads;
        return new RenderFrame(RenderColor.Black, network, 1280d / 720d);
    }

    private IEnumerable<RenderQuad> pill()
    {
        if (Sync.Pairing is not { } pairing)
            return _pill.Quads(); // home: the account picker shows and hides it
        // The game takes cards in the attract loop (its demo play too) and in entry (not from song select on), one at
        // a time.
        var entry = flowOf(Active.Id) is EntryFlow;
        pairing.Accepting = Active.Id != FlowScenes.Boot && flowOf(Active.Id) is AttractFlow or GameplayFlow { Demo: true }
            || entry && !(Loaded.Entry?.CardBusy == true);
        if (entry && pairing.TakeCardFailure())
            Loaded.Entry?.RejectCard();
        if (entry && pairing.TakeCard() is { } card)
        {
            // A card already on a drum: the game reads it and refuses it (このデータはすでに読みこんでいます).
            if (TaikoGuest.Profiles.Any(profile => profile?.Baid == card.Baid))
            {
                Console.WriteLine($"Pairing: {card.Name} (baid {card.Baid}) is already playing; the card was refused.");
                Loaded.Entry?.RefuseCardInUse();
            }
            else if (Loaded.Entry?.InsertCard(card) != true)
                Console.WriteLine("Pairing: the entry is busy with another card; this one was dropped.");
        }
        pairing.UpdatePill(_pill);
        return _pill.Quads();
    }
}
