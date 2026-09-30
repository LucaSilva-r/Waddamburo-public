using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waddamburo.Game.Scores;

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
[JsonSerializable(typeof(ScoreClient.BestsResult))]
[JsonSerializable(typeof(ScoreClient.RankingsResult))]
[JsonSerializable(typeof(ScoreClient.PlaysResult))]
[JsonSerializable(typeof(FriendPairingClient.Request))]
[JsonSerializable(typeof(FriendPairingClient.Reply))]
[JsonSerializable(typeof(PairingClient.CardRequest))]
[JsonSerializable(typeof(AccountBook.StoredBook))]
internal sealed partial class ScoreJson : JsonSerializerContext;
