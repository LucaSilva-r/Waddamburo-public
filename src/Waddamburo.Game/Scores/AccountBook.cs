using System.Text.Json;

namespace Waddamburo.Game.Scores;

/// <summary>
/// The accounts stored on a home PC (accounts.json beside the game data): who can be picked in the
/// entry, and the default one that joins by itself. Written owner-only: tokens are password equivalents.
/// </summary>
public sealed class AccountBook
{
    public const string FileName = "accounts.json";

    /// <summary>The single-account file of earlier versions, migrated on load.</summary>
    public const string LegacyFileName = "account.json";

    private readonly string _path;
    private readonly List<ScoreAccount> _accounts;

    private AccountBook(string path, List<ScoreAccount> accounts, long? defaultBaid)
    {
        _path = path;
        _accounts = accounts;
        DefaultBaid = defaultBaid;
    }

    // The game thread reads the book while the startup refresh updates it in the background.
    private readonly object _sync = new();

    public IReadOnlyList<ScoreAccount> Accounts
    {
        get
        {
            lock (_sync)
                return [.. _accounts];
        }
    }

    public long? DefaultBaid { get; private set; }

    /// <summary>The account that joins the first drum by itself, if one is set.</summary>
    public ScoreAccount? Default
    {
        get
        {
            lock (_sync)
                return _accounts.FirstOrDefault(account => account.Baid == DefaultBaid);
        }
    }

    /// <summary>Reads accounts.json in <paramref name="directory"/>, migrating account.json into it once.</summary>
    public static AccountBook Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), ScoreJson.Default.StoredBook)!;
            return new AccountBook(path, [.. file.Accounts], file.Default);
        }
        var book = new AccountBook(path, [], null);
        var legacy = Path.Combine(directory, LegacyFileName);
        if (File.Exists(legacy))
        {
            var account = JsonSerializer.Deserialize(File.ReadAllText(legacy), ScoreJson.Default.ScoreAccount)!;
            book.Add(account, makeDefault: true);
            File.Delete(legacy);
        }
        return book;
    }

    /// <summary>Adds an account (or replaces the stored one for the same player) and saves; the first becomes the default.</summary>
    public void Add(ScoreAccount account, bool makeDefault = false)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_sync)
        {
            var index = _accounts.FindIndex(stored => stored.Baid == account.Baid);
            if (index >= 0)
                _accounts[index] = account;
            else
                _accounts.Add(account);
            if (makeDefault || index < 0 && _accounts.Count == 1)
                DefaultBaid = account.Baid;
            save();
        }
    }

    public void Remove(long baid)
    {
        lock (_sync)
        {
            _accounts.RemoveAll(account => account.Baid == baid);
            if (DefaultBaid == baid)
                DefaultBaid = null;
            save();
        }
    }

    /// <summary>Sets the account that joins by itself (null: nobody, every player picks).</summary>
    public void SetDefault(long? baid)
    {
        lock (_sync)
        {
            DefaultBaid = baid is { } value && _accounts.Any(account => account.Baid == value) ? value : null;
            save();
        }
    }

    /// <summary>
    /// Brings every account's name, look and avatar up to date from the server; an account whose
    /// token was revoked (on the website's device list) is removed.
    /// </summary>
    public async Task RefreshAsync(Func<ScoreAccount, ScoreClient> clientFor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientFor);
        foreach (var account in Accounts)
        {
            var current = await clientFor(account).MeAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                Console.Error.WriteLine($"Warning ACCOUNT: {account.Name}'s login was revoked; the account was removed.");
                Remove(account.Baid);
            }
            else
                Apply(current);
        }
    }

    /// <summary>Takes a stored account's name, look and name board from a fresh profile (the server's, or a realtime push).</summary>
    public void Apply(ScoreProfile current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (Accounts.FirstOrDefault(account => account.Baid == current.Baid) is { } account)
            Add(account with { Name = current.Name, Look = current.Look, Avatar = current.Avatar, AccountName = current.AccountName,
                Title = current.Title, TitlePlate = current.TitlePlate, DanRank = current.DanRank, DanGold = current.DanGold });
    }

    private void save()
    {
        // Keep the old book intact until the replacement has been written and closed.
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        // Only clean up after we have created the temporary file ourselves.
        var stream = new FileStream(temporary, options);
        try
        {
            using (stream)
            {
                JsonSerializer.Serialize(stream, new StoredBook(_accounts, DefaultBaid), ScoreJson.Default.StoredBook);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
                File.Replace(temporary, _path, destinationBackupFileName: null);
            else
                File.Move(temporary, _path);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A cleanup failure must not hide the original save error.
            }
        }
    }

    internal sealed record StoredBook(List<ScoreAccount> Accounts, long? Default);
}
