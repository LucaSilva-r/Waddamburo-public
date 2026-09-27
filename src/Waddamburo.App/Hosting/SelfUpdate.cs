using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Waddamburo.App.Presentation;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Hosting;

/// <summary>
/// Keeps a release build on the latest GitHub release: at boot it checks, and when a newer
/// release exists it downloads it on an update screen and restarts into it, so nobody stays on
/// an old client. Only release builds (a <c>vX.Y.Z</c> tag stamped by eng/package.sh, run as the
/// single-file exe or the AppImage) update; development builds (version 0.0.0) never do.
/// </summary>
/// <remarks>
/// A running single-file app reopens its own file by path whenever it loads another bundled
/// assembly, so no process may rename or replace the file it runs from. Hence: the download
/// becomes a verified <c>&lt;file&gt;.update</c>; this process hands over to it
/// (<c>--apply-update=&lt;file&gt;</c>) and exits; the update, running from its own file, moves
/// the old one to <c>.old</c>, copies itself into place and launches that (<c>--updated</c>),
/// which removes the leftovers and runs normally.
/// </remarks>
internal static class SelfUpdate
{
    private const string ApplyOption = "--apply-update=";
    private const string UpdatedOption = "--updated";

    // ponytail: test hook; point it at a local server serving a releases/latest-shaped JSON.
    private static readonly string LatestReleaseUrl = Environment.GetEnvironmentVariable("WADDAMBURO_UPDATE_URL")
        ?? "https://api.github.com/repos/LucaSilva-r/Waddamburo-public/releases/latest";

    /// <summary>
    /// Runs first in Main: finishes or hands over to a pending update. Returns true when this
    /// process must exit now; otherwise <paramref name="args"/> loses the update options.
    /// </summary>
    public static bool HandOff(ref string[] args)
    {
        var apply = args.FirstOrDefault(static argument => argument.StartsWith(ApplyOption, StringComparison.Ordinal));
        var updated = args.Contains(UpdatedOption, StringComparer.Ordinal);
        args = args.Where(static argument => argument != UpdatedOption && !argument.StartsWith(ApplyOption, StringComparison.Ordinal)).ToArray();
        if (apply is not null)
        {
            // Running from <file>.update: the old instance is exiting; take its place.
            var target = apply[ApplyOption.Length..];
            retry(() => File.Move(target, target + ".old", overwrite: true));
            File.Copy(Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath!, target, overwrite: true);
            launch(target, [.. args, UpdatedOption]);
            return true;
        }

        var installed = installedFile();
        if (installed is null || currentVersion() is null)
            return false;
        if (updated)
        {
            retry(() => File.Delete(installed + ".update"));
            Console.WriteLine($"Updated to {currentVersion()}.");
        }
        try
        {
            File.Delete(installed + ".old");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Still held by an instance that has not exited; next start.
        }
        if (!File.Exists(installed + ".update"))
            return false;
        launch(installed + ".update", [.. args, ApplyOption + installed]);
        return true;
    }

    /// <summary>
    /// Checks for a newer release and, when there is one, downloads it on an update screen and
    /// hands over to it. Returns true when this process must exit (the update is starting, or
    /// the window was closed); false boots normally (up to date, offline, or the download failed).
    /// </summary>
    public static bool UpdateNow(string[] args, string fontPath, int width, int height, bool fullscreen)
    {
        if (installedFile() is not { } installed || currentVersion() is not { } current)
            return false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Waddamburo/{current}");
        Release? release;
        try
        {
            release = findNewer(http, current);
        }
        catch (Exception exception) when (isNetworkFailure(exception))
        {
            Console.Error.WriteLine($"Update check failed: {exception.Message}");
            return false;
        }
        if (release is null)
            return false;

        Console.WriteLine($"Updating to {release.Tag}.");
        var percent = 0;
        Exception? failure = null;
        using var cancel = new CancellationTokenSource();
        using var application = new SdlApplication(WindowTitle, width, height, fullscreen: fullscreen);
        using var pill = new PairingPill(application, fontPath, top: (720f - PairingPill.Height) / 2);
        var download = Task.Run(async () =>
        {
            try
            {
                await downloadAsync(http, release, installed, value => Volatile.Write(ref percent, value), cancel.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (isNetworkFailure(exception) || exception is InvalidDataException)
            {
                failure = exception;
            }
            finally
            {
                application.RequestQuit();
            }
        });
        application.Run(_ =>
        {
            pill.Show($"Updating to {release.Tag}", $"{Volatile.Read(ref percent)}%");
            return new RenderFrame(RenderColor.Black, pill.Quads(), 1280d / 720d);
        }, static () => { });
        if (!download.IsCompleted)
        {
            // The window was closed: quit.
            cancel.Cancel();
            download.Wait();
            return true;
        }
        if (failure is not null)
        {
            Console.Error.WriteLine($"Update {release.Tag} failed: {failure.Message}");
            return false;
        }
        launch(installed + ".update", [.. args, ApplyOption + installed]);
        return true;
    }

    private sealed record Release(string Tag, Uri Download, long Size, string? Digest);

    private static Release? findNewer(HttpClient http, Version current)
    {
        using var json = JsonDocument.Parse(http.GetStringAsync(new Uri(LatestReleaseUrl)).GetAwaiter().GetResult());
        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var latest) || latest <= current)
            return null;
        var assetName = OperatingSystem.IsWindows() ? "Waddamburo.exe" : "Waddamburo-x86_64.AppImage";
        var asset = json.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(candidate => candidate.GetProperty("name").GetString() == assetName);
        if (asset.ValueKind != JsonValueKind.Object)
            return null;
        return new Release(tag, new Uri(asset.GetProperty("browser_download_url").GetString()!),
            asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
    }

    private static async Task downloadAsync(HttpClient http, Release release, string installed, Action<int> progress, CancellationToken cancel)
    {
        var partial = installed + ".part";
        await using (var output = File.Create(partial))
        {
            await using var input = await http.GetStreamAsync(release.Download, cancel).ConfigureAwait(false);
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                total += read;
                if (release.Size > 0)
                    progress((int)Math.Min(100, total * 100 / release.Size));
            }
        }
        if (release.Digest is { } expected
            && !expected.Equals("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(partial))), StringComparison.Ordinal))
        {
            File.Delete(partial);
            throw new InvalidDataException("the download does not match its checksum");
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(partial, File.GetUnixFileMode(installed));
        File.Move(partial, installed + ".update", overwrite: true);
    }

    private static bool isNetworkFailure(Exception exception) => exception is HttpRequestException or IOException
        or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException
        or TaskCanceledException;

    private static void launch(string file, string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    /// <summary>Retries a file operation while the other instance still holds the file (up to 10 s).</summary>
    private static void retry(Action operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception exception) when (attempt < 50 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>The file a release runs from: the AppImage, or the single-file exe on Windows.</summary>
    private static string? installedFile() =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage
        : OperatingSystem.IsWindows() ? Environment.ProcessPath
        : null;

    /// <summary>The window title: the release version, or "dev" and the commit for other builds.</summary>
    public static string WindowTitle
    {
        get
        {
            var informational = typeof(SelfUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var commit = informational.Contains('+', StringComparison.Ordinal) ? informational.Split('+')[1] : "";
            return currentVersion() is { } version
                ? $"Waddamburo v{version}"
                : $"Waddamburo dev{(commit.Length >= 7 ? $" ({commit[..7]})" : "")}";
        }
    }

    private static Version? currentVersion()
    {
        var informational = typeof(SelfUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Version.TryParse(informational?.Split('+')[0], out var version) && version > new Version(0, 0, 0)
            ? version
            : null;
    }
}
