namespace TEKSteamClient.Utils.AdlerImpl;

/// <summary>Adler checksum algorithm implementation interface.</summary>
internal interface IAdlerImpl
{
	/// <summary>Computes Adler checksum for specified data.</summary>
	/// <param name="data">Buffer containing binary data to compute checksum for.</param>
	/// <returns>Adler checksum value.</returns>
	public uint ComputeChecksum(ReadOnlySpan<byte> data);
}