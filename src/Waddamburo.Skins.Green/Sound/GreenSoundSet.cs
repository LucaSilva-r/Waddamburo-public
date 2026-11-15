using Waddamburo.Formats.Audio;
using Waddamburo.Game.Sound;

namespace Waddamburo.Skins.Green.Sound;

/// <summary>
/// The game's sounds (data/sound): nuSound2 bank cues (se/*.nub, named by config/nuSound2BankStr.bin) with the
/// settings authored in each bank's header (gain, pan, pitch, leading silence), and music (bgm/nub). Also which cue
/// each engine sound name is (traced, see the cases).
/// </summary>
public sealed class GreenSoundSet : ISoundSet
{
    private readonly string _bankRoot;
    private readonly string _musicRoot;
    private readonly Lazy<NuSoundBankCatalog> _catalog;
    private readonly Dictionary<(string Bank, int Cue), SoundSource> _cues = [];

    public GreenSoundSet(string soundRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundRoot);
        var root = Path.GetFullPath(soundRoot);
        _bankRoot = Path.Combine(root, "se");
        _musicRoot = Path.Combine(root, "bgm", "nub");
        _catalog = new(() => NuSoundBankCatalog.Load(Path.Combine(root, "config", "nuSound2BankStr.bin")));
    }

    /// <summary>Green's drum sounds (音色 0-5; Waiwai has its own, 100).</summary>
    public IReadOnlyList<string> Tones { get; } = ["太鼓", "お祭り", "いぬねこ", "豪華な太鼓", "ドラム", "タンバリン"];

    public bool TryGetBankName(int bankId, out string name) => _catalog.Value.TryGetName(bankId, out name);

    /// <summary>A bank's cue: its stream in the bank, its settings from the bank's header.</summary>
    public SoundSource Cue(string bank, int cue)
    {
        if (_cues.TryGetValue((bank, cue), out var source))
            return source;
        var path = Path.Combine(_bankRoot, bank + ".nub");
        source = new SoundSource(path, checked(cue + 1), $"{bank}#{cue}")
        {
            // vgmstream leaves the header's authored gain to the player.
            Settings = () =>
            {
                var header = NubCueDelay.ReadBankHeader(path, cue);
                return new(NubCueDelay.ReadGain(header, cue), NubCueDelay.ReadPan(header, cue),
                    NubCueDelay.ReadPitch(header, cue), NubCueDelay.Read(header, cue));
            },
        };
        _cues.Add((bank, cue), source);
        return source;
    }

    /// <summary>VO_ banks are voices; the rest menu sounds.</summary>
    public static SoundBus BusOf(string bank) => bank.StartsWith("VO_", StringComparison.Ordinal) ? SoundBus.Voice : SoundBus.Menu;

    /// <summary>A bgm/nub track.</summary>
    public SoundSource Music(string name) => new(Path.Combine(_musicRoot, name + ".nub"), 0, name);

    public IReadOnlyList<SoundCue> Describe(SoundRequest request)
    {
        // Two players (traced session9-2p): a player's own cues follow the one-player cue as +1 (left) / +2 (right):
        // combo voice 1/2, banners 10/11 · 7/8 · 13/14, full-combo voice 154/155, balloon 2, roll voice 158,
        // kusudama miss voice 160/161.
        int own(int cue) => request.Side is { } side ? cue + 1 + side : cue;
        SoundCue cue(string bank, int index) => new(Cue(bank, index), BusOf(bank));
        return request.Name switch
        {
            // A drum's hit sounds in its player's tone (Waiwai: its own, traced SE_GAME_NEIRO_100_L/R): centred for one
            // player, the left drum's panned left and the right drum's right in two-player play.
            "drum.don" or "drum.ka" => [new(Cue(drumBank(request), request.Name == "drum.don" ? 0 : 1), SoundBus.Drum)],
            "roll.voice" => [cue("VO_GAME", own(156))],
            "balloon.popped" => [cue("SE_GAME", own(0))],
            "kusudama.popped" => [cue("SE_GAME", 3)],
            "kusudama.failed" => [cue("SE_GAME", 5), cue("VO_GAME", own(159))],
            // VO_GAME triplets: 0 = 50 combo, 3k = 100k combo up to 5000 (cue 150).
            "combo.voice" => [cue("VO_GAME", own(request.Variant == 50 ? 0 : request.Variant / 100 * 3))],
            "banner.fail" => [cue("SE_GAME", own(9))],
            "banner.clear" => [cue("SE_GAME", own(6))],
            "banner.fullCombo" => [cue("SE_GAME", own(12)), cue("VO_GAME", own(153))],
            // Traced SE_GAME_COLLABO_00 cue 0 / 1 with the rare note: taken as the player who hit it (Variant).
            "waiwai.rare" => [cue("SE_GAME_COLLABO_00", request.Variant)],
            // Waiwai (traced session11-waiwai): SE_WAIENSO 0 as the song starts, 4 per together section, 2 per solo.
            "waiwai.start" => [cue("SE_WAIENSO", 0)],
            "waiwai.synchro" => [cue("SE_WAIENSO", 4)],
            "waiwai.solo" => [cue("SE_WAIENSO", 2)],
            // Waiwai's song ends on the results voice alone (traced session11-waiwai).
            "song.finished" => request.Mode == "waiwai" ? [cue("VO_RESULT", 0)] : [cue("VO_RESULT", 0), cue("SE_GAME", 15)],
            // A dan course's last song ends (traced sessions 30 and 31, as the course's shutter closes).
            "dan.finished" => [cue("VO_RESULT", 19), cue("SE_GAME", 15)],
            // The home player setup refused a player (SE_COM_COM_CARD_ERROR).
            "player.refused" => [cue("SE_COM", 12)],
            // The engine's own menus (settings, search, overlays): the drum feedback of the game's menus.
            "ui.don" => [new(Cue("SE_COM", 0), SoundBus.Drum)],
            "ui.ka" => [new(Cue("SE_COM", 3), SoundBus.Drum)],
            // SE_COM's coin cue (traced per credited coin) and its service credit cue (traced session-service-coin).
            "coin.insert" => [new(Cue("SE_COM", 9), SoundBus.Coin)],
            "coin.service" => [new(Cue("SE_COM", 10), SoundBus.Coin)],
            // A player joining from song select (as on the cabinet); song select ending into the dan dojo (traced session30).
            "select.join" => [cue("SE_SELECT", 0)],
            "select.dan" => [cue("SE_SELECT", 7)],
            // Screens' music (traced: each bgm/nub track opened as its screen loads).
            "music.dan" => [new(Music("JINGLE_DANI"), SoundBus.Music)],
            "music.tutorial" => [new(Music("JINGLE_DOJO"), SoundBus.Music)],
            "music.tutorial.waiwai" => [new(Music("JINGLE_WAITUTOR"), SoundBus.Music)],
            "music.tutorial.ai" => [new(Music("JINGLE_AIBTL_TUTOR"), SoundBus.Music)],
            // The audio calibration's clicks: a drum tone's cues (BIP bap bap bap).
            "click.accent" => [new(Cue("SE_GAME_NEIRO_018_C", 0), SoundBus.Drum)],
            "click.beat" => [new(Cue("SE_GAME_NEIRO_018_C", 1), SoundBus.Drum)],
            _ => [],
        };
    }

    private static string drumBank(SoundRequest request) =>
        (request.Mode == "waiwai" ? "SE_GAME_NEIRO_100" : $"SE_GAME_NEIRO_{request.Tone:000}") + request.Side switch
        {
            0 => "_L",
            1 => "_R",
            _ => "_C",
        };
}
