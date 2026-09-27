using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Waddamburo.App.Hosting;

/// <summary>
/// Keeps a release build on the latest GitHub release. Only release builds (a <c>vX.Y.Z</c> tag
/// stamped by eng/package.sh, run as the single-file exe or the AppImage) update; development
/// builds (version 0.0.0) never do.
/// </summary>
/// <remarks>
/// A running single-file app reopens its own file by path whenever it loads another bundled
/// assembly, so no process may rename or replace the file it runs from. Hence: the check
/// downloads a verified <c>&lt;file&gt;.update</c> in the background; the next launch hands over
/// to it (<c>--apply-update=&lt;file&gt;</c>) and exits; the update, running from its own file,
/// moves the old one to <c>.old</c>, copies itself into place and launches that
/// (<c>--updated</c>), which removes the leftovers and runs normally.
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

    /// <summary>Checks for a newer release in the background and stages it for the next launch.</summary>
    public static void Check()
    {
        if (installedFile() is { } installed && currentVersion() is { } current)
            _ = Task.Run(() => downloadAsync(installed, current));
    }

    private static async Task downloadAsync(string installed, Version current)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Waddamburo/{current}");
            using var release = JsonDocument.Parse(await http.GetStringAsync(new Uri(LatestReleaseUrl)).ConfigureAwait(false));
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out var latest) || latest <= current)
                return;
            var assetName = OperatingSystem.IsWindows() ? "Waddamburo.exe" : "Waddamburo-x86_64.AppImage";
            var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(candidate => candidate.GetProperty("name").GetString() == assetName);
            if (asset.ValueKind != JsonValueKind.Object)
                return;
            var partial = installed + ".part";
            await using (var output = File.Create(partial))
            {
                await using var input = await http.GetStreamAsync(new Uri(asset.GetProperty("browser_download_url").GetString()!))
                    .ConfigureAwait(false);
                await input.CopyToAsync(output).ConfigureAwait(false);
            }
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } expected
                && !expected.Equals("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(partial))), StringComparison.Ordinal))
            {
                File.Delete(partial);
                Console.Error.WriteLine($"Update {tag}: the download does not match its checksum; skipped.");
                return;
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(partial, File.GetUnixFileMode(installed));
            File.Move(partial, installed + ".update", overwrite: true);
            Console.WriteLine($"Update {tag} downloaded; it installs at the next launch.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Update check failed: {exception.Message}");
        }
    }

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

    private static Version? currentVersion()
    {
        var informational = typeof(SelfUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Version.TryParse(informational?.Split('+')[0], out var version) && version > new Version(0, 0, 0)
            ? version
            : null;
    }
}
