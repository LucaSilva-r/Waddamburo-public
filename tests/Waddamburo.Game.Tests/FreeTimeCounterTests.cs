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
        added.Emit("score.added", n(960));
        Assert.Equal("+960", added.ReadScriptValue("word.text"));
        advance(added, 40);
        Assert.Equal("", added.ReadScriptValue("word.text"));
    }

    [Theory]
    [InlineData("onp_don")]
    [InlineData("onp_katsu_dai")]
    public void TheNotesPickTheirFaceFromTheComboAndTheBeat(string name)
    {
        var note = load($"enso_system/common/{name}");
        advance(note, 2);
        note.Publish("play.beat", n(2.5));
        Assert.Equal(note.Labels["level01"], note.CurrentFrame); // level01 holds still
        note.Publish("play.combo", n(51));
        Assert.Equal(note.Labels["level02"] + 20, note.CurrentFrame); // half a beat into its one-beat loop
        advance(note, 3);
        Assert.Equal(note.Labels["level02"] + 20, note.CurrentFrame); // stopped: only the beat moves it
        note.Publish("play.combo", n(151));
        note.Publish("play.beat", n(-0.25)); // before the first timing point
        Assert.Equal(note.Labels["level03"] + 10, note.CurrentFrame); // two loops a beat
        note.Publish("play.combo", n(0));
        Assert.Equal(note.Labels["level01"], note.CurrentFrame);
        Assert.True(note.UsesProvider);
        Assert.Empty(note.Diagnostics);
    }

    [Theory]
    [InlineData("onp_renda")]
    [InlineData("onp_renda_dai")]
    public void TheRollsStretchToTheirLengthAndSwellOnHits(string name)
    {
        var roll = load($"enso_system/common/{name}");
        Assert.True(roll.UsesProvider); // known before its first frame: the engine sizes a fresh roll at once
        roll.Publish("note.length", n(320));
        advance(roll, 2);
        roll.Emit("note.hit", LumenHostValue.FromString("don"));
        advance(roll, 10);
        Assert.True(roll.TryGetInstanceBounds("tail", out var tail));
        Assert.InRange(tail.X + tail.Width / 2, 315, 325); // the tail at the roll's end
        Assert.Empty(roll.Diagnostics);
    }

    [Fact]
    public void TheDanCardShowsEachSong()
    {
        var card = load("enso_dojo/between");
        advance(card, 2);
        card.Publish("dan.song", n(2));
        Assert.Equal("Song 2", card.ReadScriptValue("card.text"));
        card.Publish("song.title", LumenHostValue.FromString("Senbonzakura")); // the title comes after the number
        Assert.Equal("Song 2:  Senbonzakura", card.ReadScriptValue("card.text"));
        advance(card, 180);
        Assert.Equal("", card.ReadScriptValue("card.text"));
        Assert.Empty(card.Diagnostics);
    }

    [Fact]
    public void TheIntermissionsFollowTheTransitionPhase()
    {
        static LumenHostValue text(string value) => LumenHostValue.FromString(value);
        var rainbow = load("intermission/scene_change_rainbow");
        advance(rainbow, 2);
        rainbow.Publish("transition.title", LumenHostValue.FromBoolean(true));
        rainbow.Publish("transition.phase", text("cover"));
        Assert.Equal(rainbow.Labels["in_extra"], rainbow.CurrentFrame);
        rainbow.Publish("transition.phase", text("reveal"));
        Assert.Equal(rainbow.Labels["out_extra"], rainbow.CurrentFrame);
        rainbow.Publish("transition.phase", LumenHostValue.Undefined); // shown again (a kept movie)
        rainbow.Publish("transition.title", LumenHostValue.FromBoolean(false));
        rainbow.Publish("transition.phase", text("cover"));
        Assert.Equal(rainbow.Labels["in"], rainbow.CurrentFrame);
        Assert.Empty(rainbow.Diagnostics);

        var shutter = load("intermission/shutter");
        advance(shutter, 2);
        shutter.Publish("transition.side", n(2));
        shutter.Publish("transition.phase", text("cover"));
        Assert.Equal(shutter.Labels["close"], shutter.CurrentFrame);
        Assert.Empty(shutter.Diagnostics);
    }

    [Fact]
    public void TheJudgementWordFollowsTheHits()
    {
        static LumenHostValue text(string value) => LumenHostValue.FromString(value);
        var word = load("enso_system/common/lane_hit_effect");
        advance(word, 2);
        word.Emit("note.judged", text("good"), LumenHostValue.FromBoolean(true), text("don"), LumenHostValue.FromBoolean(true));
        Assert.Equal(word.Labels["hit_ryo_big"], word.CurrentFrame);
        advance(word, 40); // the word ends: back at rest, the listener still there once
        Assert.Equal(0, word.CurrentFrame);
        word.Emit("note.judged", text("bad"), LumenHostValue.FromBoolean(false), text("ka"), LumenHostValue.FromBoolean(false));
        Assert.Equal(0, word.CurrentFrame); // passed unhit: no word
        word.Emit("note.judged", text("bad"), LumenHostValue.FromBoolean(true), text("ka"), LumenHostValue.FromBoolean(true));
        Assert.Equal(word.Labels["hit_huka"], word.CurrentFrame);
        Assert.True(word.UsesProvider);
        Assert.Empty(word.Diagnostics);
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
        lane.Emit("drum.hit", text("left"), text("ka"));
        Assert.Equal((30, true), lane.InstanceFrame("effect")); // katsu_s
        lane.Emit("note.judged", text("good"), LumenHostValue.FromBoolean(true), text("don"), LumenHostValue.FromBoolean(true));
        Assert.Equal((110, true), lane.InstanceFrame("hit_effect")); // hit_dai_ryo
        Assert.Equal((50, true), lane.InstanceFrame("effect")); // don_hit
        lane.Emit("note.judged", text("bad"), LumenHostValue.FromBoolean(false), text("don"), LumenHostValue.FromBoolean(false));
        Assert.Equal((110, true), lane.InstanceFrame("hit_effect")); // passed unhit: no ring
        Assert.Empty(lane.Diagnostics);

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
    public void TheFlightMovieFliesTheHitsItHears()
    {
        var flights = load("enso_system/don1p/onp_kiseki_don_1p");
        advance(flights, 2);
        Assert.True(flights.UsesProvider);
        flights.Emit("note.judged", LumenHostValue.FromString("good"), LumenHostValue.FromBoolean(true), LumenHostValue.FromString("ka"));
        flights.Emit("note.judged", LumenHostValue.FromString("bad"), LumenHostValue.FromBoolean(false), LumenHostValue.FromString("don"));
        flights.Emit("roll.hit", LumenHostValue.FromString("don"), LumenHostValue.FromBoolean(false));
        Assert.Equal(2d, Convert.ToDouble(flights.ReadScriptValue("next"))); // the miss took no note
        advance(flights, 10);
        Assert.Equal("DisplayInstance", flights.ReadScriptValue("f0")?.GetType().Name); // in the air
        advance(flights, 30);
        Assert.NotEqual("DisplayInstance", flights.ReadScriptValue("f0")?.GetType().Name); // landed: the flight removed itself
        Assert.Empty(flights.Diagnostics);
    }

    [Fact]
    public void TheDanPanelMakesARowPerRequirement()
    {
        var panel = load("enso_dojo/dani_enso");
        advance(panel, 2);
        panel.PublishItem("dan.exam.type", 0, LumenHostValue.FromString("bads"));
        panel.PublishItem("dan.exam.target", 0, n(10));
        panel.PublishItem("dan.exam.type", 1, LumenHostValue.FromString("soul_gauge"));
        panel.PublishItem("dan.exam.target", 1, n(100));
        panel.Publish("dan.exam.count", n(2));
        Assert.Equal("BAD under 10", panel.ReadScriptValue("row0.label.text"));
        panel.PublishItem("dan.exam.value", 1, n(64));
        panel.PublishItem("dan.exam.possible", 0, LumenHostValue.FromBoolean(false));
        Assert.Equal("64%", panel.ReadScriptValue("row1.value.text"));
        Assert.Equal("Soul gauge 100% or more", panel.ReadScriptValue("row1.label.text"));
        Assert.Equal("BAD under 10  FAILED", panel.ReadScriptValue("row0.label.text"));
        panel.Publish("dan.exam.count", n(1)); // the list shrank: the row goes
        advance(panel, 1);
        Assert.NotEqual("DisplayInstance", panel.ReadScriptValue("row1")?.GetType().Name);
        Assert.Empty(panel.Diagnostics);
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
