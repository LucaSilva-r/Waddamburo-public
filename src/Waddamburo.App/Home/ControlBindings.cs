using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Home;

/// <summary>
/// The drum pads' bindings (ArcadeSettings.Controls: 8 pads, 4 per player, each a comma list of input tokens), as
/// the home controls page and the arcade service menu edit them. Devices: keyboard 0, controller 1, MIDI 2.
/// </summary>
internal static class ControlBindings
{
    // A pad takes two keyboard keys at most (controllers and MIDI: any number).
    public const int MaximumKeys = 2;

    public static string[] Inputs(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Describe(string token) =>
        SdlInput.TryParse(token, out var parsed) ? SdlApplication.Describe(parsed) : token;

    public static int DeviceOf(SdlInput input) => input.Kind switch { SdlInputKind.Key => 0, SdlInputKind.Midi => 2, _ => 1 };

    public static bool OnDevice(string token, int device) => SdlInput.TryParse(token, out var parsed) && DeviceOf(parsed) == device;

    /// <summary>
    /// The input joins the pad, leaving any other pad it hit (either player's). replace: the pad's others on this device
    /// go; else one already on the pad leaves it instead, and the oldest keys over the limit.
    /// </summary>
    public static ArcadeSettings.EquatableList Bind(ArcadeSettings.EquatableList controls, int pad, SdlInput captured, bool replace, int device)
    {
        for (var other = 0; other < controls.Count; other++)
        {
            var bound = Inputs(controls[other]);
            var kept = bound.Where(token => token != captured.Token && !(replace && other == pad && OnDevice(token, device)));
            if (other == pad && (replace || !bound.Contains(captured.Token)))
                kept = kept.Append(captured.Token);
            if (other == pad && device == 0)
            {
                var surplus = kept.Where(token => OnDevice(token, device)).SkipLast(MaximumKeys).ToHashSet();
                kept = kept.Where(token => !surplus.Contains(token));
            }
            controls = controls.With(other, string.Join(", ", kept));
        }
        return controls;
    }

    /// <summary>The input joins the button, leaving any other button it pressed (listed already: it goes).</summary>
    public static ArcadeSettings.EquatableList BindButton(ArcadeSettings.EquatableList buttons, int button, SdlInput captured, int device)
    {
        for (var other = 0; other < buttons.Count; other++)
        {
            var bound = Inputs(buttons[other]);
            var kept = bound.Where(token => token != captured.Token);
            if (other == button && !bound.Contains(captured.Token))
                kept = kept.Append(captured.Token);
            if (other == button && device == 0)
            {
                var surplus = kept.Where(token => OnDevice(token, device)).SkipLast(MaximumKeys).ToHashSet();
                kept = kept.Where(token => !surplus.Contains(token));
            }
            buttons = buttons.With(other, string.Join(", ", kept));
        }
        return buttons;
    }

    /// <summary>The player's pads get their default inputs of this device back (taken from wherever they are now).</summary>
    public static ArcadeSettings.EquatableList Reset(ArcadeSettings.EquatableList controls, int player, int device)
    {
        var defaults = new ArcadeSettings().Controls;
        var restored = Enumerable.Range(player * 4, 4).SelectMany(pad => Inputs(defaults[pad]).Where(token => OnDevice(token, device))).ToHashSet();
        for (var pad = 0; pad < controls.Count; pad++)
        {
            var mine = pad / 4 == player;
            var kept = Inputs(controls[pad]).Where(token => !restored.Contains(token) && !(mine && OnDevice(token, device)));
            controls = controls.With(pad, string.Join(", ", mine ? kept.Concat(Inputs(defaults[pad]).Where(token => OnDevice(token, device))) : kept));
        }
        return controls;
    }
}
