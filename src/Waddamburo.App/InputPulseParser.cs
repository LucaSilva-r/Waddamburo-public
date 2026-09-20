using Waddamburo.Platform.Sdl;

internal static class InputPulseParser
{
    private const string Prefix = "--press=";

    public static SdlKeyboardTimeline Parse(IEnumerable<string> arguments)
    {
        var pulses = new List<(int Tick, SdlKeyboardKey Key)>();
        foreach (var option in arguments.Where(argument => argument.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            if (option.Length == Prefix.Length)
                throw new ArgumentException("--press requires KEY@TICK[,KEY@TICK...].");
            foreach (var item in option[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = item.LastIndexOf('@');
                if (separator <= 0
                    || !tryParseKey(item[..separator], out var key)
                    || !int.TryParse(item.AsSpan(separator + 1), out var tick)
                    || tick <= 0)
                {
                    throw new ArgumentException(
                        $"Invalid input pulse '{item}'; use --press=KEY@TICK[,KEY@TICK...].");
                }
                pulses.Add((tick, key));
            }
        }
        return new SdlKeyboardTimeline(pulses);
    }

    private static bool tryParseKey(string text, out SdlKeyboardKey key) =>
        Enum.TryParse(text, ignoreCase: true, out key)
        && Enum.IsDefined(key);
}
