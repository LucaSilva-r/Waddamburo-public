using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// Decodes files the bundled decoder rejects (G.719 in Nijiiro banks, which Waddamburo
/// cannot redistribute) with a user-supplied vgmstream-cli. The WAV plays at once and is
/// re-encoded in the background as Opus (~1.2 MB a minute instead of ~10), which later plays use.
/// The user drops vgmstream-cli (or its release folder, named <c>vgmstream</c>) next to
/// the executable, or puts it on PATH.
/// </summary>
public static class VgmstreamCli
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "vgmstream-cli.exe" : "vgmstream-cli";
    private static bool _reportedMissing;

    /// <summary>The decoded WAV for <paramref name="path"/>, or null when vgmstream-cli is missing or fails.</summary>
    public static string? TryDecode(string path, uint sourceStreamIndex)
    {
        var source = new FileInfo(path);
        if (!source.Exists)
            return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{sourceStreamIndex}")))[..32];
        // Decoded once per bank: kept across restarts (the temp folder may be wiped on boot).
        // ponytail: never pruned (Opus keeps it small); cap it if the folder still grows too much.
        // Loop points are dropped with the WAV: the banks decoded this way are songs, played once.
        var cache = Path.Combine(OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "Waddamburo", "vgmstream");
        var wav = Path.Combine(cache, key + ".wav");
        var opus = Path.Combine(cache, key + ".ogg");
        if (File.Exists(opus))
        {
            tryDelete(wav); // left over when it was still open at the end of its re-encode
            return opus;
        }
        if (File.Exists(wav))
        {
            compressLater(wav, opus);
            return wav;
        }

        Directory.CreateDirectory(cache);
        // Unique per decode: the preview and the song start may decode the same bank at once.
        var partial = $"{wav}.{Guid.NewGuid():N}.part";
        var start = new ProcessStartInfo(findExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
            compressLater(wav, opus);
            return wav;
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Compressing = new();

    // The WAV re-encoded as Opus off the audio path; the WAV goes once the Opus file is in place.
    private static void compressLater(string wav, string opus)
    {
        if (!Compressing.TryAdd(opus, 0))
            return;
        _ = Task.Run(() =>
        {
            var partial = $"{opus}.{Guid.NewGuid():N}.part";
            try
            {
                var error = new MediaError { StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<MediaError>() };
                var result = NativeMediaMethods.TranscodeOpus(wav, partial, 160_000, ref error);
                if (result != MediaResult.Ok)
                {
                    Console.Error.WriteLine($"Could not compress the decoded audio {wav}: {result} {error.Text}.");
                    tryDelete(partial);
                    return;
                }
                File.Move(partial, opus, overwrite: true);
                tryDelete(wav);
            }
            catch (Exception exception)
            {
                // A background task: anything failing is reported here or nowhere (the WAV stays usable).
                Console.Error.WriteLine($"Could not compress the decoded audio {wav}: {exception.Message}");
                tryDelete(partial);
            }
            finally
            {
                Compressing.TryRemove(opus, out _);
            }
        });
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

    /// <summary>True when vgmstream-cli is next to the executable or on PATH.</summary>
    public static bool IsInstalled => _installed ??= findExecutable() != ExecutableName
        || (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(static directory => directory.Length > 0 && File.Exists(Path.Combine(directory, ExecutableName)));
    private static bool? _installed;

    private static string findExecutable()
    {
        var executable = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;
        var directory = Path.GetDirectoryName(executable);
        if (directory is not null)
        {
            foreach (var candidate in new[] { Path.Combine(directory, ExecutableName), Path.Combine(directory, "vgmstream", ExecutableName) })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return ExecutableName;
    }
}
