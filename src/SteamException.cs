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
		/// <summary>Connection to Steam CM server failed.</summary>
		CMConnectionFailed,
		/// <summary>[CM Client] Failed to get CDN server list.</summary>
		CMFailedToGetCDNServerList,
		/// <summary>[CM Client] Failed to get depot decryption key.</summary>
		CMFailedToGetDepotDecryptionKey,
		/// <summary>[CM Client] Failed to get depot manifest IDs from app info.</summary>
		CMFailedToGetManifestIds,
		/// <summary>[CM Client] Failed to get depot manifest request code.</summary>
		CMFailedToGetManifestRequestCode,
		/// <summary>[CM Client] Failed to get depot patch availability.</summary>
		CMFailedToGetPatchAvailablity,
		/// <summary>[CM Client] Failed to get workshop item details.</summary>
		CMFailedToGetWorkshopItemDetails,
		/// <summary>[CM Client] Log on failed.</summary>
		CMLogOnFailed,
		/// <summary>[CM Client] Not logged on.</summary>
		CMNotLoggedOn,
		/// <summary>Decryption key for specified depot is missing in <see cref="CDNClient.DepotDecryptionKeys"/>.</summary>
		DepotDecryptionKeyMissing,
		/// <summary>.scdelta file is corrupted.</summary>
		DepotDeltaCorrupted,
		/// <summary>Couldn't find latest manifest ID for the depot.</summary>
		DepotManifestIdNotFound,
		/// <summary>Chunk download failed (see <see cref="Exception.InnerException"/>).</summary>
		DownloadFailed,
		/// <summary>Manifest download failed (see <see cref="Exception.InnerException"/>).</summary>
		FailedToDownloadManifest,
		/// <summary>Patch download failed (see <see cref="Exception.InnerException"/>).</summary>
		FailedToDownloadPatch,
		/// <summary>App installation is corrupted.</summary>
		InstallationCorrupted,
		/// <summary>.scmanifest file is corrupted.</summary>
		ManifestCorrupted,
		/// <summary>CRC of downloaded manifest data doesn't match the one from HTTP response header.</summary>
		ManifestCrcMismatch,
		/// <summary>Not enough disk space is available for update operation.</summary>
		NotEnoughDiskSpace,
		/// <summary>.scpatch file is corrupted.</summary>
		PatchCorrupted,
		/// <summary>CRC of downloaded patch data doesn't match the one from HTTP response header.</summary>
		PatchCrcMismatch,
		/// <summary>.scvcache file is corrupted.</summary>
		ValidationCacheCorrupted
	}
}
/// <summary>An exception thrown when there is not enough disk space</summary>
public class SteamNotEnoughDiskSpaceException(long requiredSpace) : SteamException(ErrorType.NotEnoughDiskSpace)
{
	/// <summary>The amount of free space required by the client.</summary>
	public long RequiredSpace { get; } = requiredSpace;
}