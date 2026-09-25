using System.Text;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Cli;

/// <summary>
/// --login / --logout: a home player's TaikOnline account, asked for in the terminal (the game's
/// movies have no text entry). The token is kept in account.json beside the game data.
/// </summary>
internal static class AccountCommands
{
    public static int Run(ArcadeSettings arcade, string accountPath, bool login)
    {
        if (arcade.Server is not { } server)
        {
            Console.Error.WriteLine($"Set 'server = https://...' in {ArcadeSettings.FileName} first.");
            return 1;
        }
        return login ? logIn(arcade, server, accountPath) : logOut(arcade, server, accountPath);
    }

    private static int logIn(ArcadeSettings arcade, Uri server, string accountPath)
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
        account.Save(accountPath);
        Console.WriteLine($"Logged in as {(account.Name.Length > 0 ? account.Name : name)} (baid {account.Baid}). Scores are saved from now on.");
        return 0;
    }

    private static int logOut(ArcadeSettings arcade, Uri server, string accountPath)
    {
        if (ScoreAccount.Load(accountPath) is not { } account)
        {
            Console.WriteLine("Not logged in.");
            return 0;
        }
        try
        {
            using var http = ScoreClient.CreateHttp(server, arcade.ServerInsecure, account.Token);
            new ScoreClient(http).LogoutAsync().GetAwaiter().GetResult();
        }
        catch (HttpRequestException exception)
        {
            // The local token goes anyway; the server's copy can be revoked from the website.
            Console.Error.WriteLine($"Warning: the server did not revoke the token ({exception.Message}).");
        }
        File.Delete(accountPath);
        Console.WriteLine("Logged out. Plays not yet uploaded stay in scores.db until you log in again.");
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
