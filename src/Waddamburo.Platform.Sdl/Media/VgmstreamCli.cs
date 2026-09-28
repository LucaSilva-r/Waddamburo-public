using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// Decodes files the bundled decoder rejects (G.719 in Nijiiro banks, which Waddamburo
/// cannot redistribute) with a user-supplied vgmstream-cli, into a cached looping WAV.
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
        // ponytail: plain WAV (~10 MB a minute), never pruned; compress or cap it if the folder grows.
        var cache = Path.Combine(OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "Waddamburo", "vgmstream");
        var wav = Path.Combine(cache, key + ".wav");
        if (File.Exists(wav))
            return wav;

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
