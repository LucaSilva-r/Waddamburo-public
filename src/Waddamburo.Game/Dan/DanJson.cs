using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waddamburo.Game.Dan;

/// <summary>The saved dan catalogue (dan-courses.json), with the score JSON's options (snake_case) it was always written with.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DanCatalogue))]
internal sealed partial class DanJson : JsonSerializerContext;
