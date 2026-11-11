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
}
