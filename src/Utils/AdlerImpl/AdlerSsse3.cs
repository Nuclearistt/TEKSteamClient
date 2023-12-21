using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace TEKSteamClient.Utils.AdlerImpl;

/// <summary>Adler checksum implementation utilizing SSSE3 instruction set.</summary>
internal class AdlerSsse3 : IAdlerImpl
{
	private static readonly Vector128<sbyte> s_multiplicationMask = Vector128.Create<sbyte>(
		[
			0x10, 0x0F, 0x0E, 0x0D, 0x0C, 0x0B, 0x0A, 0x09,
			0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01
		]);
	public uint ComputeChecksum(ReadOnlySpan<byte> data)
	{
		ref byte byteRef = ref MemoryMarshal.GetReference(data);
		ref Vector128<byte> vectorRef = ref Unsafe.As<byte, Vector128<byte>>(ref byteRef);
		uint a = 0, b = 0;
		nint index = 0;
		int numChunks = data.Length / 5552, remainder = data.Length % 5552;
		for (int i = 0; i < numChunks; i++)
		{
			for (int j = 0; j < 347; j++)
			{
				var vector = Unsafe.AddByteOffset(ref vectorRef, index);
				index += 16;
				b += a * 16 + unchecked((uint)Sse2.ConvertToInt32(
					Ssse3.HorizontalAdd(
						Ssse3.HorizontalAdd(
							Sse2.MultiplyAddAdjacent(
								Ssse3.MultiplyAddAdjacent(vector, s_multiplicationMask),
								Vector128<short>.One),
							Vector128<int>.Zero),
						Vector128<int>.Zero)));
				var sumVector = Sse2.SumAbsoluteDifferences(vector, Vector128<byte>.Zero).AsUInt64();
				a += (uint)(sumVector[0] + sumVector[1]);
			}
			a %= 65521;
			b %= 65521;
		}
		if (remainder > 0)
		{
			numChunks = remainder / 16;
			remainder %= 16;
			for (int i = 0; i < numChunks; i++)
			{
				var vector = Unsafe.AddByteOffset(ref vectorRef, index);
				index += 16;
				b += a * 16 + unchecked((uint)Sse2.ConvertToInt32(
					Ssse3.HorizontalAdd(
						Ssse3.HorizontalAdd(
							Sse2.MultiplyAddAdjacent(
								Ssse3.MultiplyAddAdjacent(vector, s_multiplicationMask),
								Vector128<short>.One),
							Vector128<int>.Zero),
						Vector128<int>.Zero)));
				var sumVector = Sse2.SumAbsoluteDifferences(vector, Vector128<byte>.Zero).AsUInt64();
				a += (uint)(sumVector[0] + sumVector[1]);
			}
			for (int i = 0; i < remainder; i++)
			{
				a += Unsafe.AddByteOffset(ref byteRef, index++);
				b += a;
			}
			a %= 65521;
			b %= 65521;
		}
		return a | (b << 16);
	}
}