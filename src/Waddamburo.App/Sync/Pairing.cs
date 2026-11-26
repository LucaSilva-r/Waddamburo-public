using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Waddamburo.App.Sync;

/// <summary>
/// Pairing, the side that will pull (a phone): while its pairing page is open it answers discovery and shows a code;
/// the device holding the libraries (a PC) sends the code with a new token (<see cref="PairAsync"/>). Both keep the
/// token: from then on the phone syncs without a code. Ten wrong codes close it until it is opened again.
/// </summary>
public sealed class PairingListener : IDisposable
{
    private const int MaxWrongCodes = 10;
    private readonly TcpListener _listener = new(IPAddress.Any, SyncWire.PairPort);
    private readonly UdpClient _discovery;
    private readonly CancellationTokenSource _stop = new();
    private int _wrongCodes;

    public PairingListener(string name)
    {
        Name = name;
        Code = Random.Shared.Next(1000, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _listener.Start();
        _discovery = new UdpClient(new IPEndPoint(IPAddress.Any, SyncWire.PairPort)) { EnableBroadcast = true };
        _ = Task.Run(() => SyncWire.AnswerAsync(_discovery, SyncWire.PairProbe, name));
        _ = Task.Run(accept);
    }

    public string Name { get; }

    /// <summary>The code the other device must type.</summary>
    public string Code { get; }

    /// <summary>The device that paired, once one has.</summary>
    public PairedDevice? PairedWith { get; private set; }

    public bool LockedOut => Volatile.Read(ref _wrongCodes) >= MaxWrongCodes;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _discovery.Dispose();
    }

    private async Task accept()
    {
        while (!_stop.IsCancellationRequested && !LockedOut)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                // code, "pair", id, name, token
                if (await SyncWire.ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false) is not [var code, "pair", var id, var name, var token]
                    || code != Code || id.Length != 32 || token.Length != 64)
                {
                    // A 4-digit code is guessable, not in ten tries.
                    if (Interlocked.Increment(ref _wrongCodes) >= MaxWrongCodes)
                        _listener.Stop();
                    await SyncWire.WriteLengthAsync(stream, SyncWire.Refused, timeout.Token).ConfigureAwait(false);
                    continue;
                }
                var device = new PairedDevice(id, name, token, Serves: true);
                SyncWire.Pair(device);
                await SyncWire.WriteAnswerAsync(stream, Encoding.UTF8.GetBytes($"{SyncWire.DeviceId}\t{SyncWire.clean(Name)}"), timeout.Token).ConfigureAwait(false);
                PairedWith = device;
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested)
                    return;
            }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException)
            {
            }
        }
    }

    /// <summary>
    /// The holding side: pairs with <paramref name="device"/> (in pairing mode) given the code it shows. The device will
    /// pull from this one. Throws <see cref="SyncRefusedException"/> on a wrong code.
    /// </summary>
    public static async Task<PairedDevice> PairAsync(SyncDevice device, string code, CancellationToken cancellationToken = default)
    {
        var token = SyncWire.NewToken();
        using var client = await SyncWire.ConnectAsync(device.Address, SyncWire.PairPort, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var answer = await SyncWire.AskAsync(client.GetStream(), timeout.Token, code, "pair", SyncWire.DeviceId, Boot.DeviceName, token).ConfigureAwait(false);
        if (answer.Split('\t') is not [var id, var name] || id != device.Id)
            throw new InvalidDataException("Unexpected answer.");
        var paired = new PairedDevice(id, name, token, Serves: false);
        SyncWire.Pair(paired);
        return paired;
    }
}
