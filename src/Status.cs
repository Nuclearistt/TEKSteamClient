namespace TEKSteamClient;

/// <summary>Statuses that Steam client may have.</summary>
public enum Status
{
	/// <summary>A <see cref="Manifest.DepotManifest"/> is being downloaded.</summary>
	DownloadingManifest,
	/// <summary>A <see cref="Manifest.DepotManifest"/> is being loaded (decrypted and parsed or read from file).</summary>
	LoadingManifest,
	/// <summary>A <see cref="Manifest.DepotPatch"/> is being downloaded.</summary>
	DownloadingPatch,
	/// <summary>A <see cref="Manifest.DepotPatch"/> is being loaded (decrypted and parsed or read from file).</summary>
	LoadingPatch,
	/// <summary>An item is being validated.</summary>
	Validating,
	/// <summary>An item's download cache is being preallocated.</summary>
	Preallocating,
	/// <summary>An item is being downloaded.</summary>
	Downloading,
	/// <summary>An item installation is being patched.</summary>
	Patching,
	/// <summary>An item's new data is being written.</summary>
	WritingNewData,
	/// <summary>An item's old files are being removed.</summary>
	RemovingOldFiles
}