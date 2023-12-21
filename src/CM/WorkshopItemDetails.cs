namespace TEKSteamClient.CM;

/// <summary>Steam workshop item details.</summary>
public record WorkshopItemDetails(int Result, uint AppId, DateTime LastUpdated, ulong Id, ulong ManifestId, string Name, string PreviewUrl);