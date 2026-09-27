using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class AccountBookTests
{
    [Fact]
    public void MigratesTheSingleAccountFileAndKeepsTheDefaultRules()
    {
        var directory = Directory.CreateTempSubdirectory("waddamburo-accounts").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, AccountBook.LegacyFileName),
                """{"token":"t1","baid":24,"name":"あいいぁい"}""");
            var book = AccountBook.Load(directory);
            Assert.Equal(24, book.Default?.Baid);
            Assert.False(File.Exists(Path.Combine(directory, AccountBook.LegacyFileName)));

            book.Add(new ScoreAccount("t2", 25, "どんちゃん"));
            book.SetDefault(null);
            book.Add(new ScoreAccount("t1", 24, "renamed")); // a refresh: no default comes back
            Assert.Null(book.Default);

            var reloaded = AccountBook.Load(directory);
            Assert.Equal(["renamed", "どんちゃん"], reloaded.Accounts.Select(static account => account.Name));
            reloaded.SetDefault(25);
            reloaded.Remove(25);
            Assert.Null(AccountBook.Load(directory).Default);
            Assert.Equal("t1", AccountBook.Load(directory).Accounts.Single().Profile.Token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FriendPairingGivesTheFriendsProfileAndStartsOverAfterwards()
    {
        var pairing = new FriendPairingClient(new HttpClient());
        Assert.Equal(new PairingState.Active("123456", TimeSpan.FromSeconds(30)),
            pairing.Apply(new FriendPairingClient.Reply("active", "s1", "123456", 30, null, null)));
        var friend = Assert.IsType<PairingState.Visitor>(pairing.Apply(new FriendPairingClient.Reply(
            "claimed", "s1", null, null, "c1", new ScoreProfile(25, "どんちゃん") { Token = "friend" })));
        Assert.Equal("friend", friend.Profile.Token);
        Assert.IsType<PairingState.Rejected>(pairing.Apply(new FriendPairingClient.Reply("rejected", "s2", null, null, "c2", null)));
        Assert.IsType<PairingState.Closed>(pairing.Apply(new FriendPairingClient.Reply("complete", "s2", null, null, null, null)));
    }
}
