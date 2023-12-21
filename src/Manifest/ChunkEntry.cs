using System.Runtime.CompilerServices;
using TEKSteamClient.Utils;

namespace TEKSteamClient.Manifest;

/// <summary>Steam depot manifest chunk record.</summary>
public readonly struct ChunkEntry : IComparable<ChunkEntry>
{
	/// <summary>GID of the chunk.</summary>
	public required SHA1Hash Gid { get; init; }
	/// <summary>Offset of chunk data from the beginning of containing file.</summary>
	public required long Offset { get; init; }
	/// <summary>Size of LZMA-compressed chunk data.</summary>
	public required int CompressedSize { get; init; }
	/// <summary>Size of uncompressed chunk data.</summary>
	public required int UncompressedSize { get; init; }
	/// <summary>Adler checksum of chunk data.</summary>
	public required uint Checksum { get; init; }
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CompareTo(ChunkEntry other) => Gid.CompareTo(other.Gid);
}