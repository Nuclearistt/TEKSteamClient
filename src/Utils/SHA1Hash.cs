using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace TEKSteamClient.Utils;

#pragma warning disable CS0649 //Warns that _f1 and _f2 are never assigned to, however their values are modified by copying to structure reference
/// <summary>Structure representing a SHA-1 hash and providing fastest comparison methods available in .NET.</summary>
public readonly struct SHA1Hash : IComparable<SHA1Hash>, IEquatable<SHA1Hash>
{
	/// <summary>Creates a new SHA-1 hash from its binary representation.</summary>
	/// <param name="data">Span containing SHA-1 hash data.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public SHA1Hash(ReadOnlySpan<byte> data) => Unsafe.CopyBlock(ref Unsafe.As<SHA1Hash, byte>(ref this), ref MemoryMarshal.GetReference(data), 20);
	private readonly Vector128<ulong> _f1;
	private readonly uint _f2;
	///<inheritdoc/>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Equals(SHA1Hash other) => _f2 == other._f2 && _f1 == other._f1;
	///<inheritdoc/>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CompareTo(SHA1Hash other)
	{
		int res = _f2.CompareTo(other._f2);
		return res is 0 ? _f1 == other._f1 ? 0 : Vector128.LessThan(_f1, other._f1) == default ? 1 : -1 : res;
	}
	///<inheritdoc/>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(object? obj) => obj is SHA1Hash hash && this == hash;
	///<inheritdoc/>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode()
	{
		var hashCode = default(HashCode);
		hashCode.Add(_f1);
		hashCode.Add(_f2);
		return hashCode.ToHashCode();
	}
	///<inheritdoc/>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override string ToString() => Convert.ToHexString(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<SHA1Hash, byte>(ref Unsafe.AsRef(in this)), 20));
	/// <summary>Compares two hashes to determine if they are equal.</summary>
	/// <param name="left">The hash to compare with <paramref name="right"/>.</param>
	/// <param name="right">The hash to compare with <paramref name="left"/>.</param>
	/// <returns><see langword="true"/> if <paramref name="left"/> is equal to <paramref name="right"/>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator ==(SHA1Hash left, SHA1Hash right) => left._f2 == right._f2 && left._f1 == right._f1;
	/// <summary>Compares two hashes to determine if they are not equal.</summary>
	/// <param name="left">The hash to compare with <paramref name="right"/>.</param>
	/// <param name="right">The hash to compare with <paramref name="left"/>.</param>
	/// <returns><see langword="true"/> if <paramref name="left"/> is not equal to <paramref name="right"/>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator !=(SHA1Hash left, SHA1Hash right) => left._f2 != right._f2 || left._f1 != right._f1;
}
#pragma warning restore CS0649