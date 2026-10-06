using Waddamburo.Platform.Sdl;

namespace Waddamburo.Architecture.Tests;

public sealed class InputBindingsTests
{
    [Fact]
    public void SeveralInputsOfAnyKindHitOnePad()
    {
        var warnings = new List<string>();
        var bindings = SdlInputBindings.Parse(["d, S, pad1:dpad_left, pad2:left_trigger, midi:31, nonsense, pad9:south", "f, d"],
            warn: warnings.Add);
        foreach (var token in new[] { "d", "s", "pad1:dpad_left", "pad2:left_trigger", "midi:31" })
        {
            Assert.True(SdlInput.TryParse(token, out var input));
            Assert.Equal(token, input.Token);
            Assert.Equal(SdlKeyboardKey.D, bindings.Pad(input));
        }
        Assert.True(SdlInput.TryParse("f", out var f));
        Assert.Equal(SdlKeyboardKey.F, bindings.Pad(f));
        Assert.True(SdlInput.TryParse("pad2:dpad_left", out var otherPad));
        Assert.Null(bindings.Pad(otherPad));
        Assert.Equal(["unknown input 'nonsense'", "unknown input 'pad9:south'", "input 'd' is listed twice"], warnings);
        // Sticks are not buttons.
        Assert.False(SdlInput.TryParse("pad1:leftx", out _));
        Assert.False(SdlInput.TryParse("999", out _));
        Assert.False(SdlInput.TryParse("midi:128", out _));
    }

    [Fact]
    public void ButtonsReadAsTheirOwnKeyWhateverIsBoundToThem()
    {
        // The settings' defaults (ArcadeSettings.Buttons), with the scores button moved to O.
        var bindings = SdlInputBindings.Parse([],
            ["backspace", "tab, pad1:back", "o, pad1:north", "p, pad1:west", "e, pad1:right_shoulder", "q, pad1:left_shoulder", "return, pad1:south"]);
        foreach (var (token, button) in new[] { ("backspace", SdlKeyboardKey.Backspace), ("tab", SdlKeyboardKey.Tab),
            ("return", SdlKeyboardKey.Enter), ("pad1:south", SdlKeyboardKey.Enter), ("o", SdlKeyboardKey.L),
            ("pad1:north", SdlKeyboardKey.L), ("pad1:left_shoulder", SdlKeyboardKey.Q) })
        {
            Assert.True(SdlInput.TryParse(token, out var input), token);
            Assert.Equal(button, bindings.Button(input));
        }
        Assert.True(SdlInput.TryParse("l", out var l));
        Assert.Null(bindings.Button(l));
    }

    [Fact]
    public void WithoutSettingsEachPadIsTheKeyItIsNamedAfter()
    {
        foreach (var pad in SdlInputBindings.Pads)
        {
            Assert.True(SdlInput.TryParse(pad.ToString(), out var key));
            Assert.Equal(pad, SdlInputBindings.Keyboard.Pad(key));
        }
    }

    [Fact]
    public void MidiNoteOnsAreFoundThroughRunningStatusAndRealTimeBytes()
    {
        var parser = new MidiNoteParser();
        byte[] stream =
        [
            0x99, 36, 100,       // note on
            35, 0xF8, 80,        // running status, a clock byte inside the message
            36, 0,               // velocity 0: a note off
            0x89, 36, 64,        // note off
            0xC9, 5,             // program change: one data byte
            0xF0, 1, 2, 3, 0xF7, // system exclusive
            0x90, 31, 1,
        ];
        Assert.Equal([36, 35, 31], stream.Select(parser.Feed).OfType<int>());
    }
}
