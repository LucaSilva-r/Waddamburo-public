using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// Decodes files the bundled decoder rejects (G.719 in Nijiiro banks, which Waddamburo
/// cannot redistribute) with a user-supplied vgmstream-cli. The WAV plays at once and is
/// re-encoded in the background as Opus (~1.2 MB a minute instead of ~10), which later plays use.
/// The user drops vgmstream-cli (or its release folder, named <c>vgmstream</c>) next to
/// the executable or in the game folder (USRDIR), or puts it on PATH.
/// </summary>
public static class VgmstreamCli
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "vgmstream-cli.exe" : "vgmstream-cli";

    // Before the game folder held it (USRDIR/waddamburo/cache/vgmstream): the user cache folder.
    private static readonly string UserCache = Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "Waddamburo", "vgmstream");

    private static string? _cacheDirectory;

    /// <summary>The game folder (USRDIR), also searched for vgmstream-cli: a release sits in it, a development
    /// build does not.</summary>
    public static string? GameFolder { get; set; }

    /// <summary>
    /// Where decoded audio is kept (set to USRDIR/waddamburo/cache/vgmstream by a normal boot; the
    /// user cache folder otherwise). A cache in the old user-cache location moves here once.
    /// </summary>
    public static string CacheDirectory
    {
        get => _cacheDirectory ?? UserCache;
        set
        {
            _cacheDirectory = value;
            if (value != UserCache && Directory.Exists(UserCache) && !Directory.Exists(value))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(value)!);
                try
                {
                    Directory.Move(UserCache, value);
                }
                catch (IOException)
                {
                    // Another drive: copy the files over instead.
                    Directory.CreateDirectory(value);
                    foreach (var file in Directory.GetFiles(UserCache))
                        File.Move(file, Path.Combine(value, Path.GetFileName(file)));
                }
            }
        }
    }
    private static bool _reportedMissing;

    /// <summary>The decoded WAV for <paramref name="path"/>, or null when vgmstream-cli is missing or fails.</summary>
    public static string? TryDecode(string path, uint sourceStreamIndex) => decode(path, sourceStreamIndex, requireOpus: false);

    /// <summary>Returns a persistent, cached Opus file suitable for transfer, or null on conversion failure.</summary>
    public static string? TryPrepareOpus(string path) => decode(path, 0, requireOpus: true);

    private static string? decode(string path, uint sourceStreamIndex, bool requireOpus)
    {
        var source = new FileInfo(path);
        if (!source.Exists)
            return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{sourceStreamIndex}")))[..32];
        // Decoded once per bank: kept across restarts (the temp folder may be wiped on boot).
        // ponytail: never pruned (Opus keeps it small); cap it if the folder still grows too much.
        // Loop points are dropped with the WAV: the banks decoded this way are songs, played once.
        var cache = CacheDirectory;
        var wav = Path.Combine(cache, key + ".wav");
        var opus = Path.Combine(cache, key + ".ogg");
        if (File.Exists(opus))
        {
            tryDelete(wav); // left over when it was still open at the end of its re-encode
            return opus;
        }
        if (File.Exists(wav))
        {
            return output(wav, opus, requireOpus);
        }

        Directory.CreateDirectory(cache);
        // Unique per decode: the preview and the song start may decode the same bank at once.
        var partial = $"{wav}.{Guid.NewGuid():N}.part";
        var start = new ProcessStartInfo(findExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // A console program: without this, Windows flashes a console window for every decode.
            CreateNoWindow = true,
        };
        // -L writes the loop points as a RIFF smpl chunk, which the native decoder reads back.
        foreach (var argument in new[] { "-L", "-o", partial })
            start.ArgumentList.Add(argument);
        if (sourceStreamIndex != 0)
        {
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(sourceStreamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        start.ArgumentList.Add(source.FullName);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(partial))
            {
                Console.Error.WriteLine($"vgmstream-cli could not decode {path}: {errors.Trim()}");
                File.Delete(partial);
                return null;
            }
            try
            {
                File.Move(partial, wav, overwrite: true);
            }
            catch (IOException) when (File.Exists(wav))
            {
                File.Delete(partial); // a parallel decode finished first (and its file may be open)
            }
            return output(wav, opus, requireOpus);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!_reportedMissing)
            {
                _reportedMissing = true;
                Console.Error.WriteLine(
                    "Some audio needs vgmstream-cli: download it from https://github.com/vgmstream/vgmstream/releases "
                    + "and put it (or its folder, named 'vgmstream') next to Waddamburo.");
            }
            return null;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<bool>>> Compressing = new();

    private static string? output(string wav, string opus, bool requireOpus)
    {
        if (File.Exists(opus))
            return opus;
        var operation = Compressing.GetOrAdd(opus, _ => new Lazy<Task<bool>>(() => Task.Run(() => compress(wav, opus))));
        var task = operation.Value;
        _ = task.ContinueWith(_ => Compressing.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(opus, operation)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return requireOpus ? task.GetAwaiter().GetResult() ? opus : null : File.Exists(opus) ? opus : wav;
    }

    // The WAV is kept if compression fails and can still be used for desktop playback.
    private static bool compress(string wav, string opus)
    {
        if (File.Exists(opus))
            return true;
        var partial = $"{opus}.{Guid.NewGuid():N}.part";
        try
        {
            var error = new MediaError { StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<MediaError>() };
            var result = NativeMediaMethods.TranscodeOpus(wav, partial, 160_000, ref error);
            if (result != MediaResult.Ok)
            {
                Console.Error.WriteLine($"Could not compress the decoded audio {wav}: {result} {error.Text}.");
                return false;
            }
            File.Move(partial, opus, overwrite: true);
            tryDelete(wav);
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not compress the decoded audio {wav}: {exception.Message}");
            return false;
        }
        finally
        {
            tryDelete(partial);
        }
    }

    // Windows refuses while the file is open (still playing): the next lookup retries.
    private static void tryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>True when vgmstream-cli is next to the executable, in the game folder or on PATH.</summary>
    public static bool IsInstalled => _installed ??= findExecutable() != ExecutableName
        || (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(static directory => directory.Length > 0 && File.Exists(Path.Combine(directory, ExecutableName)));
    private static bool? _installed;

    /// <summary>Looks for vgmstream-cli again at the next <see cref="IsInstalled"/>.</summary>
    public static void Recheck() => _installed = null;

    private static string findExecutable()
    {
        var executable = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;
        foreach (var directory in new[] { Path.GetDirectoryName(executable), GameFolder })
        {
            if (directory is null)
                continue;
            foreach (var candidate in new[] { Path.Combine(directory, ExecutableName), Path.Combine(directory, "vgmstream", ExecutableName) })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return ExecutableName;
    }
}
