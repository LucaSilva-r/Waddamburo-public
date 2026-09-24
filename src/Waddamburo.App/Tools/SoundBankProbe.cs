using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Tools;

internal static class SoundBankProbe
{
    private const int MaximumCueCount = 256;

    public static void Run(string path, int windowWidth, int windowHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var bankPaths = resolveBankPaths(fullPath);
        using var application = new SdlApplication(
            "Waddamburo — Sound Bank Probe",
            windowWidth,
            windowHeight,
            resizable: true);
        using var device = new SdlAudioDevice();
        using var engine = new AudioEngine(device);
        Console.WriteLine($"Sound bank probe: {bankPaths.Length} bank(s).");
        Console.WriteLine(
            "Up/Down: change bank; Right/K: next cue; Left/D: previous cue; " +
            "Space/F/Enter: replay; close the window to stop.");

        var frame = new RenderFrame(RenderColor.WaddamburoBlue, []);
        var previousKeys = SdlKeyboardSnapshot.Empty;
        var selectedBank = 0;
        var selected = 0;
        var clips = loadCues(bankPaths[selectedBank], engine.Mixer.Format);
        AudioPlaybackHandle? playing = null;

        void playSelected()
        {
            if (playing is { } handle)
                engine.Mixer.Stop(handle, TimeSpan.FromMilliseconds(20));
            playing = engine.Mixer.Play(clips[selected], AudioBus.MenuSound);
            Console.WriteLine(
                $"Playing {Path.GetFileName(bankPaths[selectedBank])} cue {selected} " +
                $"(decoder stream {selected + 1}, " +
                $"{clips[selected].Duration.TotalSeconds:0.000}s).");
        }

        void changeBank(int offset)
        {
            if (playing is { } handle)
                engine.Mixer.Stop(handle, TimeSpan.FromMilliseconds(20));
            selectedBank = (selectedBank + bankPaths.Length + offset) % bankPaths.Length;
            selected = 0;
            clips = loadCues(bankPaths[selectedBank], engine.Mixer.Format);
            Console.WriteLine(
                $"Selected bank {selectedBank + 1}/{bankPaths.Length}: " +
                $"{Path.GetFileName(bankPaths[selectedBank])}, {clips.Count} cue(s).");
            playSelected();
        }

        Console.WriteLine(
            $"Selected bank 1/{bankPaths.Length}: {Path.GetFileName(bankPaths[0])}, " +
            $"{clips.Count} cue(s).");
        playSelected();
        application.Run(
            _ => frame,
            keyboard =>
            {
                bool rising(SdlKeyboardKey key) => keyboard.IsDown(key) && !previousKeys.IsDown(key);
                if (rising(SdlKeyboardKey.Down))
                {
                    changeBank(1);
                }
                else if (rising(SdlKeyboardKey.Up))
                {
                    changeBank(-1);
                }
                else if (rising(SdlKeyboardKey.Right) || rising(SdlKeyboardKey.K))
                {
                    selected = (selected + 1) % clips.Count;
                    playSelected();
                }
                else if (rising(SdlKeyboardKey.Left) || rising(SdlKeyboardKey.D))
                {
                    selected = (selected + clips.Count - 1) % clips.Count;
                    playSelected();
                }
                else if (rising(SdlKeyboardKey.Space)
                    || rising(SdlKeyboardKey.F)
                    || rising(SdlKeyboardKey.Enter))
                {
                    playSelected();
                }
                previousKeys = keyboard;
            });
    }

    private static string[] resolveBankPaths(string path)
    {
        if (File.Exists(path))
            return [path];
        if (!Directory.Exists(path))
            throw new FileNotFoundException("The sound bank or bank directory does not exist.", path);

        var paths = Directory.GetFiles(path, "*.nub", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        if (paths.Length == 0)
            throw new InvalidDataException("The sound-bank directory contains no .nub files.");
        return paths;
    }

    private static List<AudioClip> loadCues(string path, SdlAudioFormat format)
    {
        var clips = new List<AudioClip>();
        for (var cue = 0; cue < MaximumCueCount; cue++)
        {
            try
            {
                clips.Add(AudioClip.Load(path, format, sourceStreamIndex: checked((uint)cue + 1U)));
            }
            catch (Exception exception) when (
                clips.Count != 0
                && exception is IOException or InvalidDataException or NotSupportedException)
            {
                break;
            }
        }
        if (clips.Count == 0)
            throw new InvalidDataException("The sound bank contains no decodable cues.");
        return clips;
    }
}
