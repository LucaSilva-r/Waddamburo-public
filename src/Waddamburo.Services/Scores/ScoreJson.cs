using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waddamburo.Services.Scores;

/// <summary>
/// TaikOnline's JSON (snake_case) and accounts.json, generated at compile time so the game also
/// builds with NativeAOT (no reflection-based serialization there).
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ScoreAccount))]
[JsonSerializable(typeof(ScoreProfile))]
[JsonSerializable(typeof(DeviceLoginStart))]
[JsonSerializable(typeof(ChartUpload))]
[JsonSerializable(typeof(ScoreClient.LoginRequest))]
[JsonSerializable(typeof(ScoreClient.DeviceRequest))]
[JsonSerializable(typeof(ScoreClient.DeviceTokenRequest))]
[JsonSerializable(typeof(ScoreClient.RankingsRequest))]
[JsonSerializable(typeof(ScoreClient.PlaysRequest))]
[JsonSerializable(typeof(ScoreClient.AiBattleRequest))]
[JsonSerializable(typeof(Waddamburo.Game.Gameplay.AiBattleSession))]
[JsonSerializable(typeof(ScoreClient.BestsResult))]
[JsonSerializable(typeof(ScoreClient.RankingsResult))]
[JsonSerializable(typeof(ScoreClient.PlaysResult))]
[JsonSerializable(typeof(ScoreClient.DanPlaysRequest))]
[JsonSerializable(typeof(ScoreClient.DanPlaysResult))]
[JsonSerializable(typeof(ScoreClient.MedalSongsRequest))]
[JsonSerializable(typeof(ScoreClient.CreditEndRequest))]
[JsonSerializable(typeof(ScoreClient.CreditEndResult))]
[JsonSerializable(typeof(ScoreClient.MedalSongsResult))]
[JsonSerializable(typeof(ShopState))]
[JsonSerializable(typeof(ShopPurchase))]
[JsonSerializable(typeof(ScoreClient.PurchaseRequest))]
[JsonSerializable(typeof(ScoreClient.RewardsSeenRequest))]
[JsonSerializable(typeof(ScoreClient.LeaderboardResult))]
[JsonSerializable(typeof(ScoreClient.ScoresResult))]
[JsonSerializable(typeof(ScoreClient.ReplayResult))]
[JsonSerializable(typeof(ScoreClient.ReportRequest))]
[JsonSerializable(typeof(ScoreClient.MessageResult))]
[JsonSerializable(typeof(FriendPairingClient.Request))]
[JsonSerializable(typeof(FriendPairingClient.Reply))]
[JsonSerializable(typeof(PairingClient.CardRequest))]
[JsonSerializable(typeof(AccountBook.StoredBook))]
[JsonSerializable(typeof(Waddamburo.Game.Dan.DanCatalogue))]
internal sealed partial class ScoreJson : JsonSerializerContext;
