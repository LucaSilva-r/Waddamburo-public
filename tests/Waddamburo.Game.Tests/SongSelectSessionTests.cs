using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class SongSelectSessionTests
{
    [Theory]
    [InlineData(0, TaikoCourse.Easy)]
    [InlineData(1, TaikoCourse.Normal)]
    [InlineData(2, TaikoCourse.Hard)]
    [InlineData(3, TaikoCourse.Oni)]
    [InlineData(7, TaikoCourse.Ura)]
    public void AuthoredCourseSlotsAreTranslatedAtTheHostBoundary(int slot, TaikoCourse course)
        => Assert.Equal((int)course, AuthoredSongSelectionRequest.ToCatalogCourse(slot));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void UnsupportedAuthoredCourseSlotsAreNotSilentlyReinterpreted(int slot)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AuthoredSongSelectionRequest.ToCatalogCourse(slot));

    [Fact]
    public void AuthoredOnePlayerSelectionAcceptsNonFinitePlayerTwoSentinel()
    {
        var request = AuthoredSongSelectionRequest.FromHostCall(new LumenHostCall([
            LumenHostValue.FromNumber(2),
            LumenHostValue.FromNumber(7),
            LumenHostValue.FromNumber(3),
            LumenHostValue.FromNumber(double.NaN),
        ]));

        Assert.Equal(new AuthoredSongSelectionRequest(2, 7, 3, null), request);
    }

    [Fact]
    public void AuthoredSoloRightDrumSelectionHasAnEmptyPlayerOneCourse()
    {
        // Traced NotifyEndCourseSelect(2, 1, '', 3) for a player on the right drum alone.
        var request = AuthoredSongSelectionRequest.FromHostCall(new LumenHostCall([
            LumenHostValue.FromNumber(2),
            LumenHostValue.FromNumber(1),
            LumenHostValue.FromString(""),
            LumenHostValue.FromNumber(3),
        ]));

        Assert.Equal(new AuthoredSongSelectionRequest(2, 1, null, 3), request);
    }

    [Fact]
    public void AuthoredSelectionKeepsRequiredFieldsStrict()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuthoredSongSelectionRequest.FromHostCall(new LumenHostCall([
                LumenHostValue.FromNumber(0),
                LumenHostValue.FromNumber(0),
                LumenHostValue.FromNumber(double.NaN),
            ])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CourseAvailabilityDoesNotActivateRestrictedSelection(bool hasUra)
    {
        var key = new SongKey(SongSourceKind.Tja, "course-projection");
        var descriptor = new SongDescriptor(
            key,
            new SongTitle("Projection"),
            null,
            [
                new SongChartDescriptor(
                    new ChartKey(key, "oni"),
                    "Oni",
                    new CatalogAssetKey(new CatalogProviderId("synthetic"), "oni"),
                    TaikoCourse.Oni,
                    8),
                .. hasUra ? new[] { new SongChartDescriptor(
                    new ChartKey(key, "ura"),
                    "Ura",
                    new CatalogAssetKey(new CatalogProviderId("synthetic"), "ura"),
                    TaikoCourse.Ura,
                    9) } : Array.Empty<SongChartDescriptor>(),
            ]);
        var song = new SongSelectSong(
            descriptor,
            SongCourseBits.Oni | (hasUra ? SongCourseBits.Ura : SongCourseBits.None),
            [null, null, null, 8, hasUra ? 9 : null]);

        var presentation = SongSelectCoursePresentation.FromSong(song);

        Assert.Equal(AuthoredSongCourseBits.None, presentation.SelectionRestrictions);
        Assert.Equal(8, presentation.OniStars);
        Assert.Equal(hasUra ? 9 : 0, presentation.HiddenOniStars);
        Assert.Equal(0, presentation.EasyStars);
        Assert.Equal(0, presentation.NormalStars);
        Assert.Equal(0, presentation.HardStars);
        Assert.Equal(0, presentation.HiddenEasyStars);
        Assert.Equal(0, presentation.HiddenNormalStars);
        Assert.Equal(0, presentation.HiddenHardStars);
    }

    [Fact]
    public async Task CatalogViewPublishesCourseBitsLevelsAndAuthoredCategoryLabel()
    {
        using var catalog = new GlobalSongCatalog([new SyntheticProvider()]);
        var snapshot = await catalog.RefreshAsync();

        var view = new SongSelectCatalogView(snapshot);

        var category = Assert.Single(view.Categories);
        Assert.Equal("Anime", category.Name);
        Assert.Equal("アニメ", category.AuthoredLabel);
        Assert.Equal(SongCategoryPresentation.AlwaysVisible, category.Presentation);
        Assert.Equal(0x9e4309U, category.BoardStyle.CompactOutlineRgb);
        var song = Assert.Single(category.Songs);
        Assert.Equal(SongCourseBits.Easy | SongCourseBits.Oni, song.AvailableCourses);
        Assert.Equal(2, song.Level(TaikoCourse.Easy));
        Assert.Equal(8, song.Level(TaikoCourse.Oni));
        Assert.False(song.HasCourse(TaikoCourse.Hard));
    }

    [Fact]
    public async Task RightDrumPlayerAloneSelectsAsPlayerTwo()
    {
        using var catalog = new GlobalSongCatalog([new SyntheticProvider()]);
        var snapshot = await catalog.RefreshAsync();
        var playRequests = new PlayRequestState();
        var session = new SongSelectSession(new SongSelectCatalogView(snapshot), new RecordingTextureService(),
            new RecordingPreviewController(), playRequests);

        var selection = session.Select(0, 0, -1, (int)TaikoCourse.Oni);

        Assert.Null(selection.PlayerOneChart);
        Assert.Equal("oni", selection.PlayerTwoChart!.StableId);
        var player = Assert.Single(Assert.IsType<PlayRequest>(playRequests.Pending).Players);
        Assert.Equal(LocalPlayerSlot.PlayerTwo, player.Player);
    }

    [Fact]
    public async Task SessionRoutesBoardPreviewAndSelectionByPinnedCatalogIdentity()
    {
        using var catalog = new GlobalSongCatalog([new SyntheticProvider()]);
        var snapshot = await catalog.RefreshAsync();
        var textures = new RecordingTextureService();
        var previews = new RecordingPreviewController();
        var playRequests = new PlayRequestState();
        var session = new SongSelectSession(new SongSelectCatalogView(snapshot), textures, previews, playRequests);

        var surface = session.GetBoardTexture(0, 0, SongBoardTextureKind.Compact);
        var preview = session.Preview(0, 0);
        Assert.Same(preview, previews.Current);
        var selection = session.Select(0, 0, (int)TaikoCourse.Oni, -1);

        Assert.Equal(new LumenNativeSurfaceKey("title:Compact:synthetic"), surface);
        Assert.Equal("Synthetic", textures.LastSong!.Descriptor.Title.Primary);
        Assert.Equal(TimeSpan.FromSeconds(12), preview!.Start);
        Assert.Equal(snapshot.Revision, selection.CatalogRevision);
        Assert.Equal("oni", selection.PlayerOneChart!.StableId);
        Assert.Null(selection.PlayerTwoChart);
        Assert.Null(previews.Current);
        var request = Assert.IsType<PlayRequest>(playRequests.Pending);
        Assert.Equal(snapshot.Revision, request.CatalogRevision);
        Assert.Equal(selection.Song, request.Song);
        Assert.Equal(new CatalogAssetKey(new CatalogProviderId("synthetic"), "audio"), request.AudioAsset);
        var player = Assert.Single(request.Players);
        Assert.Equal(LocalPlayerSlot.PlayerOne, player.Player);
        Assert.Equal(selection.PlayerOneChart, player.Chart);
        Assert.Equal(new CatalogAssetKey(new CatalogProviderId("synthetic"), "oni"), player.ChartAsset);
        Assert.Equal(TaikoCourse.Oni, player.Course);
        Assert.Throws<InvalidOperationException>(() => session.Select(0, 0, (int)TaikoCourse.Hard, -1));
    }

    [Fact]
    public async Task PendingPlayRequestMustBeConsumedBeforeAnotherLaunch()
    {
        using var catalog = new GlobalSongCatalog([new SyntheticProvider()]);
        var snapshot = await catalog.RefreshAsync();
        var pending = new PlayRequestState();
        var session = new SongSelectSession(
            new SongSelectCatalogView(snapshot),
            new RecordingTextureService(),
            new RecordingPreviewController(),
            pending);

        session.Select(0, 0, (int)TaikoCourse.Easy, -1);

        Assert.Throws<InvalidOperationException>(() => session.Select(0, 0, (int)TaikoCourse.Oni, -1));
        var first = pending.ActivatePending();
        Assert.Equal(TaikoCourse.Easy, Assert.Single(first.Players).Course);

        pending.ClearActive();
        session.Select(0, 0, (int)TaikoCourse.Oni, -1);
        Assert.Equal(TaikoCourse.Oni, Assert.Single(pending.Pending!.Players).Course);
    }

    [Fact]
    public async Task FailedChartLaunchCanBeCancelledAndRetried()
    {
        using var catalog = new GlobalSongCatalog([new SyntheticProvider()]);
        var snapshot = await catalog.RefreshAsync();
        var pending = new PlayRequestState();
        var session = new SongSelectSession(new SongSelectCatalogView(snapshot),
            new RecordingTextureService(), new RecordingPreviewController(), pending);
        session.Select(0, 0, (int)TaikoCourse.Easy, -1);
        pending.CancelPending();
        Assert.Null(pending.Pending);
        Assert.Null(pending.Active);
        session.Select(0, 0, (int)TaikoCourse.Oni, -1);
        var active = pending.ActivatePending();
        Assert.Equal(TaikoCourse.Oni, Assert.Single(active.Players).Course);
        pending.CancelPending();
        Assert.Same(active, pending.Active);
    }

    [Fact]
    public async Task UnrestrictedSongCanLaunchBothOniAndUraButNotMissingCourses()
    {
        using var catalog = new GlobalSongCatalog([new SyntheticProvider(includeUra: true)]);
        var snapshot = await catalog.RefreshAsync();
        var pending = new PlayRequestState();
        var view = new SongSelectCatalogView(snapshot);
        var session = new SongSelectSession(view, new RecordingTextureService(),
            new RecordingPreviewController(), pending);
        Assert.True(view.TryGetSong(0, 0, out var song));
        var presentation = SongSelectCoursePresentation.FromSong(song);
        Assert.Equal(AuthoredSongCourseBits.None, presentation.SelectionRestrictions);
        Assert.Equal(8, presentation.OniStars);
        Assert.Equal(9, presentation.HiddenOniStars);
        foreach (var authoredCourse in new[] { 3, 7 })
        {
            var course = AuthoredSongSelectionRequest.ToCatalogCourse(authoredCourse);
            session.Select(0, 0, course, -1);
            Assert.Equal((TaikoCourse)course, Assert.Single(pending.ActivatePending().Players).Course);
            pending.ClearActive();
        }
        Assert.Equal(0, presentation.HardStars);
        Assert.Throws<InvalidOperationException>(() => session.Select(0, 0, (int)TaikoCourse.Hard, -1));
        Assert.Null(pending.Pending);
    }

    private sealed class SyntheticProvider(bool includeUra = false) : ISongCatalogProvider
    {
        public CatalogProviderId Id { get; } = new("synthetic");

        public SongSourceKind Source => SongSourceKind.Tja;

        public ValueTask<SongCatalogContribution> ScanAsync(
            IProgress<CatalogScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var songKey = new SongKey(Source, "synthetic");
            var song = new SongDescriptor(
                songKey,
                new SongTitle("Synthetic"),
                "Fixture",
                [
                    new SongChartDescriptor(
                        new ChartKey(songKey, "easy"),
                        "Easy",
                        new CatalogAssetKey(Id, "easy"),
                        TaikoCourse.Easy,
                        2),
                    new SongChartDescriptor(
                        new ChartKey(songKey, "oni"),
                        "Oni",
                        new CatalogAssetKey(Id, "oni"),
                        TaikoCourse.Oni,
                        8),
                    .. includeUra ? new[] { new SongChartDescriptor(
                        new ChartKey(songKey, "ura"), "Ura", new CatalogAssetKey(Id, "ura"),
                        TaikoCourse.Ura, 9) } : Array.Empty<SongChartDescriptor>(),
                ],
                new CatalogAssetKey(Id, "audio"),
                TimeSpan.FromSeconds(12));
            return ValueTask.FromResult(new SongCatalogContribution(
                Id,
                Source,
                [song],
                [new SongCategoryDescriptor(new CategoryKey(Source, "anime"), "Anime", 0, [songKey])]));
        }
    }

    private sealed class RecordingTextureService : ISongBoardTextureService
    {
        public SongSelectSong? LastSong { get; private set; }

        public LumenNativeSurfaceKey GetSongTitle(
            SongSelectSong song,
            SongBoardTextureStyle style,
            SongBoardTextureKind kind)
        {
            LastSong = song;
            return new LumenNativeSurfaceKey($"title:{kind}:{song.Descriptor.Key.StableId}");
        }
    }

    private sealed class RecordingPreviewController : ISongPreviewController
    {
        public SongPreviewRequest? Current { get; private set; }

        public void SetPreview(SongPreviewRequest? request) => Current = request;
    }
}
