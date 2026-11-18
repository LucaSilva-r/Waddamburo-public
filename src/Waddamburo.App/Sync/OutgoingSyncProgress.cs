using System.Globalization;
using Waddamburo.Game.Flow;

namespace Waddamburo.App.Sync;

/// <summary>Tracks the files a paired device reports receiving, including across brief reconnects.</summary>
internal sealed class OutgoingSyncProgress : IDisposable
{
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(10);
    private readonly JobBoard _jobs;
    private readonly TimeProvider _time;
    private readonly ITimer _expiry;
    private readonly Dictionary<(string Receiver, Guid Transfer), Transfer> _transfers = [];
    private bool _disposed;

    private sealed record Transfer(JobBoard.Job Job, long Total, DateTimeOffset LastSeen);

    public OutgoingSyncProgress(JobBoard jobs, TimeProvider? time = null)
    {
        _jobs = jobs;
        _time = time ?? TimeProvider.System;
        _expiry = _time.CreateTimer(_ => expire(), null, Silence, TimeSpan.FromSeconds(2));
    }

    public bool Report(PairedDevice receiver, SyncLibrary library, string[] request)
    {
        if (request is not [_, _, _, var id, var total, var bytes, var state]
            || !Guid.TryParseExact(id, "N", out var transferId)
            || !long.TryParse(total, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || expected <= 0
            || !long.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var received) || received > expected
            || state is not ("running" or "done" or "stopped"))
            return false;

        var key = (receiver.Id, transferId);
        lock (_transfers)
        {
            if (_disposed)
                return false;
            if (!_transfers.TryGetValue(key, out var transfer))
            {
                if (state != "running")
                    return true; // short transfer or an already expired job
                transfer = new Transfer(_jobs.Start(Strings.T("job.sync_sending", library.Name, receiver.Name), 1000),
                    expected, default);
            }
            if (transfer.Total != expected)
                return false;

            transfer.Job.Report((int)(received / (double)expected * 1000));
            if (state == "running")
                _transfers[key] = transfer with { LastSeen = _time.GetUtcNow() };
            else
            {
                _transfers.Remove(key);
                if (state == "done")
                    transfer.Job.Complete();
                else
                    transfer.Job.Fail(Strings.T("job.sync_send_stopped"));
            }
            return true;
        }
    }

    private void expire()
    {
        lock (_transfers)
        {
            var now = _time.GetUtcNow();
            foreach (var (key, transfer) in _transfers.Where(entry => now - entry.Value.LastSeen >= Silence).ToArray())
            {
                transfer.Job.Fail(Strings.T("job.sync_send_disconnected"));
                _transfers.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        _expiry.Dispose();
        lock (_transfers)
        {
            _disposed = true;
            foreach (var transfer in _transfers.Values)
                transfer.Job.Fail(Strings.T("job.sync_send_stopped"));
            _transfers.Clear();
        }
    }
}
