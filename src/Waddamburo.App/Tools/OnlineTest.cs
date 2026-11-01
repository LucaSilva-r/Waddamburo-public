using System.Diagnostics;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Online;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Tools;

/// <summary>
/// --online-test opens a room and --online-test=CODE joins one, with no game window: connects to the other player and
/// reports the route and round trip, so a network can be checked without playing. The account is the stored default and
/// the server config.cfg's; WADDAMBURO_ONLINE_TOKEN and WADDAMBURO_ONLINE_SERVER replace them for this run only, leaving
/// the player's settings and logins alone. What it says also goes to waddamburo/logs/online-test.txt.
/// </summary>
internal static class OnlineTest
{
    private static readonly TimeSpan WaitForPlayer = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RunFor = TimeSpan.FromSeconds(60);

    private static string? _logFile;

    public static int Run(ArcadeSettings arcade, AccountBook accounts, string? code, string logFolder)
    {
        try
        {
            Directory.CreateDirectory(logFolder);
            _logFile = Path.Combine(logFolder, "online-test.txt");
            File.Delete(_logFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logFile = null;
        }
        int result;
        try
        {
            result = runAsync(arcade, accounts, code).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            say($"RESULT: couldn't reach the server or sign in: {exception.Message}");
            if (exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
                say("The server refused this login: the token is wrong or wasn't set.");
            result = 7;
        }
        // A Windows release's console window closes with the program.
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Press Enter to close.");
            try
            {
                // A fresh handle: Console.In may have been set up before the console existed.
                using var input = Console.OpenStandardInput();
                input.ReadByte();
            }
            catch (IOException)
            {
                // No console to wait on.
            }
        }
        return result;
    }

    private static void say(string line)
    {
        Console.WriteLine(line);
        if (_logFile is { } file)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    private static async Task<int> runAsync(ArcadeSettings arcade, AccountBook accounts, string? code)
    {
        var token = Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_TOKEN") is { Length: > 0 } fromEnvironment ? fromEnvironment
            : accounts.Default?.Token ?? (accounts.Accounts is [var first, ..] ? first.Token : null);
        var serverOverride = Environment.GetEnvironmentVariable("WADDAMBURO_ONLINE_SERVER") is { Length: > 0 } fromVariable ? new Uri(fromVariable) : null;
        if ((serverOverride ?? arcade.Server) is not { } server || token is null)
        {
            say("RESULT: the online test needs a server and an account.");
            return 2;
        }
        var insecure = serverOverride is null && arcade.ServerInsecure;
        say($"Server: {server}");
        await using var session = new OnlineSession(ScoreClient.CreateHttp(server, insecure, token), insecure);
        var received = 0L;
        session.Link.Received += _ => Interlocked.Increment(ref received);
        session.Link.StateChanged += state => say($"Link: {state}{(session.Link.RemoteEndPoint is { } remote ? $" via {remote}" : "")}");
        if (!(code is null ? await session.HostAsync(isPublic: false).ConfigureAwait(false) : await session.JoinAsync(code).ConfigureAwait(false)))
        {
            say($"Room {code} doesn't exist or is full.");
            return 3;
        }
        say($"Room {session.Room!.Code}. This side can be reached at:");
        foreach (var candidate in session.Candidates)
            say($"  {candidate.Kind}: {candidate.EndPoint}");

        var waiting = Stopwatch.StartNew();
        while (session.Link.State is PeerLinkState.Idle or PeerLinkState.Connecting)
        {
            if (waiting.Elapsed > WaitForPlayer)
            {
                say("Nobody connected.");
                return 4;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        if (session.Link.State == PeerLinkState.Failed)
        {
            say("RESULT: couldn't connect. Neither a direct route nor a relay got through.");
            return 5;
        }

        // About what play sends: 20 small packets a second.
        var sent = 0L;
        var running = Stopwatch.StartNew();
        byte[] payload = new byte[64];
        while (running.Elapsed < RunFor && session.Link.State is PeerLinkState.Direct or PeerLinkState.Relayed)
        {
            session.Link.Send(payload);
            sent++;
            if (sent % 100 == 0)
                say($"{session.Link.State}, round trip {session.Link.RoundTrip?.TotalMilliseconds:F1} ms, sent {sent}, received {Interlocked.Read(ref received)}");
            await Task.Delay(50).ConfigureAwait(false);
        }
        var connected = session.Link.State is PeerLinkState.Direct or PeerLinkState.Relayed;
        say($"RESULT: {session.Link.State}, round trip {session.Link.RoundTrip?.TotalMilliseconds:F1} ms, sent {sent}, received {Interlocked.Read(ref received)}.");
        return connected ? 0 : 6;
    }
}
