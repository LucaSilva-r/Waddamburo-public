using System.Globalization;

namespace Waddamburo.Lumen.Runtime;

/// <summary>
/// A script object or clip with a cheap number for hashing: the pending-write tables key on targets, and the runtime's
/// identity hash took its slow path for every fresh object (a script's arrays and small objects, made each frame).
/// </summary>
internal interface IAvm1Identity
{
    int AvmId { get; }
}

internal static class Avm1Ids
{
    private static int s_next;

    // ponytail: not atomic; two threads racing only share a number, which a hash tolerates.
    public static int Next() => ++s_next;
}

internal enum Avm1Kind : byte
{
    Undefined,
    Null,
    Boolean,
    Number,
    String,
    Object,
}

/// <summary>
/// One ActionScript value, held by value: a number or a boolean never allocates (as an <c>object</c> each was a box, the
/// larger part of a script's garbage: every eased position, alpha and scale). <c>default</c> is undefined. A string or an
/// object (a clip, a function, an <see cref="Avm1Object"/>, a host's globals) is the one reference it carries.
/// </summary>
internal readonly struct Avm1Value
{
    private readonly object? _reference;
    private readonly double _number;

    public Avm1Kind Kind { get; }

    private Avm1Value(Avm1Kind kind, object? reference, double number)
    {
        Kind = kind;
        _reference = reference;
        _number = number;
    }

    public static Avm1Value Undefined => default;

    public static Avm1Value Null => new(Avm1Kind.Null, null, 0);

    public static Avm1Value True => new(Avm1Kind.Boolean, null, 1);

    public static Avm1Value False => new(Avm1Kind.Boolean, null, 0);

    public static implicit operator Avm1Value(double number) => new(Avm1Kind.Number, null, number);

    public static implicit operator Avm1Value(bool boolean) => new(Avm1Kind.Boolean, null, boolean ? 1 : 0);

    /// <summary>A string; null is null.</summary>
    public static implicit operator Avm1Value(string? text) => text is null ? Null : new(Avm1Kind.String, text, 0);

    public static implicit operator Avm1Value(Avm1Object? value) => value is null ? Null : new(Avm1Kind.Object, value, 0);

    /// <summary>An object known not to be null, a string or a box (a clip, a host table): no type test.</summary>
    public static Avm1Value Wrap(object value) => new(Avm1Kind.Object, value, 0);

    /// <summary>Any value in its old object form: null, <see cref="Avm1Undefined"/>, a box, a string, or an object.</summary>
    public static Avm1Value Of(object? value) => value switch
    {
        null => Null,
        Avm1Value already => already, // one boxed by mistake keeps its meaning
        Avm1Undefined => Undefined,
        bool boolean => boolean,
        double number => number,
        string text => new(Avm1Kind.String, text, 0),
        _ => new(Avm1Kind.Object, value, 0),
    };

    public bool IsUndefined => Kind == Avm1Kind.Undefined;

    public bool IsNull => Kind == Avm1Kind.Null;

    /// <summary>Undefined or null.</summary>
    public bool IsNullish => Kind <= Avm1Kind.Null;

    public bool IsNumber => Kind == Avm1Kind.Number;

    public bool IsString => Kind == Avm1Kind.String;

    public bool IsBoolean => Kind == Avm1Kind.Boolean;

    public bool IsObject => Kind == Avm1Kind.Object;

    /// <summary>The number of a Number value (0 for any other).</summary>
    public double Number => Kind == Avm1Kind.Number ? _number : 0;

    /// <summary>The boolean of a Boolean value (false for any other).</summary>
    public bool Boolean => Kind == Avm1Kind.Boolean && _number != 0;

    /// <summary>The text of a String value, else null.</summary>
    public string? String => Kind == Avm1Kind.String ? (string)_reference! : null;

    /// <summary>The object of an Object value, else null.</summary>
    public object? Object => Kind == Avm1Kind.Object ? _reference : null;

    /// <summary>The reference it carries (a string or an object), else null: what identifies it as a member's target.</summary>
    public object? Reference => _reference;

    public bool TryGetNumber(out double number)
    {
        number = _number;
        return Kind == Avm1Kind.Number;
    }

    /// <summary>The old object form (a number boxes): for paths that are not hot, and the public script readers.</summary>
    public object? Boxed => Kind switch
    {
        Avm1Kind.Undefined => Avm1Undefined.Instance,
        Avm1Kind.Null => null,
        Avm1Kind.Boolean => Avm1Interpreter.Box(_number != 0),
        Avm1Kind.Number => Avm1Interpreter.Box(_number),
        _ => _reference,
    };

    public override string ToString() => Kind switch
    {
        Avm1Kind.Undefined => "undefined",
        Avm1Kind.Null => "null",
        Avm1Kind.Boolean => _number != 0 ? "true" : "false",
        Avm1Kind.Number => _number.ToString("G15", CultureInfo.InvariantCulture),
        Avm1Kind.String => (string)_reference!,
        _ => _reference?.ToString() ?? "",
    };
}
