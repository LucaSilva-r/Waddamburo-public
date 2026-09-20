using Waddamburo.Catalog;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.Tests;

public sealed class SongSelectSessionTests
{
    [Fact]
    public void CoursePresentationPublishesOniAndUraWithoutCourseRestrictions()
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
                new SongChartDescriptor(
                    new ChartKey(key, "ura"),
                    "Ura",
                    new CatalogAssetKey(new CatalogProviderId("synthetic"), "ura"),
                    TaikoCourse.Ura,
                    9),
            ]);
        var song = new SongSelectSong(
            descriptor,
            SongCourseBits.Oni | SongCourseBits.Ura,
            [null, null, null, 8, 9]);

        var presentation = SongSelectCoursePresentation.FromSong(song);

        Assert.Equal(AuthoredSongCourseBits.None, presentation.InvalidCourses);
        Assert.Equal(8, presentation.OniStars);
        Assert.Equal(9, presentation.HiddenOniStars);
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
    public async Task SessionRoutesBoardPreviewAndSelectionByPinnedCatalogIdentity()
    {
        using var catalog = new GlobalSongCatalog([new SyntheticProvider()]);
        var snapshot = await catalog.RefreshAsync();
        var textures = new RecordingTextureService();
        var previews = new RecordingPreviewController();
        var session = new SongSelectSession(new SongSelectCatalogView(snapshot), textures, previews);

        var surface = session.GetBoardTexture(0, 0, SongBoardTextureKind.Compact);
        var preview = session.Preview(0, 0);
        Assert.Same(preview, previews.Current);
        var selection = session.Select(0, 0, (int)TaikoCourse.Oni, -1);

        Assert.Equal(new LumenNativeSurfaceKey("title:Compact:synthetic"), surface);
        Assert.Equal("Synthetic", textures.LastSong!.Descriptor.Title.Primary);
        Assert.Equal(TimeSpan.FromSeconds(12), preview!.Start);
        Assert.Equal(snapshot.Revision, selection.CatalogRevision);
        Assert.Equal("oni", selection.PlayerOneChart.StableId);
        Assert.Null(selection.PlayerTwoChart);
        Assert.Null(previews.Current);
        Assert.Throws<InvalidOperationException>(() => session.Select(0, 0, (int)TaikoCourse.Hard, -1));
    }

    private sealed class SyntheticProvider : ISongCatalogProvider
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
