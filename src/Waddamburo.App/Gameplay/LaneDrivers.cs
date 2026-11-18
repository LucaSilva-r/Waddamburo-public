using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// What plays a song's lanes besides the drums: the autoplay (home autoplay, dev autoplay, the attract demo),
/// AI-don's bot and its battle presentation, and the online echo of the partner's lane. A review's replays are
/// not here: the review owns the clock and the presentation reads them itself.
/// </summary>
internal sealed class LaneDrivers
{
    public GameplayAutoplay? Autoplay { get; set; }
    public AiDrummer? AiBot { get; set; }
    public AiBattlePresentation? Battle { get; set; }
    public OnlineEcho? Echo { get; set; }

    /// <summary>
    /// The lanes advance to <paramref name="time"/> (judgement time): the drums' <paramref name="keys"/>, or the
    /// autoplay's over <paramref name="autoplayKeys"/> (the drums it leaves the player), AI-don's lane by its bot,
    /// then the battle's sections. The echo is pumped by the caller (every frame, advanced or not).
    /// </summary>
    public void Advance(TaikoGameplayPresentation gameplay, SdlKeyboardSnapshot keys, SdlKeyboardSnapshot autoplayKeys, TimeSpan time)
    {
        var played = Autoplay?.Apply(autoplayKeys, time) ?? keys;
        gameplay.Advance(played, time, aiInput: AiBot is { } bot ? bot.Apply : null);
        Battle?.Advance(time, gameplay.Results);
    }

    /// <summary>The play stops being driven (a review, the effects editor, the song's end); the echo stays.</summary>
    public void Stop() => (Autoplay, AiBot, Battle) = (null, null, null);
}
