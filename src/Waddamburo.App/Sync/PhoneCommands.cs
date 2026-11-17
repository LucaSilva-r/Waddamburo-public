using System.Diagnostics;
using System.Net;
using Waddamburo.App.Gameplay;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Sync;

/// <summary>Send and receive from the console (--send-to-phone, --phone-receiver): the screens' work without a window.</summary>
internal static class PhoneCommands
{
    /// <summary>Acts as a device: receives into <paramref name="root"/> (Green/, Songs/) until killed, accepting everything.</summary>
    public static int Receive(string root)
    {
        ReleaseConsole.OpenForTool();
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        using var receiver = new PhoneReceiver(root, PhoneReceiver.Targets(root), Environment.MachineName);
        Console.WriteLine($"Receiving into {root}: code {receiver.Code} at {string.Join(", ", receiver.Addresses())}.");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>
    /// Sends to the device <paramref name="target"/> names (CODE or CODE@ADDRESS): the songs <paramref name="query"/>
    /// matches (Song Select's search; empty: all) and, with <paramref name="green"/>, Green's game data.
    /// </summary>
    public static int Send(string target, string? query, bool green, string greenData, Func<SongLibraries> libraries)
    {
        ReleaseConsole.OpenForTool();
        var code = target.Split('@')[0];
        IPAddress address;
        if (target.Split('@') is [_, var given])
            address = IPAddress.Parse(given);
        else
        {
            var phones = PhoneSync.DiscoverAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            foreach (var phone in phones)
                Console.WriteLine($"Found {phone.Name} at {phone.Address}.");
            if (phones is not [var only])
            {
                Console.Error.WriteLine(phones.Count == 0 ? "No device answered: is Waddamburo receiving on it, on the same network?"
                    : "Several devices answered: give one, --send-to-phone=CODE@ADDRESS.");
                return 1;
            }
            address = only.Address;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var device = PhoneSync.Address(address, code);
        var clock = Stopwatch.StartNew();
        long sent = 0, total = 0;
        void shown(long bytes)
        {
            var now = Interlocked.Add(ref sent, bytes);
            Console.Write($"\r{gb(now)} of {gb(total)}, {now / 1048576.0 / Math.Max(clock.Elapsed.TotalSeconds, 0.001):F0} MB/s   ");
        }
        if (green)
        {
            var plan = PhoneSync.GreenPlan(greenData);
            var missing = PhoneSync.MissingAsync(http, device, plan).GetAwaiter().GetResult();
            total = missing.Sum(static file => file.Size);
            Console.WriteLine($"Green: {plan.Count:N0} files, the device lacks {missing.Count:N0} ({gb(total)}).");
            if (total > PhoneSync.FreeSpaceAsync(http, device).GetAwaiter().GetResult())
            {
                Console.Error.WriteLine("They don't fit.");
                return 1;
            }
            PhoneSync.SendAsync(http, device, missing, shown).GetAwaiter().GetResult();
            Console.WriteLine();
        }
        if (query is null)
            return 0;
        using var loaded = libraries();
        var router = new CatalogAssetRouter(loaded.Providers);
        clock.Restart();
        var (rows, blobs) = SongExport.BuildAsync(loaded.Snapshot, router, query, static warning => Console.Error.WriteLine(warning),
            (done, count) => { if (done % 200 == 0 || done == count) Console.Write($"\rReading songs: {done:N0} of {count:N0}   "); }).GetAwaiter().GetResult();
        Console.WriteLine($"\n{rows.Count:N0} songs, {blobs.Count:N0} files, {gb(blobs.Values.Sum(static blob => blob.Size))} ({clock.Elapsed.TotalSeconds:F0} s).");
        foreach (var source in rows.GroupBy(static row => row.Source))
            Console.WriteLine($"  {source.Key,-10} {source.Count(),6:N0} songs");
        (sent, total) = (0, 0);
        clock.Restart();
        var taken = PhoneSync.SendSongsAsync(http, device, rows, blobs, shown, bytes =>
        {
            total = bytes;
            Console.WriteLine($"The device lacks {gb(bytes)}.");
        }).GetAwaiter().GetResult();
        Console.WriteLine($"\nThe device took {taken:N0} songs in {clock.Elapsed.TotalSeconds:F0} s.");
        return 0;
    }

    private static string gb(long bytes) => $"{bytes / (double)(1L << 30):F2} GB";
}
