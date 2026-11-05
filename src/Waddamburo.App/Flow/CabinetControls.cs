using Waddamburo.App.Audio;
using Waddamburo.App.Home;
using Waddamburo.App.Scenes;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

/// <summary>
/// The cabinet's own controls (arcade mode): the service menu behind Escape or the test switch, with the game reset
/// while it is open, its settings applied as it closes and its game-mode switch; and the coin and service switches,
/// with their credit sounds played one after another.
/// </summary>
/// <param name="shell">The game.</param>
/// <param name="input">The latched drum, button and coin presses (the menu takes them while open).</param>
/// <param name="resetGame">Stops everything the game is doing (the shell's reset), as the menu opens.</param>
/// <param name="applyHome">Applies settings to the menus' own state (the home controls) as the menu closes.</param>
internal sealed class CabinetControls(GameShell shell, InputLatches input, Action resetGame, Action<ArcadeSettings> applyHome)
{
    // Coin and service credit sounds still to play, in order (SoundBank.CoinCue / ServiceCue).
    private readonly Queue<int> _queuedCoinSounds = new();
    private bool _escapeHeld;

    /// <summary>The service menu (set once the shell has made it; null without one).</summary>
    public ServiceMenu? Menu { get; set; }

    // Opening it (Escape, the test switch) is a global reset: the game stops what it was doing, its state is
    // cleared (the active flow's own clean-up, then a fresh attract that waits under the menu), the credits
    // are dropped; leaving it applies its settings and the attract starts.
    /// <summary>The service menu's tick: true while it is open and takes the tick (arcade only).</summary>
    public bool TickServiceMenu(SdlKeyboardSnapshot keys)
    {
        if (Menu is not { } service || shell.Arcade.Home)
            return false;
        var escape = keys.IsDown(SdlKeyboardKey.Escape);
        var opening = escape && !_escapeHeld && !service.IsOpen;
        _escapeHeld = escape;
        if (opening)
        {
            var credits = shell.Coins?.Credits ?? 0;
            resetGame();
            _queuedCoinSounds.Clear();
            service.Health = shell.Sync.Health;
            shell.Book.Save();
            service.Open(shell.Arcade, credits,
                don => shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false),
                leave,
                settings =>
                {
                    shell.Arcade = settings;
                    shell.ApplyControls(); // the I/O test and the drum controls page use them at once
                },
                shell.RehashLibrary, shell.Options.TjaRoot);
        }
        if (!service.IsOpen)
            return false;
        input.Take(keys); // the latched drum hits, Space and F1 go nowhere
        input.TakeCoins(keys);
        service.Tick(input.Pulses(keys), keys);
        return true;
    }

    /// <summary>OTHERS' GAME MODE changed: everything the menu set is saved with the new mode, and the game starts again in it.</summary>
    public void SwitchMode(string path, ArcadeSettings settings)
    {
        try
        {
            ArcadeSettings.SaveMenuSettings(path, settings, cabinet: !shell.Arcade.Home, mode: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
            return;
        }
        shell.Book.Save();
        Console.WriteLine($"Game mode changed to {(settings.Home ? "home" : "arcade")}: restarting.");
        shell.RequestRestart();
    }

    // The service menu's EXIT: its settings are saved and take effect, and the attract loop starts from the logo.
    // Those read only at start (languages, song folders, the audio device, the cache) restart the game.
    private void leave(ArcadeSettings settings)
    {
        var before = shell.Arcade;
        if (shell.Options.ArcadePath is { } path)
            try
            {
                ArcadeSettings.SaveMenuSettings(path, settings, cabinet: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
            }
        shell.Book.Save();
        if (settings.Language != before.Language || settings.EnglishTitles != before.EnglishTitles
            || settings.TjaFolder != before.TjaFolder || settings.OsuFolder != before.OsuFolder
            || settings.NijiiroFolder != before.NijiiroFolder || settings.StockFolders != before.StockFolders
            || settings.CacheFolder != before.CacheFolder || settings.AudioBufferFrames != before.AudioBufferFrames
            || settings.AudioExclusive != before.AudioExclusive)
        {
            Console.WriteLine("Service menu left: a setting read at start changed, restarting.");
            shell.RequestRestart();
            return;
        }
        applyHome(settings);
        shell.Coins = settings.FreePlay ? null : new CoinBank(settings);
        shell.Hosts.Coins = shell.Coins;
        if (shell.Indicators is { } indicators)
        {
            indicators.Coins = shell.Coins;
            indicators.CoinsChanged();
        }
        shell.Scenes.Show(FlowScenes.Logo);
        Console.WriteLine("Service menu left: back to the attract.");
    }

    /// <summary>The coin and service switches pressed this tick credit the cabinet; their sounds queue up.</summary>
    public void TakeCoins((int Coins, int Services) switches)
    {
        var coins = shell.Coins;
        if (coins is not null && switches.Services > 0)
        {
            // Traced: each press is one service credit with its own sound (SE_COM 10); the first replaces the coins
            // inserted, and the counter shows the service text until they're used up. Not a coin for the bookkeeping.
            for (var press = 0; press < switches.Services; press++)
            {
                coins.ServiceCredit();
                _queuedCoinSounds.Enqueue(SoundBank.ServiceCue);
            }
            Console.WriteLine($"Service credit at tick {shell.Tick}: {coins.Credits} credit(s).");
            creditsChanged();
        }
        if (coins is not null && switches.Coins > 0)
        {
            for (var coin = 0; coin < switches.Coins; coin++)
                coins.InsertCoin();
            for (var coin = 0; coin < switches.Coins; coin++)
                _queuedCoinSounds.Enqueue(SoundBank.CoinCue);
            shell.Book.CoinCount += switches.Coins;
            Console.WriteLine($"Coin credited at tick {shell.Tick}: {coins.Credits} credit(s).");
            creditsChanged();
        }
        if (_queuedCoinSounds.Count > 0 && shell.Sounds?.Bank.IsCoinPlaying != true)
            shell.Sounds?.Bank.PlayCoin(_queuedCoinSounds.Dequeue());
    }

    private void creditsChanged()
    {
        shell.Indicators?.CoinsChanged();
        if (shell.Active.Id == FlowScenes.Entry)
            shell.Hosts.EntryCoinsChanged();
    }
}
