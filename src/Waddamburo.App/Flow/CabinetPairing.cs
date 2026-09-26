using System.Security.Cryptography;
using System.Text;
using Waddamburo.App.Presentation;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Flow;

/// <summary>
/// Cabinet login without a card reader: while the attract loop runs, polls TaikOnline's pairing
/// every 2 s and shows the code on the pill; a card sent from the website waits (with the player's
/// name on the pill) for the next credit, which then saves under that profile.
/// </summary>
internal sealed class CabinetPairing : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    // ponytail: a paired card waits this long for its credit; a real reader would hold it until removed.
    private static readonly TimeSpan CardLifetime = TimeSpan.FromSeconds(90);
    private readonly PairingClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private volatile bool _accepting;
    private bool _open;
    private string? _code;
    private DateTime _codeDeadline;
    private ScoreProfile? _card;
    private DateTime _cardDeadline;
    private string? _lastError;

    public CabinetPairing(HttpClient http)
    {
        _client = new PairingClient(http, CabinetId);
        _ = Task.Run(loop);
    }

    /// <summary>8 hex digits, stable per machine (the server keys sessions and rate limits by it).</summary>
    public static string CabinetId { get; } =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..8];

    /// <summary>Set by the game every tick: the attract loop is running, a card may arrive.</summary>
    public bool Accepting
    {
        set => _accepting = value;
    }

    /// <summary>A card was read but not accepted (unknown card, or the lookup failed): shown once as the red band.</summary>
    public bool TakeCardFailure()
    {
        lock (_sync)
        {
            var failed = _cardFailed;
            _cardFailed = false;
            return failed;
        }
    }

    private bool _cardFailed;

    /// <summary>The paired card for a credit that is starting (null: a guest).</summary>
    public ScoreProfile? TakeCard()
    {
        lock (_sync)
        {
            var card = DateTime.UtcNow < _cardDeadline ? _card : null;
            _card = null;
            return card;
        }
    }

    public void UpdatePill(PairingPill pill)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (_card is { } card && now < _cardDeadline)
                pill.Show(card.Name.Length > 0 ? card.Name : $"#{card.Baid}", null);
            else if (_accepting && _code is { } code && now < _codeDeadline)
                pill.Show($"{code[..3]}-{code[3..]}", ((int)Math.Ceiling((_codeDeadline - now).TotalSeconds)).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            else
                pill.Hide();
        }
    }

    public void Dispose() => _stop.Cancel();

    private async Task loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                bool accepting;
                lock (_sync)
                    accepting = _accepting && (_card is null || DateTime.UtcNow >= _cardDeadline);
                if (accepting || _open)
                    await poll(accepting).ConfigureAwait(false);
                _lastError = null;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                if (_lastError != exception.Message)
                    Console.Error.WriteLine($"Warning PAIRING: {_lastError = exception.Message}");
                lock (_sync)
                    _code = null;
            }
            try
            {
                await Task.Delay(PollInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task poll(bool accepting)
    {
        var state = await _client.PollAsync(accepting, _stop.Token).ConfigureAwait(false);
        _open = accepting && state is not PairingState.Closed;
        switch (state)
        {
            case PairingState.Active active:
                lock (_sync)
                {
                    _code = active.Code;
                    _codeDeadline = DateTime.UtcNow + active.ExpiresIn;
                }
                break;
            case PairingState.Claimed claimed:
                ScoreProfile? profile = null;
                try
                {
                    // Diagnostic: WADDAMBURO_REJECT_CARDS=1 treats every card as unregistered (the red band).
                    profile = Environment.GetEnvironmentVariable("WADDAMBURO_REJECT_CARDS") == "1" ? null
                        : await _client.ResolveCardAsync(claimed.AccessCode, _stop.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                {
                    Console.Error.WriteLine($"Warning PAIRING: the card could not be looked up ({exception.Message}).");
                }
                Console.WriteLine(profile is null ? "Pairing: the card was rejected (no TaikOnline player)."
                    : $"Pairing: {profile.Name} (baid {profile.Baid}) is ready for the next credit.");
                lock (_sync)
                {
                    _code = null;
                    _card = profile;
                    _cardFailed = profile is null;
                    _cardDeadline = DateTime.UtcNow + CardLifetime;
                }
                break;
            default:
                lock (_sync)
                    _code = null;
                break;
        }
    }
}
