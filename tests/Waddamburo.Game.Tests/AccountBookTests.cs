using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class AccountBookTests
{
    [Fact]
    public void ReadsTheNameBoardTitleAndCostumeSets()
    {
        var account = System.Text.Json.JsonSerializer.Deserialize<ScoreAccount>("""
            {"token":"t","baid":1,"name":"どん","title":"ほんのきもち","title_plate":3,"dan_rank":18,"dan_gold":true,
               "look":{"costume":[32,0,0,0,0],"presets":[[32,0,0,0],[0,10,2,4],[0,0,0,0]],"face":"#000000","body":"#000000","limb":"#000000"}}
            """, ScoreClient.Json)!;
        Assert.Equal(("ほんのきもち", 3), (account.Profile.Title, account.Profile.TitlePlate));
        Assert.Equal((18, true), (account.Profile.DisplayDanRank, account.Profile.DanGold));
        Assert.Equal([0, 10, 2, 4], account.Profile.Look!.Presets![1]);
    }

    [Fact]
    public async Task RefreshPersistsTheEquippedRankOfflineAndClearsItWhenAnOlderServerOmitsIt()
    {
        var directory = Directory.CreateTempSubdirectory("waddamburo-rank").FullName;
        try
        {
            var book = AccountBook.Load(directory);
            book.Add(new ScoreAccount("token", 1, "Player") { DanRank = 12 });
            using var handler = new ProfileHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://taikonline.test/") };
            var client = new ScoreClient(http);
            handler.Json = """{"baid":1,"name":"Player","dan_rank":18,"dan_gold":true}""";

            await book.RefreshAsync(_ => client);

            var stored = AccountBook.Load(directory).Default!;
            Assert.Equal(("token", 18, true), (stored.Token, stored.Profile.DisplayDanRank, stored.Profile.DanGold));
            handler.Json = """{"baid":1,"name":"Player"}""";
            await book.RefreshAsync(_ => client);
            stored = AccountBook.Load(directory).Default!;
            Assert.Equal((0, false), (stored.Profile.DisplayDanRank, stored.Profile.DanGold));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(25, 25)]
    [InlineData(26, 0)]
    public void UnsupportedProfileRanksAreHidden(int rank, int expected)
    {
        Assert.Equal(expected, new ScoreProfile(1, "Player") { DanRank = rank }.DisplayDanRank);
    }

    private sealed class ProfileHandler : HttpMessageHandler
    {
        public string Json { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/wdb/me", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

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
    public void SavingReplacesTheBookWithoutChangingAnExistingReader()
    {
        var directory = Directory.CreateTempSubdirectory("waddamburo-accounts").FullName;
        try
        {
            var book = AccountBook.Load(directory);
            book.Add(new ScoreAccount("token", 1, "Before"));
            var path = Path.Combine(directory, AccountBook.FileName);
            var previous = File.ReadAllText(path);
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            book.Add(new ScoreAccount("token", 1, "After"));

            Assert.Equal("After", AccountBook.Load(directory).Default?.Name);
            // Read only after saving: an in-place rewrite would change what this existing reader sees.
            using var reader = new StreamReader(original);
            Assert.Equal(previous, reader.ReadToEnd());
            Assert.Equal(path, Assert.Single(Directory.EnumerateFileSystemEntries(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedSaveLeavesTheDestinationAndRemovesTheTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("waddamburo-accounts").FullName;
        try
        {
            var book = AccountBook.Load(directory);
            var path = Path.Combine(directory, AccountBook.FileName);
            Directory.CreateDirectory(path); // A directory cannot be replaced by the account file.
            var sentinel = Path.Combine(path, "keep.txt");
            File.WriteAllText(sentinel, "untouched");

            var exception = Record.Exception(() => book.Add(new ScoreAccount("token", 1, "Player")));

            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.Equal(path, Assert.Single(Directory.EnumerateFileSystemEntries(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SavesAccountsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Directory.CreateTempSubdirectory("waddamburo-accounts").FullName;
        try
        {
            var book = AccountBook.Load(directory);
            book.Add(new ScoreAccount("token", 1, "Before"));
            var path = Path.Combine(directory, AccountBook.FileName);
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
            File.SetUnixFileMode(path, ownerOnly | UnixFileMode.GroupRead);

            book.Add(new ScoreAccount("token", 1, "After"));

            Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
            Assert.Equal("After", AccountBook.Load(directory).Default?.Name);
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
