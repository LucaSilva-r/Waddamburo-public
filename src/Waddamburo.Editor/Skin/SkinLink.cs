using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waddamburo.Lumen.Provider;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>
/// The live link between a running game and the skin editor it started (F7 in the game): one JSON line a message
/// over a loopback socket, game to editor. <c>{"t":"screen","k":"song-select"}</c> when the game shows a screen (the
/// skin's key), then for each movie on it (by skin folder) what the provider gives it:
/// <c>{"t":"s|i|e","m":"song_select/song_select","n":"row.title","d":0,"x":3,"v":["Senbonzakura"]}</c> (a state, a
/// list's place, an event; null is "no value").
/// </summary>
public static class SkinLinkWire
{
    public static string Screen(string key) => new JsonObject { ["t"] = "screen", ["k"] = key }.ToJsonString();

    /// <summary>Editor to game: show this screen (a skin key).</summary>
    public static string Show(string key) => new JsonObject { ["t"] = "show", ["k"] = key }.ToJsonString();

    /// <summary>Game to editor: a sentence to show (why a screen could not be shown).</summary>
    public static string Notice(string text) => new JsonObject { ["t"] = "notice", ["k"] = text }.ToJsonString();

    /// <summary>A show request or a notice: its type and text; null when the line is something else.</summary>
    public static (string Type, string Text)? Command(string line)
    {
        try
        {
            var node = JsonNode.Parse(line)!.AsObject();
            return node["t"]?.GetValue<string>() is "show" or "notice" ? (node["t"]!.GetValue<string>(), node["k"]!.GetValue<string>()) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    public static string Provided(string movie, LumenProvided provided) => new JsonObject
    {
        ["t"] = provided.Kind switch { WdKind.State => "s", WdKind.List => "i", _ => "e" },
        ["m"] = movie,
        ["n"] = provided.Name,
        ["d"] = provided.Drum,
        ["x"] = provided.Index,
        ["v"] = new JsonArray([.. provided.Values.Select(static value => value.Kind switch
        {
            LumenHostValueKind.Number => JsonValue.Create(value.AsNumber()),
            LumenHostValueKind.Text => JsonValue.Create(value.AsString()),
            LumenHostValueKind.Boolean => (JsonNode?)JsonValue.Create(value.AsBoolean()),
            _ => null,
        })]),
    }.ToJsonString();

    /// <summary>A line read back: a screen (Movie null) or a movie's delivery; false for one that does not read.</summary>
    public static bool TryRead(string line, out string? screen, out string? movie, out LumenProvided provided)
    {
        (screen, movie, provided) = (null, null, default);
        try
        {
            var node = JsonNode.Parse(line)!.AsObject();
            var type = node["t"]!.GetValue<string>();
            if (type == "screen")
            {
                screen = node["k"]!.GetValue<string>();
                return true;
            }
            var kind = type switch { "s" => WdKind.State, "i" => WdKind.List, "e" => WdKind.Event, _ => (WdKind?)null };
            if (kind is null || WdNames.Find(node["n"]!.GetValue<string>()) is not { } name || name.Kind != kind)
                return false;
            movie = node["m"]!.GetValue<string>();
            provided = new(kind.Value, name.Name, node["d"]!.GetValue<int>(), node["x"]!.GetValue<int>(),
                [.. node["v"]!.AsArray().Select(static value => value switch
                {
                    JsonValue number when number.TryGetValue<double>(out var parsed) => LumenHostValue.FromNumber(parsed),
                    JsonValue text when text.TryGetValue<string>(out var got) => LumenHostValue.FromString(got),
                    JsonValue flag when flag.TryGetValue<bool>(out var on) => LumenHostValue.FromBoolean(on),
                    _ => LumenHostValue.Undefined,
                })]);
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return false;
        }
    }
}

/// <summary>The game's end: listens on a loopback port, sends lines to the editor that joins (one at a time).</summary>
public sealed class SkinLinkServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly BlockingCollection<string> _outgoing = new(new ConcurrentQueue<string>(), 20000);
    private readonly ConcurrentQueue<string> _incoming = new();

    /// <summary>A line the editor sent (a show request), oldest first; false when none waits.</summary>
    public bool TryTake(out string line) => _incoming.TryDequeue(out line!);
    private volatile bool _joined;
    private TcpClient? _client;

    public SkinLinkServer()
    {
        _listener.Start();
        new Thread(accept) { IsBackground = true, Name = "Skin link" }.Start();
        new Thread(write) { IsBackground = true, Name = "Skin link writer" }.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>An editor is joined.</summary>
    public bool Connected => _client?.Connected == true;

    /// <summary>True once after an editor joins (the game then sends it everything it holds).</summary>
    public bool TakeJoined()
    {
        if (!_joined) return false;
        _joined = false;
        return true;
    }

    /// <summary>Queued for the editor; dropped when none is joined or it falls far behind.</summary>
    public void Send(string line)
    {
        if (Connected) _outgoing.TryAdd(line);
    }

    private void accept()
    {
        try
        {
            while (true)
            {
                var client = _listener.AcceptTcpClient();
                Interlocked.Exchange(ref _client, client)?.Dispose();
                while (_outgoing.TryTake(out _)) { }
                _joined = true;
                new Thread(() => read(client)) { IsBackground = true, Name = "Skin link reader" }.Start();
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void read(TcpClient client)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
                _incoming.Enqueue(line);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void write()
    {
        foreach (var line in _outgoing.GetConsumingEnumerable())
        {
            try
            {
                if (_client is { Connected: true } client)
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    client.GetStream().Write(bytes);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                Interlocked.Exchange(ref _client, null)?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _outgoing.CompleteAdding();
        _client?.Dispose();
    }
}

/// <summary>
/// The editor's end: reads the game's lines and keeps, per movie (skin folder), what the game's provider holds for it,
/// handing it on to the editor's own players of that movie (the scene view, the live window, the component editor),
/// which then show what the game shows: the real song list, the real score while a song plays.
/// </summary>
public sealed class SkinLive : IDisposable
{
    private readonly TcpClient _client = new();
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly Dictionary<string, Dictionary<(WdKind Kind, string Name, int Drum, int Index), LumenHostValue>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WeakReference<LumenPlayer>>> _players = new(StringComparer.Ordinal);

    public SkinLive(int port)
    {
        _client.Connect(IPAddress.Loopback, port);
        new Thread(read) { IsBackground = true, Name = "Skin link reader" }.Start();
    }

    /// <summary>The screen the game shows (a skin screen key), or null before it said.</summary>
    public string? Screen { get; private set; }

    public bool Connected { get; private set; } = true;

    /// <summary>Raised when the game shows another screen.</summary>
    public event Action<string>? ScreenChanged;

    /// <summary>Raised with what the game says it could not do.</summary>
    public event Action<string>? Notice;

    /// <summary>Asks the game to show a screen (a skin key); it says so if it cannot.</summary>
    public void Show(string key)
    {
        if (!Connected) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(SkinLinkWire.Show(key) + "\n");
            _client.GetStream().Write(bytes);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Connected = false;
        }
    }

    /// <summary>The game has given this movie something (it is on the game's screen, or was).</summary>
    public bool Holds(string movie) => _held.ContainsKey(movie);

    private void read()
    {
        try
        {
            using var reader = new StreamReader(_client.GetStream(), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
                _incoming.Enqueue(line);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
        Connected = false;
    }

    /// <summary>
    /// Takes what arrived (on the editor's thread, once a tick) and passes it to the players that show it: a state by its
    /// last value only, events in order, within a few milliseconds (the rest waits for the next tick, so a song pouring
    /// values in never holds the editor up).
    /// </summary>
    public void Drain()
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var batch = new ProvidedBatch();
        while (budget.Elapsed.TotalMilliseconds < 4 && _incoming.TryDequeue(out var line))
        {
            if (SkinLinkWire.Command(line) is ("notice", var text))
            {
                Notice?.Invoke(text);
                continue;
            }
            if (!SkinLinkWire.TryRead(line, out var screen, out var movie, out var provided))
                continue;
            if (screen is not null)
            {
                foreach (var (each, given) in batch.Take()) take(each, given);
                Screen = screen;
                ScreenChanged?.Invoke(screen);
                continue;
            }
            batch.Add(movie!, provided);
        }
        foreach (var (each, given) in batch.Take()) take(each, given);
    }

    private void take(string movie, LumenProvided provided)
    {
        var held = _held.TryGetValue(movie, out var known) ? known : _held[movie] = [];
        if (provided.Kind != WdKind.Event)
        {
            var key = (provided.Kind, provided.Name, provided.Drum, provided.Index);
            if (provided.Values[0].Kind == LumenHostValueKind.Undefined) held.Remove(key);
            else held[key] = provided.Values[0];
        }
        if (_players.TryGetValue(movie, out var players))
        {
            players.RemoveAll(static reference => !reference.TryGetTarget(out _));
            foreach (var reference in players)
                if (reference.TryGetTarget(out var player))
                    give(player, provided);
        }
    }

    /// <summary>A player of this movie follows the game from now: what the game holds for it at once, then each change.</summary>
    public void Follow(LumenPlayer player, string movie)
    {
        player.RequestHandler = static (_, _) => null; // the game answers its own movie's requests
        if (_held.TryGetValue(movie, out var held))
            foreach (var ((kind, name, drum, index), value) in held)
                give(player, new(kind, name, drum, index, [value]));
        (_players.TryGetValue(movie, out var list) ? list : _players[movie] = []).Add(new(player));
    }

    private static void give(LumenPlayer player, LumenProvided provided)
    {
        switch (provided.Kind)
        {
            case WdKind.State: player.Publish(provided.Name, provided.Values[0], provided.Drum); break;
            case WdKind.List: player.PublishItem(provided.Name, provided.Index, provided.Values[0], provided.Drum); break;
            default: player.EmitFor(provided.Drum, provided.Name, provided.Values); break;
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// What movies were given over a moment, in the order given, a state or list place kept only where it was last given
/// (a song changes some every tick): scripts react to that order (a count before its rows), so it is kept.
/// </summary>
public sealed class ProvidedBatch
{
    private readonly List<(string Movie, LumenProvided Provided)> _items = [];
    private readonly Dictionary<(string Movie, WdKind Kind, string Name, int Drum, int Index), int> _last = [];

    public int Count => _items.Count;

    public void Add(string movie, LumenProvided provided)
    {
        if (provided.Kind != WdKind.Event)
            _last[(movie, provided.Kind, provided.Name, provided.Drum, provided.Index)] = _items.Count;
        _items.Add((movie, provided));
    }

    /// <summary>Everything in order, an older value of a state left out; the batch is then empty.</summary>
    public List<(string Movie, LumenProvided Provided)> Take()
    {
        var taken = new List<(string, LumenProvided)>(_last.Count);
        for (var index = 0; index < _items.Count; index++)
        {
            var (movie, provided) = _items[index];
            if (provided.Kind == WdKind.Event || _last[(movie, provided.Kind, provided.Name, provided.Drum, provided.Index)] == index)
                taken.Add((movie, provided));
        }
        _items.Clear();
        _last.Clear();
        return taken;
    }
}
