using System.Runtime.InteropServices;

namespace TEKSteamClient;

/// <summary>Called when connection to Steam CM server is terminated.</summary>
public delegate void DisconnectedHandler();
/// <summary>Called when Steam client sets up a progress with known total value.</summary>
/// <param name="type">Type of the progress.</param>
/// <param name="totalValue">The value of total part of the progress.</param>
/// <param name="initialValue">Initial value of current part of the progress.</param>
public delegate void ProgressInitiatedHandler(ProgressType type, long totalValue, [Optional]long initialValue);
/// <summary>Called when progress' current value is updated.</summary>
/// <param name="newValue">New current value of the progress.</param>
public delegate void ProgressUpdatedHandler(long newValue);
/// <summary>Called when Steam client status is updated.</summary>
/// <param name="newStatus">New status of the client.</param>
public delegate void StatusUpdatedHandler(Status newStatus);
/// <summary>Called when a validation file counter value is updated.</summary>
/// <param name="newValue">New current value of the counter.</param>
/// <param name="type">Type of the counter.</param>
public delegate void ValidationCounterUpdatedHandler(int newValue, ValidationCounterType type);
/// <summary>Indicates how the progress should be displayed in UI.</summary>
public enum ProgressType
{
	/// <summary>Regular numbers (e.g 123/789).</summary>
	Numeric,
	/// <summary>Binary units (e.g 789 KB/1.23 GB).</summary>
	Binary,
	/// <summary>Percentage of current/total (e.g 77.64%).</summary>
	Percentage
}
/// <summary>Indicates the category of files being counted.</summary>
public enum ValidationCounterType
{
	Matching,
	Mismatching,
	Missing
}