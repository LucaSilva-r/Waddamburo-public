using System.Collections.Immutable;

namespace Waddamburo.Lumen.Runtime;

/// <summary>Installs game-provided globals before a movie enters frame zero.</summary>
public interface ILumenHostBinding
{
    void Install(LumenHostContext context);
}

public enum LumenHostValueKind
{
    Undefined,
    Null,
    Boolean,
    Number,
    Text,
}

/// <summary>A bounded primitive value crossing the runtime/host boundary.</summary>
public readonly record struct LumenHostValue
{
    private readonly object? _value;

    private LumenHostValue(LumenHostValueKind kind, object? value)
    {
        Kind = kind;
        _value = value;
    }

    public LumenHostValueKind Kind { get; }

    public static LumenHostValue Undefined => default;

    public static LumenHostValue Null { get; } = new(LumenHostValueKind.Null, null);

    public static LumenHostValue FromBoolean(bool value) => new(LumenHostValueKind.Boolean, value);

    public static LumenHostValue FromNumber(double value) => new(LumenHostValueKind.Number, value);

    public static LumenHostValue FromString(string value) =>
        new(LumenHostValueKind.Text, value ?? throw new ArgumentNullException(nameof(value)));

    public bool AsBoolean() => Kind == LumenHostValueKind.Boolean
        ? (bool)_value!
        : throw wrongKind(LumenHostValueKind.Boolean);

    public double AsNumber() => Kind == LumenHostValueKind.Number
        ? (double)_value!
        : throw wrongKind(LumenHostValueKind.Number);

    public string AsString() => Kind == LumenHostValueKind.Text
        ? (string)_value!
        : throw wrongKind(LumenHostValueKind.Text);

    private InvalidOperationException wrongKind(LumenHostValueKind expected) =>
        new($"Host value is {Kind}, not {expected}.");
}

public readonly record struct LumenHostCall(ImmutableArray<LumenHostValue> Arguments);

public delegate LumenHostValue LumenHostCallback(LumenHostCall call);

/// <summary>Builder for one host-owned object exposed to AVM code.</summary>
public sealed class LumenHostObject
{
    private readonly Dictionary<string, LumenHostCallback> _methods = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, LumenHostCallback> Methods => _methods;

    public void RegisterMethod(string name, LumenHostCallback callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(callback);
        if (!_methods.TryAdd(name, callback))
            throw new ArgumentException($"Host method '{name}' is already registered.", nameof(name));
    }
}

/// <summary>Registration surface scoped to one Lumen player.</summary>
public sealed class LumenHostContext
{
    private readonly Dictionary<string, object?> _globals;
    private readonly Avm1Object _externalInterface;
    private bool _externalInterfaceCallRegistered;

    internal LumenHostContext(Dictionary<string, object?> globals, Avm1Object externalInterface)
    {
        _globals = globals;
        _externalInterface = externalInterface;
    }

    public void RegisterFunction(string name, LumenHostCallback callback)
    {
        validateName(name);
        ArgumentNullException.ThrowIfNull(callback);
        addGlobal(name, new Avm1NativeFunction(name, callback));
    }

    public void RegisterObject(string name, Action<LumenHostObject> configure)
    {
        validateName(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new LumenHostObject();
        configure(builder);
        var value = new Avm1Object();
        foreach (var (methodName, callback) in builder.Methods)
            value.Properties.Add(methodName, new Avm1NativeFunction($"{name}.{methodName}", callback));
        addGlobal(name, value);
    }

    /// <summary>
    /// Resolves an authored numeric constant without exposing the VM's mutable global object.
    /// The lookup is evaluated when called, after package bootstrap scripts may have run.
    /// </summary>
    public string? FindNumericGlobalName(string prefix, double value)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!double.IsFinite(value))
            return null;
        return _globals
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                && pair.Value is double number
                && number == value)
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Registers the movie-to-host endpoint used by flash.external.ExternalInterface.call.</summary>
    public void RegisterExternalInterfaceCall(LumenHostCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_externalInterfaceCallRegistered)
            throw new ArgumentException("The ExternalInterface call endpoint is already registered.", nameof(callback));
        _externalInterface.Properties["call"] = new Avm1NativeFunction("ExternalInterface.call", callback);
        _externalInterfaceCallRegistered = true;
    }

    private void addGlobal(string name, object value)
    {
        if (!_globals.TryAdd(name, value))
            throw new ArgumentException($"Global '{name}' is already registered.", nameof(name));
    }

    private static void validateName(string name) => ArgumentException.ThrowIfNullOrWhiteSpace(name);
}
