using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

/// <summary>
/// The engine's own movies, run with what the provider tells them (the time_counter first, hence the name).
/// </summary>
public sealed class FreeTimeCounterTests
{
    private static LumenPlayer load(string movie = "indicator/time_counter") => FreeMovies.Load(movie);

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
    public void TheTimerShowsTheSecondsAndSongItIsTold()
    {
        var player = load();
        player.Advance();
        player.Publish("timer.left", n(42));
        Assert.Equal("42", player.ReadScriptValue("face.digits.dark.text"));
        player.Publish("timer.left", n(7)); // The last seconds are drawn light on red.
        Assert.Equal("", player.ReadScriptValue("face.digits.dark.text"));
        Assert.Equal("7", player.ReadScriptValue("face.digits.light.text"));
        player.Publish("song.stage", n(2));
        Assert.Equal("2曲目", player.ReadScriptValue("song.normal.text"));
        player.Publish("song.finalStage", LumenHostValue.FromBoolean(true));
        Assert.Equal("2曲目", player.ReadScriptValue("song.final.text"));
        player.Publish("song.stage", LumenHostValue.Undefined);
        Assert.Equal("", player.ReadScriptValue("song.final.text"));
        player.Publish("timer.left", LumenHostValue.Undefined);
        Assert.Equal("", player.ReadScriptValue("face.digits.light.text"));
        Assert.Empty(player.Diagnostics);
    }

    [Fact]
    public void TheRevivalAndTheDanResultsShowWhatTheEngineCounted()
    {
        var revival = load("enso_result/retry_game");
        revival.Advance();
        revival.Publish("revival.norma", n(40));
        revival.Publish("revival.hits", n(12));
        revival.Publish("revival.timeLeft", n(6.2));
        Assert.Equal("12 / 40", revival.ReadScriptValue("count.text"));
        Assert.Equal("7", revival.ReadScriptValue("time.text"));
        revival.Publish("revival.result", LumenHostValue.FromString("success"));
        Assert.Equal("REVIVED!", revival.ReadScriptValue("heading.text"));

        var dan = load("dani_result/dani_result");
        dan.Publish("dan.name", LumenHostValue.FromString("六段"));
        dan.Publish("dan.passed", LumenHostValue.FromString("red"));
        dan.Publish("result.score", n(812340), 1);
        dan.Publish("dan.exam.count", n(1));
        dan.PublishItem("dan.exam.type", 0, LumenHostValue.FromString("bads"));
        dan.PublishItem("dan.exam.target", 0, n(25));
        dan.PublishItem("dan.exam.value", 0, n(9));
        dan.PublishItem("dan.songTitle", 0, LumenHostValue.FromString("Senbonzakura"));
        dan.PublishItem("dan.songGood", 0, n(300));
        dan.PublishItem("dan.songOk", 0, n(20));
        dan.PublishItem("dan.songBad", 0, n(3));
        dan.Advance();
        Assert.Equal("六段", dan.ReadScriptValue("heading.text"));
        Assert.Equal("PASS", dan.ReadScriptValue("outcome.text"));
        Assert.Equal("812340", dan.ReadScriptValue("tvalue0.text"));
        Assert.Equal("BADs   9 / 25", dan.ReadScriptValue("exam0.text"));
        Assert.Equal("1.  Senbonzakura   GOOD 300  OK 20  BAD 3", dan.ReadScriptValue("song0.text"));
        Assert.Equal("", dan.ReadScriptValue("song1.text"));
    }

    [Fact]
    public void ANameBoardShowsItsDrumsPlayerAndTheCardPromptItsCode()
    {
        var board = load("indicator/player_name");
        board.Advance();
        board.Publish("player.name", LumenHostValue.FromString("Don-chan"));
        board.Publish("player.joined", LumenHostValue.FromBoolean(true));
        Assert.Equal("Don-chan", board.ReadScriptValue("plate.label.text"));
        Assert.Equal("D", board.ReadScriptValue("plate.initial.text"));
        board.Publish("player.joined", LumenHostValue.FromBoolean(false));
        Assert.Equal("", board.ReadScriptValue("plate.label.text"));

        var card = load("indicator/msg_banapass");
        card.Advance();
        card.Publish("card.accepting", LumenHostValue.FromBoolean(true));
        card.Publish("card.code", LumenHostValue.FromString("AB1-C2D"));
        card.Publish("card.codeSeconds", n(50));
        Assert.Equal("PAIR A CARD:  AB1-C2D   50", card.ReadScriptValue("line.text"));
        card.Publish("card.player", LumenHostValue.FromString("Katsu-chan"));
        Assert.Equal("CARD:  Katsu-chan", card.ReadScriptValue("line.text"));
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
    public void TheSongSelectListsTheRowsAndPicksForThePlayerWhenTimeRunsOut()
    {
        var select = load("song_select/song_select");
        var asked = new List<string>();
        select.RequestHandler = (name, _) => { if (name != "rows.window") asked.Add(name); return null; }; // the window it shows: any
        static LumenHostValue text(string value) => LumenHostValue.FromString(value);
        select.Publish("rows.count", n(3));
        select.Publish("selected.row", n(2));
        select.Publish("selected.kind", text("chart"));
        select.Publish("selected.title", text("Oni"));
        select.PublishItem("row.kind", 0, text("folder"));
        select.PublishItem("row.depth", 0, n(0));
        select.PublishItem("row.opened", 0, LumenHostValue.FromBoolean(true));
        select.PublishItem("row.title", 0, text("Vocaloid"));
        select.PublishItem("row.count", 0, n(1));
        select.PublishItem("row.kind", 1, text("song"));
        select.PublishItem("row.depth", 1, n(1));
        select.PublishItem("row.opened", 1, LumenHostValue.FromBoolean(true));
        select.PublishItem("row.title", 1, text("Senbonzakura"));
        select.PublishItem("row.kind", 2, text("chart"));
        select.PublishItem("row.depth", 2, n(2));
        select.PublishItem("row.title", 2, text("Oni"));
        select.PublishItem("row.stars", 2, n(7));
        advance(select, 2);
        // A panel per row, in row order: the folder with its count, the chart with its level.
        Assert.Equal("Vocaloid", select.ReadScriptValue("list.p0.title.text"));
        Assert.Equal("1", select.ReadScriptValue("list.p0.meta.text"));
        Assert.Equal("Oni", select.ReadScriptValue("list.p2.chart_title.text"));
        Assert.Equal("7", select.ReadScriptValue("list.p2.chip.label.text"));
        Assert.Equal("Senbonzakura", select.ReadScriptValue("info.title.text")); // a chart shows its song
        Assert.Empty(asked);
        select.Publish("select.timeLeft", n(0)); // out of time on a chart: it is played, once
        advance(select, 3);
        Assert.Equal(["song.start"], asked);
        Assert.Empty(select.Diagnostics);
    }

    [Fact]
    public void TheDanSelectListsTheRowsAndPlaysAnOpenCourse()
    {
        var select = load("dani_select/dani_select");
        var asked = new List<string>();
        select.RequestHandler = (name, arguments) =>
        {
            asked.Add(name + (arguments.Length > 0 ? " " + arguments[0].AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
            return null;
        };
        static LumenHostValue text(string value) => LumenHostValue.FromString(value);
        select.Publish("rows.count", n(3));
        select.Publish("selected.row", n(1));
        select.Publish("selected.kind", text("course"));
        select.Publish("selected.title", text("六段"));
        select.PublishItem("row.kind", 0, text("folder"));
        select.PublishItem("row.depth", 0, n(0));
        select.PublishItem("row.opened", 0, LumenHostValue.FromBoolean(true));
        select.PublishItem("row.title", 0, text("Green"));
        select.PublishItem("row.kind", 1, text("course"));
        select.PublishItem("row.depth", 1, n(1));
        select.PublishItem("row.opened", 1, LumenHostValue.FromBoolean(true));
        select.PublishItem("row.title", 1, text("六段"));
        select.PublishItem("row.kind", 2, text("chart"));
        select.PublishItem("row.depth", 2, n(2));
        select.PublishItem("row.title", 2, text("Senbonzakura"));
        select.PublishItem("row.stars", 2, n(9));
        select.Publish("dan.exam.count", n(1));
        select.PublishItem("dan.exam.type", 0, text("bads"));
        select.PublishItem("dan.exam.target", 0, n(10));
        advance(select, 2);
        Assert.Equal("  - Green", select.ReadScriptValue("row0.text"));
        Assert.Equal(">     - 六段", select.ReadScriptValue("row1.text"));
        Assert.Equal("            Senbonzakura   Lv 9", select.ReadScriptValue("row2.text"));
        Assert.Equal("BADs under 10", select.ReadScriptValue("rule0.text"));
        Assert.Equal("六段", select.ReadScriptValue("course.text"));
        Assert.Empty(select.Diagnostics);
    }

    [Fact]
    public void TheResultsShowEachDrumAndAskToGoOn()
    {
        var result = load("enso_result/result");
        result.Publish("result.course", LumenHostValue.FromString("oni"), 2);
        result.Publish("result.score", n(1004280), 2);
        result.Publish("result.good", n(612), 2);
        result.Publish("result.cleared", LumenHostValue.FromBoolean(true), 2);
        var asked = 0;
        result.RequestHandler = (_, _) => { asked++; return null; };
        advance(result, 60);
        Assert.Equal("ONI  CLEAR", result.ReadScriptValue("p2.value0.text"));
        Assert.Equal("1004280", result.ReadScriptValue("p2.value1.text"));
        Assert.Equal("", result.ReadScriptValue("p2.value2.text")); // a guest: no best
        Assert.Equal(0, asked);
        advance(result, 15 * 60); // nobody hit: the results go on by themselves, once
        Assert.Equal(1, asked);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TheEntryFollowsWhoIsInAndAsksToStart()
    {
        var entry = load("entry/entry");
        var asked = new List<string>();
        entry.RequestHandler = (name, arguments) =>
        {
            asked.Add($"{name} {string.Join(" ", arguments.Select(static value => value.Kind == LumenHostValueKind.Text ? value.AsString() : value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture)))}");
            return null;
        };
        advance(entry, 2);
        Assert.Equal("Hit DON to join", entry.ReadScriptValue("label.text"));
        entry.Publish("entry.setup", LumenHostValue.FromBoolean(true));
        entry.Publish("player.joined", LumenHostValue.FromBoolean(true), 1);
        entry.Publish("entry.setup", LumenHostValue.FromBoolean(false)); // the home setup closes: the mode menu
        Assert.Equal("Play", entry.ReadScriptValue("label.text"));
        Assert.Equal("Ka · turn", entry.ReadScriptValue("hintKa.text"));
        advance(entry, 60); // the dial comes out of the drum
        Assert.True(entry.TryGetInstanceBounds("i0", out _));
        entry.Publish("entry.timeLeft", n(0)); // the countdown ran out: the mode shown starts
        Assert.Equal(["session.start normal"], asked);
        Assert.Empty(entry.Diagnostics);
    }

    [Fact]
    public void TheEntryChoosesWhoPlaysFromTheAccounts()
    {
        static LumenHostValue text(string value) => LumenHostValue.FromString(value);
        var entry = load("entry/entry");
        var asked = new List<string>();
        entry.RequestHandler = (name, arguments) =>
        {
            asked.Add($"{name} {string.Join(" ", arguments.Select(static value => value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture)))}");
            return null;
        };
        advance(entry, 2);
        Assert.Contains("account.picking", entry.ProviderNames); // the engine leaves the choice to this movie
        entry.Publish("account.count", n(2));
        entry.PublishItem("account.name", 0, text("[LAWN] Red"));
        entry.PublishItem("account.name", 1, text("Firestorm"));
        entry.Publish("account.default", n(1));
        entry.Publish("account.picking", LumenHostValue.FromBoolean(true));
        Assert.Equal("Firestorm", entry.ReadScriptValue("label.text"));
        Assert.Equal("L", entry.ReadScriptValue("acct0.glyph.text"));
        entry.EmitFor(1, "drum.hit", text("left"), text("ka")); // back one: [LAWN] Red
        entry.EmitFor(1, "drum.hit", text("left"), text("ka")); // and Guest
        Assert.Equal("Guest", entry.ReadScriptValue("label.text"));
        entry.EmitFor(1, "drum.hit", text("left"), text("don"));
        Assert.Equal(["account.pick -1", "player.join 1"], asked); // the guest takes the left drum on this screen
        entry.Publish("player.joined", LumenHostValue.FromBoolean(true), 1);
        Assert.Equal("Play", entry.ReadScriptValue("label.text")); // in: straight to the modes
        Assert.Empty(entry.Diagnostics);
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
        rainbow.Publish("song.title", text("Senbonzakura"));
        rainbow.Publish("transition.title", LumenHostValue.FromBoolean(true));
        rainbow.Publish("transition.phase", text("cover"));
        Assert.Equal(rainbow.Labels["in_extra"], rainbow.CurrentFrame);
        advance(rainbow, 30); // the title card crosses in
        Assert.Equal("Senbonzakura", rainbow.ReadScriptValue("scene_change_song_name.text"));
        Assert.True(rainbow.TryGetInstanceBounds("scene_change_song_name", out _)); // the engine stops the preview by it
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
