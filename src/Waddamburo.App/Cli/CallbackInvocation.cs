using System.Collections.Immutable;
using System.Globalization;
using Waddamburo.Lumen.Runtime;

/// <summary>A probe callback; <see cref="Tick"/> set = invoke just before that one-based tick (suffix @N).</summary>
internal sealed record CallbackInvocation(
    string Name,
    ImmutableArray<LumenHostValue> Arguments,
    int? Tick = null)
{
    private const string Prefix = "--invoke=";

    public static ImmutableArray<CallbackInvocation> Parse(string[] arguments) =>
        [.. arguments
            .Where(argument => argument.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(parseOne)];

    public static CallbackInvocation ParseExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return parseOne(Prefix + expression);
    }

    private static CallbackInvocation parseOne(string option)
    {
        var text = option[Prefix.Length..];
        int? tick = null;
        var at = text.LastIndexOf('@');
        if (at > 0 && int.TryParse(text.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            (text, tick) = (text[..at], parsed);
        var fields = text.Split('|');
        if (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0]))
            throw new ArgumentException("--invoke requires a callback name.");
        return new CallbackInvocation(
            fields[0],
            [.. fields.Skip(1).Select(parseValue)], tick);
    }

    private static LumenHostValue parseValue(string value)
    {
        if (value == "null")
            return LumenHostValue.Null;
        if (value == "undefined")
            return LumenHostValue.Undefined;
        if (value.StartsWith("b:", StringComparison.Ordinal)
            && bool.TryParse(value.AsSpan(2), out var boolean))
        {
            return LumenHostValue.FromBoolean(boolean);
        }
        if (value.StartsWith("n:", StringComparison.Ordinal)
            && double.TryParse(value.AsSpan(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number))
        {
            return LumenHostValue.FromNumber(number);
        }
        if (value.StartsWith("s:", StringComparison.Ordinal))
            return LumenHostValue.FromString(value[2..]);
        throw new ArgumentException(
            $"Invalid --invoke argument '{value}'; use b:true, n:1.5, s:text, null, or undefined.");
    }
}
