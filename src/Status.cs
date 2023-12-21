namespace TEKSteamClient;

/// <summary>Statuses that Steam client may have.</summary>
public enum Status
{
	DownloadingManifest,
	LoadingManifest,
	DownloadingPatch,
	LoadingPatch,
	Validating,
	Preallocating,
	Downloading,
	Patching,
	WritingNewData,
	RemovingOldFiles
}