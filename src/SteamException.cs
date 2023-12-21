namespace TEKSteamClient;

/// <summary>An exception that occurred in Steam client and is not critical.</summary>
public class SteamException : Exception
{
	/// <summary>Creates a new Steam exception with specified error type.</summary>
	/// <param name="type">Type of error that caused this exception.</param>
	public SteamException(ErrorType type) : base(type.ToString()) => Type = type;
	/// <summary>Initializes a new Steam exception with specified error type and error code.</summary>
	/// <param name="type">Type of error that caused this exception.</param>
	/// <param name="errorCode">Extra error code.</param>
	public SteamException(ErrorType type, int errorCode) : base($"{type}:{errorCode}")
	{
		Type = type;
		ErrorCode = errorCode;
	}
	/// <summary>Creates a new Steam exception with specified error type and inner exception.</summary>
	/// <param name="type">Type of error that caused this exception.</param>
	/// <param name="inner">Inner exception that is the cause of current exception.</param>
	public SteamException(ErrorType type, Exception? inner) : base(type.ToString(), inner) => Type = type;
	/// <summary>Extra error code optionally provided by the exception.</summary>
	public int ErrorCode { get; }
	/// <summary>Type of error that caused this exception.</summary>
	public ErrorType Type { get; }
	/// <summary>Describes type of error that caused the exception.</summary>
	public enum ErrorType
	{
		CMConnectionFailed,
		CMFailedToGetCDNServerList,
		CMFailedToGetDepotDecryptionKey,
		CMFailedToGetManifestIds,
		CMFailedToGetManifestRequestCode,
		CMFailedToGetPatchAvailablity,
		CMFailedToGetWorkshopItemDetails,
		CMLogOnFailed,
		CMNotLoggedOn,
		DepotDecryptionKeyMissing,
		DepotDeltaCorrupted,
		DepotManifestIdNotFound,
		DownloadFailed,
		FailedToDownloadManifest,
		FailedToDownloadPatch,
		InstallationCorrupted,
		ManifestCorrupted,
		ManifestCrcMismatch,
		NotEnoughDiskSpace,
		PatchCorrupted,
		PatchCrcMismatch,
		ValidationCacheCorrupted
	}
}
/// <summary>An exception thrown when there is not enough disk space</summary>
public class SteamNotEnoughDiskSpaceException(long requiredSpace) : SteamException(ErrorType.NotEnoughDiskSpace)
{
	/// <summary>The amount of free space required by the client.</summary>
	public long RequiredSpace { get; } = requiredSpace;
}