using System.Text;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Cli;

/// <summary>
/// --login / --logout for headless setups: a TaikOnline account asked for in the terminal and added
/// to the PC's accounts (accounts.json; the game's own way is the account picker's device code), or
/// every stored account logged out.
/// </summary>
internal static class AccountCommands
{
    public static int Run(ArcadeSettings arcade, AccountBook accounts, bool login)
    {
        if (arcade.Server is not { } server)
        {
            Console.Error.WriteLine($"Set 'server = https://...' in {ArcadeSettings.FileName} first.");
            return 1;
        }
        return login ? logIn(arcade, server, accounts) : logOut(arcade, server, accounts);
    }

    private static int logIn(ArcadeSettings arcade, Uri server, AccountBook accounts)
    {
        Console.Write($"TaikOnline ({server.Host}) username or email: ");
        var name = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Password: ");
        var password = readHidden();
        using var http = ScoreClient.CreateHttp(server, arcade.ServerInsecure);
        var client = new ScoreClient(http);
        var device = $"Waddamburo on {Environment.MachineName}";
        ScoreAccount account;
        try
        {
            try
            {
                account = client.LoginAsync(name, password, null, device).GetAwaiter().GetResult();
            }
            catch (TwoFactorRequiredException)
            {
                Console.Write("Two-factor code: ");
                account = client.LoginAsync(name, password, Console.ReadLine()?.Trim(), device).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine($"Login failed: {exception.Message}");
            return 1;
        }
        accounts.Add(account);
        Console.WriteLine($"Logged in as {(account.Name.Length > 0 ? account.Name : name)} (baid {account.Baid})"
            + $"{(accounts.DefaultBaid == account.Baid ? ", the default player" : "")}. Scores are saved from now on.");
        return 0;
    }

    private static int logOut(ArcadeSettings arcade, Uri server, AccountBook accounts)
    {
        if (accounts.Accounts.Count == 0)
        {
            Console.WriteLine("Not logged in.");
            return 0;
        }
        foreach (var account in accounts.Accounts.ToArray())
        {
            try
            {
                using var http = ScoreClient.CreateHttp(server, arcade.ServerInsecure, account.Token);
                new ScoreClient(http).LogoutAsync().GetAwaiter().GetResult();
            }
            catch (HttpRequestException exception)
            {
                // The local token goes anyway; the server's copy can be revoked from the website.
                Console.Error.WriteLine($"Warning: the server did not revoke {account.Name}'s token ({exception.Message}).");
            }
            accounts.Remove(account.Baid);
            Console.WriteLine($"Logged out {account.Name} (baid {account.Baid}).");
        }
        Console.WriteLine("Plays not yet uploaded stay in scores.db until that account logs in again.");
        return 0;
    }

    private static string readHidden()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        var text = new StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                    text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return text.ToString();
    }
}
