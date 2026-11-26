using Waddamburo.Catalog;
using Waddamburo.Features;
using Waddamburo.Features.Dan;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Flow;

namespace Waddamburo.App.Tests;

public sealed class DanModuleTests
{
    private sealed class Fixed(params DanSeries[] series) : IDanCourseProvider
    {
        public IReadOnlyList<DanSeries> Saved => series;

        public Task<IReadOnlyList<DanSeries>> LoadAsync(DanLibrary library) => Task.FromResult<IReadOnlyList<DanSeries>>(series);
    }

    [Fact]
    public async Task ProvidersSeriesListInOrderWithTheirNames()
    {
        static DanSeries series(string id, string name) =>
            new(id, name, [new DanCourse(1, "5級", id + "-1", [], [new DanCourseSong("missing", 3, "?", 100, [])])]);
        var dan = new DanModule(new CreditState(), static () => null, static () => false);
        Assert.False(dan.Available);

        dan.Load(new DanLibrary(new GlobalSongCatalog([]).Current, new ChartHashes(null, static (_, _, _) => throw new InvalidDataException()),
                static (_, _, _) => throw new InvalidDataException()),
            new Fixed(series("green", "")), new Fixed(series(TjaDanCourses.Prefix + "mine", "My courses")));

        Assert.Equal([("green", "Green"), (TjaDanCourses.Prefix + "mine", "My courses")], dan.AllSeries);
        for (var tries = 0; !dan.Ready && tries < 100; tries++)
            await Task.Delay(10);
        Assert.True(dan.Ready);
        // A song the library lacks locks its course.
        Assert.False(Assert.Single(dan.Courses("green")).Playable);
    }
}
