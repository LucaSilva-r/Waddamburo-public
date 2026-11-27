using Waddamburo.Editor;
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
        using var server = new SkinLinkServer();
        using var live = new SkinLive(server.Port);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!server.TakeJoined() && waited.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        live.Show("song-select");
        string? line = null;
        while (!server.TryTake(out line) && waited.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        Assert.Equal(("show", "song-select"), SkinLinkWire.Command(line!));
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
