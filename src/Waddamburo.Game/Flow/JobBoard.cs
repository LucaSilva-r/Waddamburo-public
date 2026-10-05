namespace Waddamburo.Game.Flow;

/// <summary>
/// Work running in the background on this PC (hashing the library, uploading scores, ...), for the
/// notice sidebar. A job shows once it has run for <see cref="ShowAfter"/> (quick ones never flash up)
/// and stays a few seconds after it ends. Thread-safe: jobs report from their own tasks.
/// </summary>
public sealed class JobBoard
{
    public static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan KeepFinished = TimeSpan.FromSeconds(5);

    private readonly List<Job> _jobs = [];
    private readonly TimeProvider _time;

    public JobBoard(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>A job of <paramref name="total"/> steps (null: no count, an indeterminate bar).</summary>
    public Job Start(string title, int? total = null)
    {
        var job = new Job(title, total, _time);
        lock (_jobs)
        {
            _jobs.RemoveAll(job => job.EndedAt is { } ended && _time.GetUtcNow() - ended > KeepFinished);
            _jobs.Add(job);
        }
        return job;
    }

    /// <summary>The jobs to show now, oldest first.</summary>
    public IReadOnlyList<Job> Visible
    {
        get
        {
            var now = _time.GetUtcNow();
            lock (_jobs)
                return [.. _jobs.Where(job => job.EndedAt is { } ended
                    ? now - ended <= KeepFinished && ended - job.StartedAt >= ShowAfter
                    : now - job.StartedAt >= ShowAfter)];
        }
    }

    public sealed class Job
    {
        private readonly TimeProvider _time;
        private int _done;

        internal Job(string title, int? total, TimeProvider time)
        {
            Title = title;
            Total = total;
            _time = time;
            StartedAt = time.GetUtcNow();
        }

        public string Title { get; }

        public int? Total { get; }

        public int Done => Volatile.Read(ref _done);

        public DateTimeOffset StartedAt { get; }

        public DateTimeOffset? EndedAt { get; private set; }

        /// <summary>Why it failed (null while running or after a success).</summary>
        public string? Failure { get; private set; }

        /// <summary>0..1, or null for a job without a count.</summary>
        public double? Fraction => Total is > 0 and var total ? Math.Clamp(Done / (double)total, 0, 1) : null;

        public void Report(int done) => Volatile.Write(ref _done, done);

        public void Complete() => EndedAt ??= _time.GetUtcNow();

        public void Fail(string reason)
        {
            Failure = reason;
            Complete();
        }
    }
}
