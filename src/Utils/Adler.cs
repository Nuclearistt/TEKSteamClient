using System.Runtime.Intrinsics.X86;
using TEKSteamClient.Utils.AdlerImpl;

namespace TEKSteamClient.Utils;

/// <summary>Hardware-accelerated Adler checksum utility.</summary>
public static class Adler
{
	private static readonly IAdlerImpl s_implementation =
		Avx2.IsSupported ? new AdlerAvx2()
		: Ssse3.IsSupported ? new AdlerSsse3()
		: new AdlerBase();
	/// <summary>Computes Adler checksum for specified data.</summary>
	/// <param name="data">Buffer containing binary data to compute checksum for.</param>
	/// <returns>Adler checksum value.</returns>
	public static uint ComputeChecksum(ReadOnlySpan<byte> data) => s_implementation.ComputeChecksum(data);
}