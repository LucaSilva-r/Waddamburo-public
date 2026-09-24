using Waddamburo.Formats.Audio;
using Waddamburo.Game.Lumen;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl.Media;

/// <summary>Resolves authored nuSound2 bank/cue requests against a user-supplied sound tree.</summary>
internal sealed class AuthoredSoundController : ISongSelectSoundController, IRetrySoundController,
    IResultSoundController, IGameOverSoundController, IAttractSoundController
{
    private readonly AudioEngine _audio;
    private readonly string _bankRoot;
    private readonly string _musicRoot;
    private readonly NuSoundBankCatalog _catalog;
    private readonly Dictionary<(string Bank, int Cue), AudioClip> _clips = [];
    private readonly HashSet<(string Bank, int Cue)> _reportedFailures = [];
    private AudioClip? _latestVoice;
    private int _latestVoiceCue = -1;
    private AudioPlaybackHandle? _oneShotVoiceHandle;
    private AudioPlaybackHandle? _loopVoiceHandle;
    private AudioPlaybackHandle? _retryMusicHandle;
    private AudioPlaybackHandle? _resultMusicHandle;
    private AudioPlaybackHandle? _gameOverMusicHandle;
    private string? _gameOverMusicName;
    private AudioPlaybackHandle? _attractStreamHandle;
    private bool _categoryVoiceSelected;

    public AuthoredSoundController(AudioEngine audio, string soundRoot)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        ArgumentException.ThrowIfNullOrWhiteSpace(soundRoot);
        var root = Path.GetFullPath(soundRoot);
        _bankRoot = Path.Combine(root, "se");
        _musicRoot = Path.Combine(root, "bgm", "nub");
        _catalog = NuSoundBankCatalog.Load(Path.Combine(root, "config", "nuSound2BankStr.bin"));
    }

    public void RequestSound(LumenFrontendSoundRequest request)
    {
        switch (request.Kind)
        {
            case LumenFrontendSoundRequestKind.Effect:
                playBankCue(request.Arguments);
                break;
            case LumenFrontendSoundRequestKind.SystemEffect:
                playEntrySystemEffect(request.Arguments);
                break;
            case LumenFrontendSoundRequestKind.LoopVoice:
                replayLatestVoiceOnce();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public void RequestSound(SongSelectSoundRequest request)
    {
        switch (request.Kind)
        {
            case SongSelectSoundRequestKind.Effect:
                playBankCue(request.Arguments);
                break;
            case SongSelectSoundRequestKind.LoopVoice:
                replayLatestVoiceOnce();
                break;
            case SongSelectSoundRequestKind.SystemEffect:
                traceUnmapped(request.Kind.ToString(), request.Arguments);
                break;
            case SongSelectSoundRequestKind.PlayerEffect:
                playPlayerEffect(request.Arguments);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public void RequestSound(RetrySoundRequest request)
    {
        if (request.Kind == RetrySoundRequestKind.Voice
            && request.Group == 0
            && request.Cue is 1 or 2 or 3 or 4 or 6 or 7 or 8)
        {
            playNamedBankCue("VO_REVIVAL", request.Cue);
        }
        else if (request.Kind == RetrySoundRequestKind.Effect)
        {
            if (request.Group == 0 && request.Cue is 0 or 1 or 3 or 5 or 7 or 8 or 12 or 14 or 16)
                playNamedBankCue("SE_REVIVAL", request.Cue);
            else if (request.Group == 1 && request.Cue == 100)
                playNamedBankCue("SE_COM", 0, AudioBus.DrumHit);
            else if (request.Group == 0 && request.Cue == 101)
                playNamedBankCue("SE_COM", 13, AudioBus.MenuSound);
            else
                traceUnmappedRetry(request);
        }
        else
        {
            traceUnmappedRetry(request);
        }
    }

    public void RequestSound(ResultSoundRequest request)
    {
        // Group 1/2 = the left/right player's request. The right player's voices are Katsu-chan's,
        // one cue after Don-chan's (traced (2, 1/6/8) -> VO_RESULT 2/12/16); effects are shared.
        var katsu = request.Group == 2;
        var group = katsu ? 1 : request.Group;
        var sound = (request.Kind, group, request.Cue) switch
        {
            (ResultSoundRequestKind.Voice, 1, 1) => (Bank: "VO_RESULT", Cue: 1),
            (ResultSoundRequestKind.Voice, 1, 5) => (Bank: "VO_RESULT", Cue: 9),
            (ResultSoundRequestKind.Voice, 1, 6) => (Bank: "VO_RESULT", Cue: 11),
            (ResultSoundRequestKind.Voice, 1, 7) => (Bank: "VO_RESULT", Cue: 13),
            (ResultSoundRequestKind.Voice, 1, 8) => (Bank: "VO_RESULT", Cue: 15),
            (ResultSoundRequestKind.Voice, 0, 9) => (Bank: "VO_RESULT", Cue: 17),
            (ResultSoundRequestKind.Voice, 0, 10) => (Bank: "VO_RESULT", Cue: 18),
            (ResultSoundRequestKind.Effect, 1, 0) => (Bank: "SE_RESULT", Cue: 0),
            (ResultSoundRequestKind.Effect, 1, 1) => (Bank: "SE_RESULT", Cue: 3),
            (ResultSoundRequestKind.Effect, 1, 2) => (Bank: "SE_RESULT", Cue: 6),
            (ResultSoundRequestKind.Effect, 1, 5) => (Bank: "SE_RESULT", Cue: 15),
            (ResultSoundRequestKind.Effect, 0, 6) => (Bank: "SE_RESULT",
                Cue: request.FullCombo ? 22 : request.Cleared ? 18 : 20),
            (ResultSoundRequestKind.Effect, 0, 7) => (Bank: "SE_RESULT", Cue: 24),
            _ => default,
        };
        if (katsu && request.Kind == ResultSoundRequestKind.Voice && sound.Bank is not null)
            sound.Cue++;
        if (sound.Bank is not null)
        {
            playNamedBankCue(sound.Bank, sound.Cue,
                request.Kind == ResultSoundRequestKind.Voice ? AudioBus.Voice : null,
                trace: request.Cue != 1 || request.Kind != ResultSoundRequestKind.Effect);
        }
        else
            Console.WriteLine($"Result.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
    }

    public void RequestSound(GameOverSoundRequest request)
    {
        switch (request.Kind, request.Group, request.Cue)
        {
            case (GameOverSoundRequestKind.Effect, 0, 100):
                playNamedBankCue("SE_GAMEOVER", 0);
                break;
            case (GameOverSoundRequestKind.Voice, 0, 100):
                playNamedBankCue("VO_HOWTOPLAY", 0);
                break;
            default:
                Console.WriteLine($"GameOver.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");
                break;
        }
    }

    public void StopAll()
    {
        foreach (var bus in Enum.GetValues<AudioBus>().Where(static bus => bus != AudioBus.Coin))
            _audio.Mixer.StopBus(bus);
        _retryMusicHandle = null;
        _resultMusicHandle = null;
        _gameOverMusicHandle = null;
        _gameOverMusicName = null;
        _oneShotVoiceHandle = null;
        _loopVoiceHandle = null;
        _latestVoice = null;
        _categoryVoiceSelected = false;
    }

    public void StartMusic()
    {
        if (_retryMusicHandle is { } handle && _audio.Mixer.IsPlaying(handle))
            return;
        var path = Path.Combine(_musicRoot, "JINGLE_RENDA.nub");
        try
        {
            _retryMusicHandle = _audio.PlayLoop(path, AudioBus.Bgm);
            Console.WriteLine("Revival music JINGLE_RENDA -> Bgm.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add(("JINGLE_RENDA", -1)))
                Console.Error.WriteLine($"Revival music is unavailable: {exception.Message}");
        }
    }

    public void StartResultMusic()
    {
        if (_resultMusicHandle is { } handle && _audio.Mixer.IsPlaying(handle))
            return;
        var path = Path.Combine(_musicRoot, "JINGLE_SEISEKI.nub");
        try
        {
            _resultMusicHandle = _audio.PlayLoop(path, AudioBus.Bgm);
            Console.WriteLine("Result music JINGLE_SEISEKI -> Bgm.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add(("JINGLE_SEISEKI", -1)))
                Console.Error.WriteLine($"Result music is unavailable: {exception.Message}");
        }
    }

    public void StopResultMusic()
    {
        if (_resultMusicHandle is { } handle)
            _audio.Mixer.Stop(handle, TimeSpan.FromMilliseconds(20));
        _resultMusicHandle = null;
    }

    public void StartGameOverMusic() => startGameOverMusic("JINGLE_CARDRECOM");

    public void StartGameOverOutro() => startGameOverMusic("JINGLE_GOVER");

    public void StopGameOverMusic()
    {
        if (_gameOverMusicHandle is { } handle)
            _audio.Mixer.Stop(handle, TimeSpan.FromMilliseconds(20));
        _gameOverMusicHandle = null;
        _gameOverMusicName = null;
    }

    private void startGameOverMusic(string name)
    {
        if (_gameOverMusicName == name && _gameOverMusicHandle is { } current
            && _audio.Mixer.IsPlaying(current))
            return;
        StopGameOverMusic();
        var path = Path.Combine(_musicRoot, name + ".nub");
        try
        {
            _gameOverMusicHandle = name == "JINGLE_GOVER"
                ? _audio.PlayOneShot(path, AudioBus.Bgm)
                : _audio.PlayLoop(path, AudioBus.Bgm);
            _gameOverMusicName = name;
            Console.WriteLine($"Game Over music {name} -> Bgm.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add((name, -1)))
                Console.Error.WriteLine($"Game Over music {name} is unavailable: {exception.Message}");
        }
    }

    public void PlayAttractStream(string name)
    {
        StopAttractStream();
        try
        {
            _attractStreamHandle = _audio.PlayOneShot(Path.Combine(_musicRoot, name + ".nub"), AudioBus.Bgm);
            Console.WriteLine($"Attract music {name} -> Bgm.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            if (_reportedFailures.Add((name, -1)))
                Console.Error.WriteLine($"Attract music {name} is unavailable: {exception.Message}");
        }
    }

    public bool IsAttractStreamPlaying => isPlaying(_attractStreamHandle);

    public void StopAttractStream()
    {
        if (_attractStreamHandle is { } handle)
            _audio.Mixer.Stop(handle, TimeSpan.FromMilliseconds(20));
        _attractStreamHandle = null;
    }

    public void PlayAttractCue(bool voice, int cue) => playNamedBankCue(voice ? "VO_ATTRACT" : "SE_ATTRACT", cue);

    private AudioPlaybackHandle? _coinHandle;

    /// <summary>
    /// One queued coin's sound (traced SE_COM cue 9 per credited coin), on its own bus. The caller
    /// credits the next coin only once this has finished, so every coin is heard in full.
    /// </summary>
    public void PlayCoin()
    {
        try
        {
            var clip = loadClip(("SE_COM", 9));
            _coinHandle = _audio.Mixer.Play(clip, AudioBus.Coin);
            Console.WriteLine($"Authored sound SE_COM#9 -> Coin ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(("SE_COM", 9), exception);
        }
    }

    public bool IsCoinPlaying => isPlaying(_coinHandle);

    public void PlayEntryVoice(int cue) => playNamedBankCue("VO_ENTRY", cue);

    /// <summary>A drum hit leaving the attract loop (traced SE_COM cue 1 at the title).</summary>
    public void PlayAttractExit() => playNamedBankCue("SE_COM", 1, AudioBus.MenuSound);

    public bool IsVoicePlaying => isPlaying(_oneShotVoiceHandle) || isPlaying(_loopVoiceHandle);

    public void PrepareGameplayDrums()
    {
        foreach (var cue in new[] { 0, 1 })
        {
            var key = (Bank: "SE_GAME_NEIRO_000_C", Cue: cue);
            try { _ = loadClip(key); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
            {
                reportUnavailable(key, exception);
            }
        }
    }

    public void PlayDrum(bool don) =>
        playNamedBankCue("SE_GAME_NEIRO_000_C", don ? 0 : 1, AudioBus.DrumHit, trace: false);

    public void PlayGameplayEvent(GameplaySoundEvent sound)
    {
        switch (sound)
        {
            case GameplaySoundEvent.LongNoteStarted:
                playNamedBankCue("VO_GAME", 156);
                break;
            case GameplaySoundEvent.BalloonPopped:
                playNamedBankCue("SE_GAME", 0);
                break;
            case GameplaySoundEvent.KusudamaPopped:
                playNamedBankCue("SE_GAME", 3);
                break;
            case GameplaySoundEvent.KusudamaFailed:
                playNamedBankCue("SE_GAME", 5);
                playNamedBankCue("VO_GAME", 159);
                break;
            case GameplaySoundEvent.FiftyCombo:
                playNamedBankCue("VO_GAME", 0);
                break;
            case GameplaySoundEvent.HundredCombo:
                playNamedBankCue("VO_GAME", 3);
                break;
            case GameplaySoundEvent.FailBanner:
                playNamedBankCue("SE_GAME", 9);
                break;
            case GameplaySoundEvent.ClearBanner:
                playNamedBankCue("SE_GAME", 6);
                break;
            case GameplaySoundEvent.FullComboBanner:
                playNamedBankCue("SE_GAME", 12);
                playNamedBankCue("VO_GAME", 153);
                break;
            case GameplaySoundEvent.SongFinished:
                playNamedBankCue("VO_RESULT", 0);
                playNamedBankCue("SE_GAME", 15);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sound));
        }
    }

    public void SelectCategoryVoice(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        var cue = category switch
        {
            "Namco Original" => 0,
            "Pop" or "J-POP" => 1,
            "Game Music" => 2,
            "Classical" => 3,
            "Variety" => 4,
            "Anime" => 5,
            "Vocaloid" => 7,
            _ => -1,
        };
        if (cue < 0)
        {
            Console.WriteLine($"Category voice for \"{category}\" is unmapped.");
            StopVoice();
            return;
        }

        _categoryVoiceSelected = true;
        playNamedBankCue("VO_SELECT", cue, AudioBus.Voice);
    }

    /// <summary>Stops the current voice only when it is the given cue.</summary>
    public void StopVoice(int cue)
    {
        if (_latestVoice is not null && _latestVoiceCue == cue)
            StopVoice();
    }

    public void StopVoice()
    {
        stopVoiceImmediately(_oneShotVoiceHandle);
        stopVoiceImmediately(_loopVoiceHandle);
        _oneShotVoiceHandle = null;
        _loopVoiceHandle = null;
        _latestVoice = null;
    }

    private void playBankCue(System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        if (!tryInteger(arguments, 0, out var bankId) || !tryInteger(arguments, 1, out var cueId) || cueId < 0)
        {
            traceUnmapped("Effect", arguments);
            return;
        }
        if (!_catalog.TryGetName(bankId, out var bankName))
        {
            traceUnmapped("Effect", arguments);
            return;
        }

        // Song Select emits its stock initial-genre sound ID after host data is
        // assigned. The host-owned category selection above has already resolved
        // the custom catalog's semantic genre and must not be replaced by that ID.
        if (_categoryVoiceSelected && bankName == "VO_SELECT" && cueId == 32)
            return;

        playNamedBankCue(bankName, cueId);
    }

    private void playPlayerEffect(System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        if (!tryInteger(arguments, 0, out _)
            || !tryInteger(arguments, 1, out var hitKind))
        {
            traceUnmapped("PlayerEffect", arguments);
            return;
        }

        var sound = hitKind switch
        {
            0 => (Bank: "SE_COM", Cue: 0), // Don
            1 => (Bank: "SE_COM", Cue: 3), // Ka
            2 => (Bank: "SE_COM", Cue: 6), // Close-folder substitution
            _ => default,
        };
        if (sound.Bank is null)
        {
            traceUnmapped("PlayerEffect", arguments);
            return;
        }
        playNamedBankCue(sound.Bank, sound.Cue, AudioBus.DrumHit);
    }

    private void playEntrySystemEffect(System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        if (!tryInteger(arguments, 0, out var group)
            || !tryInteger(arguments, 1, out var hitKind)
            || group is not (0 or 1))
        {
            traceUnmapped("SystemEffect", arguments);
            return;
        }

        // Group = the drum: the right one's Don played SE_COM cue 2 (traced session8-p2-solo); its Ka
        // shares the left drum's cue.
        var cueId = hitKind switch
        {
            0 => group == 0 ? 0 : 2, // Don / confirm
            1 => 3, // Ka / navigation
            _ => -1,
        };
        if (cueId < 0)
        {
            traceUnmapped("SystemEffect", arguments);
            return;
        }
        playNamedBankCue("SE_COM", cueId, AudioBus.DrumHit);
    }

    private void playNamedBankCue(
        string bankName,
        int cueId,
        AudioBus? busOverride = null,
        bool loop = false,
        bool trace = true)
    {
        var key = (bankName, cueId);
        try
        {
            var clip = loadClip(key);
            var bus = busOverride
                ?? (bankName.StartsWith("VO_", StringComparison.Ordinal) ? AudioBus.Voice : AudioBus.MenuSound);
            if (bus == AudioBus.Voice)
            {
                stopVoiceImmediately(_oneShotVoiceHandle);
                stopVoiceImmediately(_loopVoiceHandle);
                _latestVoice = clip;
                _latestVoiceCue = cueId;
                _oneShotVoiceHandle = null;
                _loopVoiceHandle = null;
            }
            var handle = _audio.Mixer.Play(clip, bus, loop: loop);
            if (bus == AudioBus.Voice)
            {
                if (loop)
                    _loopVoiceHandle = handle;
                else
                    _oneShotVoiceHandle = handle;
            }
            if (trace)
                Console.WriteLine($"Authored sound {bankName}#{cueId} -> {bus} ({clip.Duration.TotalSeconds:0.00} s).");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            reportUnavailable(key, exception);
        }
    }

    private AudioClip loadClip((string Bank, int Cue) key)
    {
        if (_clips.TryGetValue(key, out var clip))
            return clip;
        var path = Path.Combine(_bankRoot, key.Bank + ".nub");
        clip = AudioClip.Load(path, _audio.Mixer.Format, sourceStreamIndex: checked((uint)key.Cue + 1U));
        _clips.Add(key, clip);
        return clip;
    }

    private void reportUnavailable((string Bank, int Cue) key, Exception exception)
    {
        if (_reportedFailures.Add(key))
            Console.Error.WriteLine($"Authored sound {key.Bank}#{key.Cue} is unavailable: {exception.Message}");
    }

    private void replayLatestVoiceOnce()
    {
        if (_latestVoice is null || IsVoicePlaying)
            return;
        _loopVoiceHandle = _audio.Mixer.Play(_latestVoice, AudioBus.Voice);
        Console.WriteLine("Authored voice replay -> Voice.");
    }

    private bool isPlaying(AudioPlaybackHandle? handle)
        => handle is { } value && _audio.Mixer.IsPlaying(value);

    private void stopVoiceImmediately(AudioPlaybackHandle? handle)
    {
        if (handle is { } value)
            _audio.Mixer.Stop(value);
    }

    private static bool tryInteger(
        System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments,
        int index,
        out int result)
    {
        result = 0;
        if ((uint)index >= (uint)arguments.Length || arguments[index].Kind != LumenHostValueKind.Number)
            return false;
        var value = arguments[index].AsNumber();
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
            return false;
        result = (int)value;
        return true;
    }

    private static void traceUnmapped(
        string kind,
        System.Collections.Immutable.ImmutableArray<LumenHostValue> arguments)
    {
        Console.WriteLine($"Lumen.{kind}({string.Join(", ", arguments.Select(formatValue))}) [unmapped]");
    }

    private static void traceUnmappedRetry(RetrySoundRequest request) =>
        Console.WriteLine($"Retry.{request.Kind}({request.Group}, {request.Cue}) [unmapped]");

    private static string formatValue(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Undefined => "undefined",
        LumenHostValueKind.Null => "null",
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        LumenHostValueKind.Number => value.AsNumber().ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
        LumenHostValueKind.Text => $"\"{value.AsString()}\"",
        _ => "undefined",
    };
}
