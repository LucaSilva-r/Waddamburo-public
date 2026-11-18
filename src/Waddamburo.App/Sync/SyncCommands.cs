namespace Waddamburo.App.Sync;

/// <summary>
/// Song sync from the console, to test it (two "devices" on one PC: give one --sync-home=DIR):
/// <list type="bullet">
/// <item>--sync-wait-pair: the pulling side shows a code and waits for a PC to pair.</item>
/// <item>--sync-pair=CODE: pairs with the device showing CODE (this one will serve it).</item>
/// <item>--sync-serve: lists this install's libraries and serves paired devices until Enter.</item>
/// <item>--sync-pull[=ID,ID]: mirrors the given libraries (default: the ones switched on, else all) from a paired
/// device; big removals go ahead only with --yes.</item>
/// <item>--sync-catalog: loads the libraries, received ones included, and prints each one's song count.</item>
/// </list>
/// </summary>
internal static class SyncCommands
{
    public static int Run(string[] args, string greenData, Func<Gameplay.SongLibraries> libraries)
    {
        try
        {
            return run(args, greenData, libraries);
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or InvalidDataException)
        {
            Console.Error.WriteLine($"Song sync: {exception.Message}");
            return 1;
        }
    }

    private static int run(string[] args, string greenData, Func<Gameplay.SongLibraries> libraries)
    {
        string? option(string name) => args.FirstOrDefault(arg => arg.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        if (option("--sync-home") is { } home)
        {
            SyncWire.Folder = Path.Combine(home, "sync");
            ReceivedLibraries.Root = Path.Combine(home, "received");
            Boot.DeviceName += " (" + Path.GetFileName(Path.TrimEndingDirectorySeparator(home)) + ")";
        }
        if (args.Contains("--sync-catalog"))
        {
            using var _ = libraries(); // prints each provider's songs
            return 0;
        }
        if (args.Contains("--sync-wait-pair"))
        {
            using var pairing = new PairingListener(Boot.DeviceName);
            Console.WriteLine($"{Boot.DeviceName}: pair with code {pairing.Code} (Ctrl+C to stop).");
            while (pairing.PairedWith is null && !pairing.LockedOut)
                Thread.Sleep(200);
            Console.WriteLine(pairing.PairedWith is { } paired ? $"Paired with {paired.Name}." : "Too many wrong codes.");
            return pairing.PairedWith is null ? 1 : 0;
        }
        if (option("--sync-pair") is { } code)
        {
            var devices = SyncWire.DiscoverAsync(SyncWire.PairProbe, SyncWire.PairPort, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if (devices.Count == 0)
            {
                Console.Error.WriteLine("No device is waiting to pair.");
                return 1;
            }
            foreach (var device in devices)
            {
                try
                {
                    Console.WriteLine($"Paired with {PairingListener.PairAsync(device, code).GetAwaiter().GetResult().Name}.");
                    return 0;
                }
                catch (SyncRefusedException)
                {
                    Console.Error.WriteLine($"{device.Name}: wrong code.");
                }
            }
            return 1;
        }
        if (args.Contains("--sync-serve"))
        {
            using var loaded = libraries();
            var offered = loaded.SyncLibraries(greenData);
            foreach (var library in offered)
                Console.WriteLine($"  {library.Id,-24} {library.Name}  ({string.Join(", ", library.Parts.Select(static part => part.Folder))})");
            using var server = new SyncServer(offered, Boot.DeviceName);
            Console.WriteLine($"Serving {offered.Count} libraries to paired devices; Enter stops.");
            Console.ReadLine();
            return 0;
        }
        if (args.Any(static arg => arg == "--sync-pull" || arg.StartsWith("--sync-pull=", StringComparison.Ordinal)))
        {
            var server = SyncClient.FindAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult().FirstOrDefault();
            if (server is null)
            {
                Console.Error.WriteLine("No paired device is serving.");
                return 1;
            }
            var remote = server.LibrariesAsync().GetAwaiter().GetResult();
            var wanted = option("--sync-pull")?.Split(',').ToHashSet() is { Count: > 0 } named ? named
                : SyncWire.Wanted(server.Device.Id) is { Count: > 0 } on ? on : [.. remote.Select(static library => library.Id)];
            SyncWire.SetWanted(server.Device.Id, wanted);
            foreach (var library in remote.Where(library => wanted.Contains(library.Id)))
            {
                if (ReceivedLibraries.FolderOf(server.Device.Id, library) is not { } folder)
                {
                    Console.WriteLine($"{library.Name}: this device doesn't take it.");
                    continue;
                }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var plan = server.PlanAsync(library, folder).GetAwaiter().GetResult();
                Console.WriteLine($"{library.Name}: {plan.Fetch.Count:N0} files to fetch ({plan.FetchBytes / 1048576.0:N0} MB), {plan.Remove.Count:N0} to remove, {plan.Have:N0} here.");
                if (plan.Remove.Count > plan.Have / 2 && plan.Remove.Count > 0 && !args.Contains("--yes"))
                {
                    Console.WriteLine("  More than half would be removed: skipped (--yes to allow).");
                    continue;
                }
                long fetched = 0;
                var shown = 0L;
                server.MirrorAsync(library, folder, plan, bytes =>
                {
                    var now = Interlocked.Add(ref fetched, bytes);
                    if (Environment.TickCount64 - Interlocked.Read(ref shown) > 500)
                    {
                        Interlocked.Exchange(ref shown, Environment.TickCount64);
                        Console.Write($"\r  {now / 1048576.0:N0} / {plan.FetchBytes / 1048576.0:N0} MB   ");
                    }
                }).GetAwaiter().GetResult();
                Console.WriteLine($"\r  done in {clock.Elapsed.TotalSeconds:N0} s ({fetched / 1048576.0 / Math.Max(0.1, clock.Elapsed.TotalSeconds):N1} MB/s).        ");
            }
            return 0;
        }
        Console.Error.WriteLine("Song sync: --sync-catalog, --sync-wait-pair, --sync-pair=CODE, --sync-serve or --sync-pull[=IDS] (with --sync-home=DIR for a second device).");
        return 2;
    }
}
