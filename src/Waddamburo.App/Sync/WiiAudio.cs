using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Sync;

/// <summary>
/// A Wii library's song banks on the way to another device: one in G.719 (whose decoder Waddamburo cannot ship) goes as
/// Opus, made here by vgmstream-cli, as Nijiiro's banks do; the receiver's Wii catalog reads SONG_&lt;ID&gt;.ogg instead.
/// </summary>
internal static class WiiAudio
{
    public static bool IsBank(string path) => path.StartsWith("files/sound/SONG_", StringComparison.Ordinal)
        && path.EndsWith(".nub", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<(string Path, long Size, long Time, string Full)> Prepare((string Path, long Size, long Time, string Full) bank)
    {
        if (NativeAudioDecoder.BundledCanOpen(bank.Full))
            return [bank];
        var opus = VgmstreamCli.TryPrepareOpus(bank.Full)
            ?? throw new IOException(Strings.T("sync.nijiiro.audio_failed", Path.GetFileName(bank.Path)));
        return [(Path.ChangeExtension(bank.Path, ".ogg"), new FileInfo(opus).Length, bank.Time, opus)];
    }
}
