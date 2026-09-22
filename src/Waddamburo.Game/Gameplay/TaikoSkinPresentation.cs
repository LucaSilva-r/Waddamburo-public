using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>Authored gameplay-skin movies, any of which a skin may omit.</summary>
public sealed record TaikoSkinMovies(
    LumenPlayer? Chibi = null,
    LumenPlayer? DonBackground = null,
    LumenPlayer? Dancers = null,
    LumenPlayer? FeverBackground = null,
    LumenPlayer? Fever = null,
    LumenPlayer? Roll = null);

/// <summary>
/// Drives the gameplay skin from play state:
/// <list type="bullet">
/// <item>every Good/Great sends a runner across the character strip and every miss a soul;</item>
/// <item>the character backdrop shows hit/miss;</item>
/// <item>one more dancer joins per quarter of the clear line;</item>
/// <item>the fever background (and the backdrop's fever state) follows the clear line;</item>
/// <item>the fever overlay follows a full gauge;</item>
/// <item>each roll hit spawns a roll character.</item>
/// </list>
/// Go-Go is not a skin event. Callbacks a skin does not define are skipped.
/// </summary>
public sealed class TaikoSkinPresentation
{
    private readonly TaikoSkinMovies _movies;
    private bool? _cleared;
    private bool? _full;

    public TaikoSkinPresentation(TaikoSkinMovies movies)
    {
        _movies = movies;
        call(movies.Roll, "SetPlayerNum", LumenHostValue.FromNumber(1));
    }

    public void OnJudged(TaikoNoteJudgement judgement, TaikoSoulGauge gauge)
    {
        if (judgement.StrongHitCompleted || judgement.Result is not { } result) return;
        var hit = result != TaikoHitResult.Miss;
        call(_movies.Chibi, hit ? "Create" : "CreateTamashii");
        call(_movies.DonBackground, "SetHit", LumenHostValue.FromBoolean(hit));
        call(_movies.Dancers, "SetDancerNum", LumenHostValue.FromNumber(DancerCount(gauge)));
        var cleared = gauge.State != TaikoGaugeState.BelowClear;
        if (_cleared != cleared)
        {
            _cleared = cleared;
            call(_movies.FeverBackground, "SetFever", LumenHostValue.FromBoolean(cleared));
            call(_movies.DonBackground, "SetFever", LumenHostValue.FromBoolean(cleared));
        }
        var full = gauge.State == TaikoGaugeState.Full;
        if (_full != full)
        {
            _full = full;
            call(_movies.Fever, "SetFever", LumenHostValue.FromBoolean(full));
        }
    }

    public void OnRollHit() => call(_movies.Roll, "Create");

    /// <summary>One dancer, plus one per quarter of the clear line reached (the movie caps it).</summary>
    public static int DancerCount(TaikoSoulGauge gauge) => 1 + gauge.Value * 4 / gauge.Clear;

    private static void call(LumenPlayer? movie, string name, params LumenHostValue[] arguments) =>
        movie?.TryInvokeCallback(name, arguments);
}
