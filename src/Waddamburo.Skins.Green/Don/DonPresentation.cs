using Waddamburo.Lumen.Rendering;

using Waddamburo.Game.Scores;

namespace Waddamburo.Skins.Green.Don;

public readonly record struct DonMotionRequest(int PlayerIndex, string? OneShot, string? Loop);

/// <summary>
/// What Don wears: a whole costume (full/cos/cos_NNN000) or divided parts (head, body, face paint).
/// Costume 0 is "no costume": the default parts.
/// </summary>
public readonly record struct DonCostume(int? Whole, int Head, int Body, int Paint)
{
    public static DonCostume Default { get; } = new(null, 0, 0, 0);

    /// <summary>Green's dan costume: the renderer draws the model of the wearer's displayed rank.</summary>
    public const int Dan = 36;

    public static DonCostume FromWhole(int id) => id <= 0 ? Default : new(id, 0, 0, 0);
}

/// <summary>The Don a profile's look dresses (<see cref="DonLook"/> is the account's data).</summary>
public static class DonLooks
{
    public static DonCostume ToCostume(this DonLook look) => look.Costume switch
    {
        [> 0 and var whole, ..] => DonCostume.FromWhole(whole),
        [_, var head, var body, var paint, ..] => new DonCostume(null, head, body, paint),
        _ => DonCostume.Default,
    };
}

public enum DonPresentationLayout
{
    /// <summary>The second player's Don-chan on the right, the mirrored camera turning them inward (entry, Song Select).</summary>
    Standard,
    /// <summary>The second player's Don-chan unmirrored, facing the same way as the first (results).</summary>
    OpposedPlayers,
    Gameplay,
    Retry,
    RetrySuccess,
    /// <summary>The balloon overlay: a closer menu-style view, the balloon at Don's mouth.</summary>
    Balloon,
    /// <summary>The kusudama overlay: the menu view, both Dons facing the centre.</summary>
    Kusudama,
}

/// <summary>
/// Renderer-facing Don state owned outside Lumen. Implementations advance at the game tick and
/// provide GPU-backed native surfaces to the platform compositor.
/// </summary>
public interface IDonPresentationController
{
    void Reset(DonPresentationLayout layout);

    /// <summary>Changes the view while keeping the current motion.</summary>
    void SetCameraLayout(DonPresentationLayout layout) => Reset(layout);

    /// <summary>Changes one player's view (two-player gameplay: one Don in a balloon, the other playing).</summary>
    void SetCameraLayout(int playerIndex, DonPresentationLayout layout) => SetCameraLayout(layout);

    LumenNativeSurfaceKey GetSurface(int playerIndex);

    void SetMotion(DonMotionRequest request);

    /// <summary>
    /// Changes the loop without interrupting a running one-shot: the one-shot finishes into the
    /// new loop, and an idle that is already looping switches at once.
    /// </summary>
    void SetIdle(int playerIndex, string motionLoop) => SetMotion(new(playerIndex, null, motionLoop));

    /// <summary>Dresses a player's Don; kept across scenes.</summary>
    void SetCostume(int playerIndex, DonCostume costume)
    {
    }

    /// <summary>Dresses and colours a player's Don as their profile has it (null: the default Don).</summary>
    void SetLook(int playerIndex, DonLook? look)
    {
        SetCostume(playerIndex, look?.ToCostume() ?? DonCostume.Default);
        SetAccessory(playerIndex, look?.Puchi ?? 0);
    }

    /// <summary>
    /// Whether the installed data has a shop costume (kigurumi 3, body 4, head 5, puchi 7): TaikOnline serves the latest Green
    /// revision's goods, an older install may lack some.
    /// </summary>
    bool HasCostume(int type, int id) => true;

    /// <summary>The puchi chara floating beside a player's Don (0 = none); kept across scenes.</summary>
    void SetAccessory(int playerIndex, int puchi)
    {
    }

    /// <summary>Turns a player's Don model in the gameplay view (degrees; a modchart's). The other views ignore it.</summary>
    void SetModelRotation(int playerIndex, float yaw, float pitch, float roll)
    {
    }

    /// <summary>The entry's card dialog Don (the donExM marker, slot 2): shown while a read card waits for a drum.</summary>
    void SetDialogDon(bool visible)
    {
    }
}
