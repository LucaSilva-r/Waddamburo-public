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
            var file = JsonSerializer.Deserialize<StoredBook>(File.ReadAllText(path), ScoreClient.Json)!;
            return new AccountBook(path, [.. file.Accounts], file.Default);
        }
        var book = new AccountBook(path, [], null);
        var legacy = Path.Combine(directory, LegacyFileName);
        if (File.Exists(legacy))
        {
            var account = JsonSerializer.Deserialize<ScoreAccount>(File.ReadAllText(legacy), ScoreClient.Json)!;
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
                Add(account with { Name = current.Name, Look = current.Look, Avatar = current.Avatar, AccountName = current.AccountName });
        }
    }

    private void save()
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(_path, options);
        JsonSerializer.Serialize(stream, new StoredBook(_accounts, DefaultBaid), ScoreClient.Json);
    }

    private sealed record StoredBook(List<ScoreAccount> Accounts, long? Default);
}
