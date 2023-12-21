using System.Text.Json.Serialization;

namespace TEKSteamClient;

/// <summary>Steam Web API CM server list response JSON object.</summary>
internal class CMListResponse
{
	public required ServerList Response { get; init; }
	public class ServerList
	{
		public required string[] ServerlistWebsockets { get; init; }
	}
}
/// <summary>General Steam client JSON context.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
	PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
	PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CMListResponse))]
internal partial class JsonContext : JsonSerializerContext { }