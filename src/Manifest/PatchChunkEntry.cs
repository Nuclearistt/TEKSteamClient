using System.Runtime.CompilerServices;

namespace TEKSteamClient.Manifest;

/// <summary>Steam depot patch chunk record.</summary>
public readonly struct PatchChunkEntry : IComparable<PatchChunkEntry>
{
	/// <summary>Index of the source chunk in its manifest chunk buffer.</summary>
	public required int SourceChunkIndex { get; init; }
	/// <summary>Index of the target chunk in its manifest chunk buffer.</summary>
	public required int TargetChunkIndex { get; init; }
	/// <summary>Patch data.</summary>
	public required ReadOnlyMemory<byte> Data { get; init; }
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CompareTo(PatchChunkEntry other) => TargetChunkIndex.CompareTo(other.TargetChunkIndex);
}