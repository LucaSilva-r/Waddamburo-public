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
    [InlineData("var r = report('first'); report(r, report('second') + 10);", "first|second|1,12")]
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
        Assert.Equal(["start,false,undefined", "undefined", "score,100,100", "judged,good,true"], reports);
        Assert.Throws<ArgumentException>(() => player.Publish("play.scroe", LumenHostValue.FromNumber(1)));
        Assert.Throws<ArgumentException>(() => player.Emit("play.score"));
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
        Assert.False(player.UsesProvider);
        player.Publish("play.combo", LumenHostValue.FromNumber(7));
        Assert.True(player.TryInvokeCallback("Late", []));
        Assert.Equal(["7"], reports);
    }
}
