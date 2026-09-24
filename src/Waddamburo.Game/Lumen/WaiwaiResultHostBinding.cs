using Waddamburo.Game.Don;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public enum WaiwaiResultSoundKind { Effect, Voice }

/// <summary>A Waiwai results sound: the movie's PlaySE(id) / PlayVO(id); Sentence (the comment) picks its voice.</summary>
public readonly record struct WaiwaiResultSoundRequest(WaiwaiResultSoundKind Kind, int Id, int Sentence);

public interface IWaiwaiResultSoundController
{
    void RequestSound(WaiwaiResultSoundRequest request);
}

/// <summary>
/// Waiwai's results (waiwai_result.lm) as the game drives it (traced session9, 11, 13): the shared
/// gauge (SetGaugeType(1, segments)), the duet percentage, no revival, a rated comment
/// (SetMsgType(rating, id)), and the rare notes; the movie then plays itself, asking for sounds
/// (PlaySE / PlayVO) and Don motions (SetMotion), and reports AllFinish when done.
/// </summary>
public sealed class WaiwaiResultHostBinding(
    int gaugeSegments,
    int duetPercent,
    IReadOnlyList<bool> rareNotesHit,
    IDonPresentationController? don = null,
    IWaiwaiResultSoundController? sounds = null,
    Random? random = null) : ILumenHostBinding
{
    // MSGTYPE_BAD 0, NORMAL 1, GOOD 2, EXCELLENT 3 (the movie's constants). The comments are text.nut's
    // 70 sentences: 10 blue (bad), 30 pink, 30 gold (excellent); traced EXCELLENT ids 18 and 24 spoke
    // voice 54 + id, i.e. 14 + sentence 40 + id.
    // ponytail: the rating thresholds (the movie words the gauge at 35 and 50) and the pink split
    // between NORMAL and GOOD (15 each) are guesses; the id is uniform random.
    private static readonly int[] SentenceBase = [0, 10, 25, 40];
    private static readonly int[] SentenceCount = [10, 15, 15, 30];
    private readonly int _messageType = rating(gaugeSegments);
    private readonly int _messageId = (random ?? Random.Shared).Next(SentenceCount[rating(gaugeSegments)]);

    /// <summary>The comment shown: its index in the collabo's text.nut.</summary>
    public int Sentence => SentenceBase[_messageType] + _messageId;

    private static int rating(int segments) => segments >= 50 ? 3 : segments >= 35 ? 2 : segments >= 20 ? 1 : 0;

    /// <summary>The movie reported AllFinish: the scene can end.</summary>
    public bool Ended { get; private set; }

    public void Install(LumenHostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterObject("Lumen", lumen =>
        {
            lumen.RegisterMethod("PlaySE", call => sound(WaiwaiResultSoundKind.Effect, call));
            lumen.RegisterMethod("PlayVO", call => sound(WaiwaiResultSoundKind.Voice, call));
            lumen.RegisterMethod("AllFinish", _ =>
            {
                Ended = true;
                return LumenHostValue.Undefined;
            });
            if (don is not null)
                DonLumenBinding.RegisterMotion(context, lumen, don);
            foreach (var name in new[] { "Apply", "SelectCommonSound" })
                lumen.RegisterMethod(name, static _ => LumenHostValue.Undefined);
        });
    }

    public void Attach(LumenPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (don is not null)
            DonLumenBinding.Attach(player, don);
        call(player, "SetGaugeType", number(1), number(gaugeSegments));
        call(player, "SetDuetPercent", number(duetPercent));
        call(player, "SetRevive", LumenHostValue.FromBoolean(false));
        call(player, "SetMsgType", number(_messageType), number(_messageId));
        call(player, "SetMsgDisp", LumenHostValue.FromBoolean(true));
        call(player, "SetRareOnpNum", number(rareNotesHit.Count));
        for (var index = 0; index < rareNotesHit.Count; index++)
            call(player, "SetHitRareOnp", number(index), LumenHostValue.FromBoolean(rareNotesHit[index]));
    }

    private LumenHostValue sound(WaiwaiResultSoundKind kind, LumenHostCall call)
    {
        if (sounds is not null && call.Arguments.Length > 0 && call.Arguments[0].Kind == LumenHostValueKind.Number)
            sounds.RequestSound(new WaiwaiResultSoundRequest(kind, (int)call.Arguments[0].AsNumber(), Sentence));
        return LumenHostValue.Undefined;
    }

    private static LumenHostValue number(double value) => LumenHostValue.FromNumber(value);

    private static void call(LumenPlayer player, string name, params LumenHostValue[] arguments)
    {
        if (!player.TryInvokeCallback(name, arguments))
            throw new InvalidDataException($"Waiwai result movie is missing callback '{name}'.");
    }
}
