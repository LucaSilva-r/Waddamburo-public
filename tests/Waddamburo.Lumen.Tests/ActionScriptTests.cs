using Waddamburo.Formats.Lmb;
using Waddamburo.Formats.Package;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Lumen.Tests;

/// <summary>Scripts are compiled, run by the interpreter, and judged by what they report to the host.</summary>
public sealed class ActionScriptTests
{
    private const string Movie = """
        {
          "format": 1, "root": "main",
          "classes": [CLASSES],
          "shapes": { "box": { "rect": [0, 0, 10, 10] } },
          "sprites": {
            "clip": { "frames": 3, "export": "Clip", "labels": { "idle": 0, "go": 2 },
                      "layers": [ { "character": "box", "keys": [ { "frame": 0 } ] } ] },
            "main": { "frames": 2, "scripts": { "0": "main.as" },
                      "layers": [ { "character": "clip", "name": "clip", "keys": [ { "frame": 0 } ] } ] }
          }
        }
        """;

    private static (LumenPlayer Player, List<string> Reports) run(string script, string? classes = null)
    {
        var reports = new List<string>();
        var files = new Dictionary<string, string> { ["main.as"] = script, ["classes.as"] = classes ?? "" };
        var assembled = MoviePackage.Assemble(Movie.Replace("CLASSES", classes is null ? "" : "\"classes.as\""), file => files[file]);
        var read = LmbSemanticReader.Read(LmbFile.Parse(assembled.Lmb), validationContext: new LmbSemanticValidationContext(0));
        Assert.Empty(read.Diagnostics);
        var player = new LumenPlayer(read.Value, 1280, 720, hostBinding: new Host(context => context.RegisterFunction("report", call =>
        {
            reports.Add(string.Join(",", call.Arguments.Select(static value => value.Kind switch
            {
                LumenHostValueKind.Number => value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
                LumenHostValueKind.Text => value.AsString(),
                LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
                var kind => kind.ToString().ToLowerInvariant(),
            })));
            return LumenHostValue.FromNumber(reports.Count);
        })));
        Assert.True(player.Diagnostics.IsEmpty, string.Join(" | ", player.Diagnostics.Select(static each => $"{each.Code}: {each.Message}")));
        return (player, reports);
    }

    [Fact]
    public void ArtPreviewSkipsScriptsButAdvancesTimelines()
    {
        var assembled = MoviePackage.Assemble(Movie.Replace("CLASSES", ""), _ => "this.clip._visible = false; stop();");
        var movie = LmbSemanticReader.Read(LmbFile.Parse(assembled.Lmb), validationContext: new LmbSemanticValidationContext(0)).Value;
        var normal = new LumenPlayer(movie, 1280, 720);
        var art = new LumenPlayer(movie, 1280, 720, scriptsEnabled: false);
        Assert.False(normal.InspectDisplayList().Single(clip => clip.Name == "clip").Visible);
        Assert.True(art.InspectDisplayList().Single(clip => clip.Name == "clip").Visible);
        art.Advance();
        Assert.Equal(1, art.CurrentFrame);
        Assert.True(art.InspectDisplayList().Single(clip => clip.Name == "clip").Visible);
    }

    [Fact]
    public void EnterFrameRunsRepeatedlyEvenWhenTimelineIsStopped()
    {
        var (player, reports) = run("this.onEnterFrame = function() { report('tick'); }; stop();");
        Assert.Empty(reports);
        player.Advance();
        player.Advance();
        player.Advance();
        Assert.Equal(new[] { "tick", "tick", "tick" }, reports);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Theory]
    [InlineData("report(1 + 2 * 3, (1 + 2) * 3, 7 % 4, 10 / 4, -3, 2 - 5);", "7,9,3,2.5,-3,-3")]
    // Flash 8's String methods: negative starts from the end where Flash takes them, a missing length to the end.
    [InlineData("var s = '1002340'; report(s.substr(4), s.substr(-3), s.substr(0, 4), s.substr(2, 99));", "340,340,1002,02340")]
    [InlineData("var s = 'Waddamburo'; report(s.substring(4, 1), s.slice(-4), s.slice(1, -4), s.indexOf('d'), s.lastIndexOf('d'), s.indexOf('z'));", "add,buro,addam,2,3,-1")]
    [InlineData("report('Don'.toUpperCase(), 'KA'.toLowerCase(), 'abc'.substring(1));", "DON,ka,bc")]
    [InlineData("report('a' + 1 + 2, 1 + 2 + 'a', \"x\\ty\", 0x10, 1e3, .5);", "a12,3a,x\ty,16,1000,0.5")]
    [InlineData("report(1 < 2, 2 <= 2, 3 > 4, 4 >= 5, 1 == '1', 1 === '1', 1 != 2, 1 !== 1);", "true,true,false,false,true,false,true,false")]
    [InlineData("report(6 & 3, 6 | 3, 6 ^ 3, ~5, 1 << 4, -16 >> 2, 16 >>> 2, !0);", "2,7,5,-6,16,-4,4,true")]
    [InlineData("var a = 0; var b = (a && report('no')) || 'right'; report(b, a || 0, 1 && 2, null, undefined);", "right,0,2,null,undefined")]
    [InlineData("var n = 5; n += 2; n *= 3; n -= 1; n /= 4; var a = n++; var b = ++n; var c = n--; var d = --n; report(a, b, c, d, n);", "5,7,7,5,5")]
    // Arguments are evaluated last to first, as Flash's own compiler does.
    [InlineData("var n = 0; report(n++, n++, n++);", "2,1,0")]
    [InlineData("var t = 0; for (var i = 0; i < 10; i++) { if (i == 2) continue; if (i == 5) break; t += i; } report(t, i);", "8,5")]
    [InlineData("var i = 0, t = ''; while (i < 3) { t += i; i++; } do { t += 'x'; } while (false); report(t);", "012x")]
    [InlineData("function f(a, b) { return a * b; } var g = function(x) { return f(x, x) + 1; }; report(f(3, 4), g(3), h());"
        + " function h() { return 'hoisted'; }", "12,10,hoisted")]
    [InlineData("function counter() { var n = 0; return function() { n++; return n; }; } var c = counter(); c(); c(); report(c(), counter()(), [counter()][0]());", "3,1,1")]
    [InlineData("var o = { a: 1, 'b c': [10, 20, 30] }; o.d = o.a + 1; o['b c'][1] += 5; o.a++; report(o.a, o.d, o['b c'][1], o['b c'].length);", "2,2,25,3")]
    [InlineData("var o = { n: 2, twice: function() { return this.n * 2; } }; report(o.twice(), [1, 2, 3].length, new Array(1, 2).length);", "4,3,2")]
    [InlineData("function pick(v) { switch (v) { case 1: return 'one'; case 2: case 3: var r = 'few'; break; default: r = 'many'; } return r; }"
        + " report(pick(1), pick(2), pick(3), pick(9));", "one,few,few,many")]
    [InlineData("var t = ''; for (var i = 0; i < 4; i++) { switch (i) { case 1: continue; case 2: t += 'b'; break; } t += i; } report(t);", "0b23")]
    [InlineData("report(true ? 1 : 2, false ? 1 : 2, 1 ? (0 ? 'a' : 'b') : 'c'); // comment\n/* block\ncomment */", "1,2,b")]
    [InlineData("var x: Number = 3; function typed(a: String, b: flash.geom.Point): Void { report(a, x); } typed('t');", "t,3")]
    [InlineData("var o = { a: 1 }; delete o.a; report(o.a, Math.floor(2.7), Math.abs(-4));", "undefined,2,4")]
    // Objects compare by reference: the same one is equal, two alike are not.
    [InlineData("var a = { n: 1 }; var b = { n: 1 }; var m = {}; m[3] = a; report(a == a, a === a, a == b, m[3] == a, a != b, [] == []);", "true,true,false,true,true,false")]
    [InlineData("var d = new Date(2026, 10, 27, 21, 4, 9); report(d.getFullYear(), d.getMonth(), d.getDate(), d.getDay(), d.getHours(), d.getMinutes(), d.getSeconds());", "2026,10,27,5,21,4,9")]
    [InlineData("var d = new Date(2026, 0, 31, 23, 59); var e = new Date(d.getTime() + 60000); report(e.getMonth(), e.getDate(), e.getHours(), e.getMinutes());", "1,1,0,0")]
    [InlineData("var r = report('first'); report(r, report('second') + 10);", "first|second|1,12")]
    // A function gets this and arguments only when it names them; a nested one gets its own.
    [InlineData("function count() { return arguments.length; } function plain(a) { return a; }"
        + " function outer() { var inner = function() { return arguments.length; }; return inner(1, 2, 3); }"
        + " var o = { n: 4, get: function() { return this.n; }, none: function() { return 'n'; } };"
        + " report(count(1, 2), plain('p'), outer(), o.get(), o.none());", "2,p,3,4,n")]
    public void ScriptsRun(string script, string expected)
    {
        var (_, reports) = run(script);
        Assert.Equal(expected, string.Join("|", reports));
    }

    [Fact]
    public void ClassesBindToClipsAndCallbacksReachTheirMethods()
    {
        const string classes = """
            import flash.external.ExternalInterface;
            class game.Base {
                var steps = 0;
                static var made = 0;
                function Base(start) { steps = start; made++; }
                function step(by) { steps += by; return steps; }
            }
            class Counter extends Base {
                var label = "counter";
                function Counter() { super(10); }
                function step(by) { return super.step(by * 2) + Base.made; } // Inherited members are named in full.
                static function describe(c) { return c.label + ":" + c.steps; }
            }
            class ClipClass extends MovieClip {
                var shown = 0;
                function onEnterFrame() { shown++; }
                function jump() { this.gotoAndStop("go"); return this._currentframe; }
            }
            Object.registerClass("Clip", ClipClass);
            """;
        var (player, reports) = run("""
            var c = new Counter();
            var first = c.step(1);
            var second = c.step(2);
            report(first, second, Counter.describe(c), Base.made);
            var self = this;
            import flash.external.ExternalInterface;
            ExternalInterface.addCallback("Jump", this, function(times) {
                return self.clip.jump() * times + self.clip.shown;
            });
            stop();
            """, classes);
        Assert.Equal("13,17,counter:16,1", Assert.Single(reports));
        player.Advance();
        player.Advance();
        Assert.True(player.TryInvokeCallback("Jump", [LumenHostValue.FromNumber(100)], out var result));
        Assert.Equal(302, result.AsNumber()); // Frame 3 (one-based) * 100 + two onEnterFrame calls.
        Assert.Equal(2, player.InstanceFrame("clip")?.Frame);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Fact]
    public void ATextFieldShowsWhatAScriptSetsInIt()
    {
        var movie = Movie.Replace("CLASSES", "").Replace("\"shapes\":", "\"texts\": { \"label\": { \"rect\": [0, 0, 200, 40], \"size\": 32, "
            + "\"align\": \"center\", \"color\": \"#ff8800\" } }, \"shapes\":")
            .Replace("\"layers\": [ { \"character\": \"clip\"", "\"layers\": [ { \"character\": \"label\", \"name\": \"label\", "
            + "\"keys\": [ { \"frame\": 0, \"x\": 50, \"y\": 60 } ] }, { \"character\": \"clip\"");
        var read = LmbSemanticReader.Read(LmbFile.Parse(MoviePackage.Assemble(movie, _ => "this.label.text = 'Time ' + 42; stop();").Lmb),
            validationContext: new LmbSemanticValidationContext(0));
        Assert.Empty(read.Diagnostics);
        var player = new LumenPlayer(read.Value, 1280, 720);
        Assert.True(player.Diagnostics.IsEmpty, string.Join(" | ", player.Diagnostics.Select(static each => $"{each.Code}: {each.Message}")));
        var text = Assert.Single(player.CreateRenderSnapshot().Quads, static quad => quad.NativeSurface is not null);
        Assert.True(Rendering.LumenTextSurface.TryParse(text.NativeSurface!.Value, out var shown, out var width, out _, out var size, out var align, out var rgb));
        Assert.Equal(("Time 42", 200f, 32f, 'c', 0xFF8800u), (shown, width, size, align, rgb));
        Assert.Equal((50f, 60f), (text.TopLeft.X, text.TopLeft.Y));
    }

    // A package's field: its own text until something gives one, the Wd state it is bound to over that, a script's over
    // both; its settings ride in the key for the host's styles, and a styled field's quad grows by the style's reach.
    [Fact]
    public void AFieldShowsItsOwnTextThenItsBindThenAScripts()
    {
        LumenPlayer play(string script)
        {
            var movie = Movie.Replace("CLASSES", "").Replace("\"shapes\":", "\"texts\": { \"label\": { \"rect\": [0, 0, 200, 40], \"style\": \"title\", "
                + "\"text\": \"ソングセレクト\", \"bind\": \"song.title\", \"fit\": \"squeeze\" } }, \"shapes\":")
                .Replace("\"layers\": [ { \"character\": \"clip\"", "\"layers\": [ { \"character\": \"label\", \"name\": \"label\", "
                + "\"keys\": [ { \"frame\": 0, \"x\": 50, \"y\": 60 } ] }, { \"character\": \"clip\"");
            var read = LmbSemanticReader.Read(LmbFile.Parse(MoviePackage.Assemble(movie, _ => script).Lmb),
                validationContext: new LmbSemanticValidationContext(0));
            return new LumenPlayer(read.Value, 1280, 720);
        }
        static (string Text, string? Settings, float Left) shown(LumenPlayer player)
        {
            var quad = Assert.Single(player.CreateRenderSnapshot().Quads, static quad => quad.NativeSurface is not null);
            Assert.True(Rendering.LumenTextSurface.TryParse(quad.NativeSurface!.Value, out var text, out _, out _, out _, out _, out _, out var settings));
            return (text, Rendering.LumenTextSurface.Settings(settings), quad.TopLeft.X);
        }
        Rendering.LumenTextSurface.Margin = static _ => 5;
        try
        {
            var player = play("stop();");
            var (text, settings, left) = shown(player);
            Assert.Equal(("ソングセレクト", 45f), (text, left));
            Assert.Contains("\"style\":\"title\"", settings);
            player.Publish("song.title", LumenHostValue.FromString("Senbonzakura"));
            Assert.Equal("Senbonzakura", shown(player).Text);
            Assert.Contains("song.title", player.ProviderNames);
            Assert.Equal("Time", shown(play("this.label.text = 'Time'; stop();")).Text);
        }
        finally
        {
            Rendering.LumenTextSurface.Margin = null;
        }
    }

    [Theory]
    [InlineData("\"bad\": 1", "\"bad\" is no text setting")]
    [InlineData("\"fit\": \"stretch\"", "\"fit\" is clip, shrink, squeeze or wrap")]
    [InlineData("\"layers\": [ { \"color\": \"red\" } ]", "colour like")]
    public void ATextFieldsSettingsAreChecked(string setting, string message)
    {
        var movie = Movie.Replace("CLASSES", "").Replace("\"shapes\":", "\"texts\": { \"label\": { \"rect\": [0, 0, 200, 40], " + setting + " } }, \"shapes\":");
        Assert.Contains(message, Assert.Throws<FormatException>(() => MoviePackage.Assemble(movie, _ => "stop();")).Message);
    }

    [Theory]
    [InlineData("var a = ;", "main.as(1,9): Unexpected ';'.")]
    [InlineData("\n  foo(1;", "main.as(2,8): Expected ',' but found ';'.")]
    [InlineData("for (var k in o) {}", "main.as(1,12): 'for..in' is not supported yet.")]
    [InlineData("try { } catch (e) { }", "main.as(1,1): 'try' is not supported yet.")]
    [InlineData("var s = 'open", "main.as(1,14): This string is never closed.")]
    [InlineData("break;", "main.as(1,1): 'break' is outside a loop.")]
    [InlineData("1 = 2;", "main.as(1,3): Only a variable or a member can be assigned to.")]
    public void MistakesNameTheirPlace(string script, string expected) =>
        Assert.Equal(expected, Assert.Throws<ActionScriptException>(() => run(script)).Message);

    private sealed class Host(Action<LumenHostContext> install) : ILumenHostBinding
    {
        public void Install(LumenHostContext context) => install(context);
    }

    [Fact]
    public void AMovieReadsAndHearsTheProvider()
    {
        var (player, reports) = run("""
            report("start", Wd.has("play.score"), Wd.get("play.score"));
            Wd.on("play.score", function(score) { report("score", score, Wd.get("play.score")); });
            Wd.on("note.judged", function(result, big) { report("judged", result, big); });
            report(Wd.get("play.scroe"));
            stop();
            """);
        Assert.True(player.UsesProvider);
        player.Publish("play.score", LumenHostValue.FromNumber(100));
        player.Publish("play.score", LumenHostValue.FromNumber(100)); // no change: not told again
        player.Emit("note.judged", LumenHostValue.FromString("good"), LumenHostValue.FromBoolean(true));
        player.Emit("combo.milestone", LumenHostValue.FromNumber(100)); // nobody listens
        Assert.Equal(["start,false,undefined", "score,undefined,undefined", "undefined", "score,100,100", "judged,good,true"], reports);
        Assert.Throws<ArgumentException>(() => player.Publish("play.scroe", LumenHostValue.FromNumber(1)));
        Assert.Throws<ArgumentException>(() => player.Emit("play.score"));
    }

    [Fact]
    public void AMovieReadsAListByIndex()
    {
        var (player, reports) = run("""
            import flash.external.ExternalInterface;
            report("start", Wd.has("dan.exam.value", 0), Wd.get("dan.exam.value", 0));
            Wd.on("dan.exam.value", function(index, value) { report("value", index, value, Wd.get("dan.exam.value", index)); });
            ExternalInterface.addCallback("Late", this, function() {
                Wd.on("dan.exam.value", function(index, value) { report("late", index, value); });
                report("p2", Wd.get("dan.exam.value", 0, 2));
            });
            stop();
            """);
        player.PublishItem("dan.exam.value", 1, LumenHostValue.FromNumber(5));
        player.PublishItem("dan.exam.value", 0, LumenHostValue.FromNumber(3));
        player.PublishItem("dan.exam.value", 0, LumenHostValue.FromNumber(3)); // no change: not told again
        player.PublishItem("dan.exam.value", 0, LumenHostValue.FromNumber(9), 2);
        Assert.True(player.TryInvokeCallback("Late", []));
        player.PublishItem("dan.exam.value", 1, LumenHostValue.Undefined); // gone
        Assert.Equal(["start,false,undefined", "value,1,5,5", "value,0,3,3", "late,0,3", "late,1,5", "p2,9",
            "value,1,undefined,undefined", "late,1,undefined"], reports);
        Assert.Throws<ArgumentException>(() => player.Publish("dan.exam.value", LumenHostValue.FromNumber(1)));
        Assert.Throws<ArgumentException>(() => player.PublishItem("play.score", 0, LumenHostValue.FromNumber(1)));
    }

    [Fact]
    public void ARequestIsTakenOrRefusedWithAReason()
    {
        var (player, reports) = run("""
            import flash.external.ExternalInterface;
            ExternalInterface.addCallback("Ask", this, function(mode) {
                report(Wd.request("session.start", mode));
            });
            ExternalInterface.addCallback("AskWrong", this, function() {
                report(Wd.request("play.combo"));
            });
            stop();
            """);
        Assert.True(player.TryInvokeCallback("Ask", [LumenHostValue.FromString("dan")])); // no screen takes it here
        var asked = new List<string>();
        new Waddamburo.Lumen.Provider.WdRequests("entry").Handle("session.start", arguments =>
        {
            asked.Add(arguments[0].AsString());
            return arguments[0].AsString() == "dan" ? null : "not that one";
        }).Install([player]);
        Assert.True(player.TryInvokeCallback("Ask", [LumenHostValue.FromString("dan")]));
        Assert.True(player.TryInvokeCallback("Ask", [LumenHostValue.FromString("nope")]));
        Assert.True(player.TryInvokeCallback("AskWrong", [])); // a state, not a request
        Assert.Equal(["false", "true", "false", "false"], reports);
        Assert.Equal(["dan", "nope"], asked);
        // A screen takes only its own requests.
        Assert.Throws<ArgumentException>(() => new Waddamburo.Lumen.Provider.WdRequests("song_select").Handle("session.start", static _ => null));
        Assert.Contains("request of entry", new Waddamburo.Lumen.Provider.WdRequests("song_select").Take("session.start", []));
    }

    [Fact]
    public void AListenerHearsAValueAlreadyThere()
    {
        var (player, reports) = run("""
            import flash.external.ExternalInterface;
            ExternalInterface.addCallback("Late", this, function() {
                Wd.on("play.combo", function(combo) { report(combo); });
            });
            stop();
            """);
        Assert.True(player.UsesProvider); // its scripts name Wd: one of ours before it has asked anything
        player.Publish("play.combo", LumenHostValue.FromNumber(7));
        Assert.True(player.TryInvokeCallback("Late", []));
        Assert.Equal(["7"], reports);
    }

    [Fact]
    public void AMovieAsksForItsOwnDrumOrAPlayerByNumber()
    {
        var (player, reports) = run("""
            Wd.on("play.score", function(score) { report("mine", score); });
            Wd.on("play.score", function(score) { report("p2", score, Wd.get("play.score", 2), Wd.get("play.score")); }, 2);
            Wd.on("drum.hit", function(side, part) { report("hit2", side, part); }, 2);
            stop();
            """);
        player.Publish("play.score", LumenHostValue.FromNumber(10));
        player.Publish("play.score", LumenHostValue.FromNumber(20), 2);
        player.Emit("drum.hit", LumenHostValue.FromString("left"), LumenHostValue.FromString("don"));
        player.EmitFor(2, "drum.hit", LumenHostValue.FromString("right"), LumenHostValue.FromString("ka"));
        Assert.Equal(["mine,undefined", "p2,undefined,undefined,undefined", "mine,10", "p2,20,20,10", "hit2,right,ka"], reports);
    }

    [Fact]
    public void AMovieOfNoDrumGetsPlayerOne()
    {
        var (player, reports) = run("""
            Wd.on("play.score", function(score) { report(score, Wd.get("play.score")); });
            stop();
            """);
        player.Publish("play.score", LumenHostValue.FromNumber(2), 2);
        player.Publish("play.score", LumenHostValue.FromNumber(1), 1);
        Assert.Equal(["undefined,undefined", "1,1"], reports);
    }
}
