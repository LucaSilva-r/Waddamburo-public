using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

/// <summary>
/// The free time_counter movie (movies/indicator/time_counter) keeps the behaviour measured on the timer it
/// replaces: a second every 60 frames, and what each callback answers in each state.
/// </summary>
public sealed class FreeTimeCounterTests
{
    private static LumenPlayer load(string movie = "indicator/time_counter")
    {
        var directory = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(directory, "movies", "indicator", "time_counter")))
            directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("movies/indicator/time_counter");
        // The textures only need a size here.
        var content = LumenMovieContent.LoadPackage(Path.Combine(directory, "movies", movie),
            static _ => (1, 1, new byte[4]));
        Assert.Empty(content.Diagnostics);
        return content.CreatePlayer();
    }

    private static LumenHostValue call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        Assert.True(player.TryInvokeCallback(name, arguments, out var result), name);
        return result;
    }

    private static void advance(LumenPlayer player, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
            player.Advance();
    }

    private static LumenHostValue n(double value) => LumenHostValue.FromNumber(value);

    [Fact]
    public void CountsASecondEverySixtyFramesAndAnswersEachState()
    {
        var player = load();
        Assert.Equal(16, player.CallbackNames.Length);
        call(player, "SetTime", n(12));
        Assert.Equal(12, call(player, "GetSec").AsNumber());
        Assert.False(call(player, "IsStarted").AsBoolean());
        advance(player, 90);
        Assert.Equal(12, call(player, "GetSec").AsNumber()); // Not started: not counting.

        call(player, "Start", n(12), LumenHostValue.FromBoolean(true));
        Assert.True(call(player, "IsStarted").AsBoolean());
        advance(player, 59);
        Assert.Equal(12, call(player, "GetSec").AsNumber());
        advance(player, 1);
        Assert.Equal(11, call(player, "GetSec").AsNumber());

        call(player, "Pause");
        Assert.True(call(player, "IsPaused").AsBoolean());
        advance(player, 200);
        Assert.Equal(11, call(player, "GetSec").AsNumber());
        call(player, "Resume");
        Assert.False(call(player, "IsPaused").AsBoolean());
        call(player, "AddSec", n(5));
        Assert.Equal(16, call(player, "GetSec").AsNumber());

        call(player, "Stop");
        Assert.False(call(player, "IsStarted").AsBoolean());
        call(player, "Resume"); // Only a paused timer resumes.
        advance(player, 120);
        Assert.Equal(16, call(player, "GetSec").AsNumber());

        call(player, "Start", n(2), LumenHostValue.FromBoolean(false));
        advance(player, 119);
        Assert.False(call(player, "IsTimeup").AsBoolean());
        advance(player, 1);
        Assert.True(call(player, "IsTimeup").AsBoolean());
        Assert.Equal(0, call(player, "GetSec").AsNumber());
        advance(player, 300);
        Assert.Equal(0, call(player, "GetSec").AsNumber());
        Assert.True(call(player, "IsStarted").AsBoolean()); // Time up is still "started".
        call(player, "SetTime", n(30));
        Assert.True(call(player, "IsTimeup").AsBoolean());
        advance(player, 120);
        Assert.Equal(30, call(player, "GetSec").AsNumber());
        Assert.Empty(player.Diagnostics);
    }

    [Fact]
    public void EveryMovieOfTheEngineLoadsAndRunsItsFirstFrames()
    {
        var directory = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(directory, "movies", "indicator", "time_counter")))
            directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("movies");
        var packages = Directory.EnumerateFiles(Path.Combine(directory, "movies"), "movie.json", SearchOption.AllDirectories)
            .Select(static file => Path.GetDirectoryName(file)!).Order(StringComparer.Ordinal).ToArray();
        Assert.True(packages.Length >= 30);
        foreach (var package in packages)
        {
            var errors = new List<Waddamburo.Formats.Package.ActionScriptException>();
            var content = LumenMovieContent.LoadPackage(package, static _ => (1, 1, new byte[4]), scriptErrors: errors);
            Assert.True(errors.Count == 0, $"{package}: {string.Join("; ", errors.Select(static error => error.Message))}");
            Assert.True(content.Diagnostics.IsEmpty, $"{package}: {string.Join("; ", content.Diagnostics.Select(static each => each.Message))}");
            var player = content.CreatePlayer();
            advance(player, 3);
            Assert.True(player.Diagnostics.IsEmpty, $"{package}: {string.Join("; ", player.Diagnostics.Select(static each => each.Message))}");
        }
    }

    [Fact]
    public void TheGameplaySignsFollowTheProvider()
    {
        var goGo = load("enso_system/base1p/action_gogotime");
        advance(goGo, 2);
        goGo.Publish("play.goGo", LumenHostValue.FromBoolean(true));
        Assert.True(goGo.TryGotoLabel("", "splash")); // the game's jump: the listener is not set twice
        advance(goGo, 2);
        Assert.Equal("GO-GO TIME!", goGo.ReadScriptValue("word.text"));
        goGo.Publish("play.goGo", LumenHostValue.FromBoolean(false));
        Assert.Equal("", goGo.ReadScriptValue("word.text"));

        var fire = load("enso_system/base1p/gage_fire_1p");
        advance(fire, 2);
        fire.Publish("play.gaugeFull", LumenHostValue.FromBoolean(true));
        Assert.Equal("MAX", fire.ReadScriptValue("word.text"));

        var combo = load("enso_system/don1p/combo_bonus_don_1p");
        advance(combo, 2);
        combo.Emit("combo.milestone", n(200));
        Assert.Equal("200 COMBO!", combo.ReadScriptValue("word.text"));
        advance(combo, 90);
        Assert.Equal("", combo.ReadScriptValue("word.text"));

        var added = load("enso_system/don1p/score_add_don_1p");
        advance(added, 2);
        added.Publish("play.score", n(0)); // the first value is where the play stands, not points scored
        Assert.Null(added.ReadScriptValue("word.text"));
        added.Publish("play.score", n(960));
        Assert.Equal("+960", added.ReadScriptValue("word.text"));
        advance(added, 40);
        Assert.Equal("", added.ReadScriptValue("word.text"));
    }

    [Fact]
    public void TheLaneAndTheBubbleFollowTheRoute()
    {
        static LumenHostValue text(string value) => LumenHostValue.FromString(value);
        var lane = load("enso_system/common/lane_hit");
        advance(lane, 2);
        lane.Publish("play.goGo", LumenHostValue.FromBoolean(true));
        lane.Publish("play.branch", text("master"));
        Assert.Empty(lane.Diagnostics); // what the tints show is checked on screen: a clip's _visible is not readable here

        var bubble = load("enso_system/base1p/fukidashi_level_1p");
        advance(bubble, 2);
        bubble.Publish("play.branch", text("normal")); // where the play stands: no change yet
        Assert.Null(bubble.ReadScriptValue("word.text"));
        bubble.Publish("play.branch", text("master"));
        Assert.Equal("LEVEL UP!", bubble.ReadScriptValue("word.text"));
        bubble.Publish("play.branch", text("expert"));
        Assert.Equal("LEVEL DOWN", bubble.ReadScriptValue("word.text"));
        advance(bubble, 90);
        Assert.Equal("", bubble.ReadScriptValue("word.text"));
    }

    [Fact]
    public void ShowsItsNumberAndSongTab()
    {
        var player = load();
        call(player, "SetFinalStage", LumenHostValue.FromBoolean(false));
        call(player, "SetMusicNum", n(2));
        call(player, "SetTime", n(42));
        Assert.Equal("42", player.ReadScriptValue("face.digits.dark.text"));
        Assert.Equal("2曲目", player.ReadScriptValue("song.normal.text"));
        call(player, "SetFinalStage", LumenHostValue.FromBoolean(true));
        Assert.Equal("2曲目", player.ReadScriptValue("song.final.text"));
        call(player, "SetTime", n(7)); // The last seconds are drawn light on red.
        Assert.Equal("7", player.ReadScriptValue("face.digits.light.text"));
        call(player, "SetMusicNum", n(0));
        Assert.Equal("", player.ReadScriptValue("song.final.text"));
        call(player, "SetVisible", LumenHostValue.FromBoolean(false));
        Assert.Empty(player.CreateRenderSnapshot().Quads);
    }

    [Fact]
    public void TheBoardLightsItsDrumWhereItWasStruck()
    {
        var directory = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(directory, "movies", "indicator", "time_counter")))
            directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("movies");
        var player = LumenMovieContent.LoadPackage(Path.Combine(directory, "movies", "enso_system", "common", "lane_obi"),
            static _ => (1, 1, new byte[4])).CreatePlayer();
        advance(player, 2);
        var idle = player.CreateRenderSnapshot().Quads.Length;
        Assert.Equal(0, player.InstanceFrame("ka_right")?.Frame);
        player.Emit("drum.hit", LumenHostValue.FromString("right"), LumenHostValue.FromString("ka"));
        advance(player, 1);
        Assert.True(player.InstanceFrame("ka_right")?.Frame > 0);
        Assert.Equal(0, player.InstanceFrame("don_left")?.Frame);
        Assert.Equal(idle + 1, player.CreateRenderSnapshot().Quads.Length);
        advance(player, 10);
        Assert.Equal(idle, player.CreateRenderSnapshot().Quads.Length); // faded and gone
        // Both hands at once, then struck again while still lit (a drumroll).
        player.Emit("drum.hit", LumenHostValue.FromString("left"), LumenHostValue.FromString("ka"));
        player.Emit("drum.hit", LumenHostValue.FromString("right"), LumenHostValue.FromString("ka"));
        advance(player, 2);
        Assert.Equal(idle + 2, player.CreateRenderSnapshot().Quads.Length);
        player.Emit("drum.hit", LumenHostValue.FromString("left"), LumenHostValue.FromString("ka"));
        advance(player, 1);
        Assert.Equal(idle + 2, player.CreateRenderSnapshot().Quads.Length);
        Assert.True(player.Diagnostics.IsEmpty, string.Join("; ", player.Diagnostics.Select(static each => each.Message)));
    }
}
