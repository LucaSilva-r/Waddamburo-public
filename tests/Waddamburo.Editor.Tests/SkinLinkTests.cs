using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor.Tests;

public sealed class SkinLinkTests
{
    [Fact]
    public void WhatTheGameGivesItsMoviesReachesTheEditor()
    {
        using var server = new SkinLinkServer();
        using var live = new SkinLive(server.Port);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!server.TakeJoined() && waited.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        server.Send(SkinLinkWire.Screen("song-select"));
        server.Send(SkinLinkWire.Provided("song_select/song_select", new(WdKind.List, "row.title", 0, 3, [LumenHostValue.FromString("Senbonzakura")])));
        server.Send(SkinLinkWire.Provided("song_select/song_select", new(WdKind.State, "made.up", 0, 0, [LumenHostValue.FromNumber(1)])));
        string? followed = null;
        live.ScreenChanged += screen => followed = screen;
        while (!live.Holds("song_select/song_select") && waited.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
            live.Drain();
        }
        Assert.Equal("song-select", followed);
        Assert.True(live.Holds("song_select/song_select"));
        Assert.False(live.Holds("entry/entry"));
    }

    [Fact]
    public void TheEditorAsksForScreensAndHearsWhyNot()
    {
        Assert.Equal(("show", "gameplay"), SkinLinkWire.Command(SkinLinkWire.Show("gameplay")));
        Assert.Equal(("notice", "no song"), SkinLinkWire.Command(SkinLinkWire.Notice("no song")));
        Assert.Null(SkinLinkWire.Command(SkinLinkWire.Screen("entry")));
        Assert.Equal(("entry/entry", "{ \"format\": 2 }"), SkinLinkWire.TryPackage(SkinLinkWire.Package("entry/entry", "{ \"format\": 2 }")));
        Assert.Null(SkinLinkWire.TryPackage(SkinLinkWire.Show("entry")));
        using var server = new SkinLinkServer();
        using var live = new SkinLive(server.Port);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!server.TakeJoined() && waited.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        live.Show("song-select");
        string? line = null;
        while (!server.TryTake(out line) && waited.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        Assert.Equal(("show", "song-select"), SkinLinkWire.Command(line!));
    }

    // A count given after its rows reaches the editor after them too (scripts rebuild a list on the count); only an
    // older value of the same state is left out.
    [Fact]
    public void ABatchKeepsTheOrderValuesWereGivenIn()
    {
        var batch = new ProvidedBatch();
        LumenProvided state(string name, double value, int index = 0) =>
            new(name.Contains("row.", StringComparison.Ordinal) ? WdKind.List : WdKind.State, name, 0, index, [LumenHostValue.FromNumber(value)]);
        batch.Add("m", state("rows.count", 10));
        batch.Add("m", state("row.depth", 1, 7));
        batch.Add("m", state("rows.count", 12));
        batch.Add("m", new(WdKind.Event, "combo.milestone", 0, 0, [LumenHostValue.FromNumber(100)]));
        Assert.Equal(["row.depth", "rows.count", "combo.milestone"], batch.Take().Select(static each => each.Provided.Name));
        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public void ValuesKeepTheirKindOnTheWire()
    {
        var line = SkinLinkWire.Provided("a/b", new(WdKind.Event, "note.judged", 2, 0,
            [LumenHostValue.FromString("good"), LumenHostValue.FromBoolean(true), LumenHostValue.FromNumber(1.5), LumenHostValue.Undefined]));
        Assert.True(SkinLinkWire.TryRead(line, out var screen, out var movie, out var provided));
        Assert.Null(screen);
        Assert.Equal("a/b", movie);
        Assert.Equal((WdKind.Event, "note.judged", 2), (provided.Kind, provided.Name, provided.Drum));
        Assert.Equal([LumenHostValueKind.Text, LumenHostValueKind.Boolean, LumenHostValueKind.Number, LumenHostValueKind.Undefined],
            provided.Values.Select(static value => value.Kind));
        Assert.False(SkinLinkWire.TryRead("{\"t\":\"s\",\"m\":\"a/b\",\"n\":\"made.up\",\"d\":0,\"x\":0,\"v\":[1]}", out _, out _, out _));
    }
}
