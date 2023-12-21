using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace TEKSteamClient.Utils.AdlerImpl;

/// <summary>Adler checksum implementation utilizing AVX2 instruction set.</summary>
internal class AdlerAvx2 : IAdlerImpl
{
	private static readonly Vector256<sbyte> s_multiplicationMask = Vector256.Create<sbyte>(
		[
			0x20, 0x1F, 0x1E, 0x1D, 0x1C, 0x1B, 0x1A, 0x19, 0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
			0x10, 0x0F, 0x0E, 0x0D, 0x0C, 0x0B, 0x0A, 0x09, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01
		]);
	public uint ComputeChecksum(ReadOnlySpan<byte> data)
	{
		ref byte byteRef = ref MemoryMarshal.GetReference(data);
		ref Vector256<byte> vectorRef = ref Unsafe.As<byte, Vector256<byte>>(ref byteRef);
		uint a = 0, b = 0;
		nint index = 0;
		int numChunks = data.Length / 5536, remainder = data.Length % 5536;
		for (int i = 0; i < numChunks; i++)
		{
			for (int j = 0; j < 173; j++)
			{
				var vector = Unsafe.AddByteOffset(ref vectorRef, index);
				index += 32;
				var sumsVector = Avx2.HorizontalAdd(
					Avx2.HorizontalAdd(
						Avx2.MultiplyAddAdjacent(
							Avx2.MultiplyAddAdjacent(vector, s_multiplicationMask),
							Vector256<short>.One),
						Vector256<int>.Zero),
					Vector256<int>.Zero);
				b += a * 32 + unchecked((uint)Sse2.ConvertToInt32(Sse2.Add(sumsVector.GetLower(), sumsVector.GetUpper())));
				var sumVector256 = Avx2.SumAbsoluteDifferences(vector, Vector256<byte>.Zero);
				var sumVector128 = Sse2.Add(sumVector256.GetLower(), sumVector256.GetUpper()).AsUInt64();
				a += (uint)(sumVector128[0] + sumVector128[1]);
			}
			a %= 65521;
			b %= 65521;
		}
		if (remainder > 0)
		{
			numChunks = remainder / 32;
			remainder %= 32;
			for (int i = 0; i < numChunks; i++)
			{
				var vector = Unsafe.AddByteOffset(ref vectorRef, index);
				index += 32;
				var sumsVector = Avx2.HorizontalAdd(
					Avx2.HorizontalAdd(
						Avx2.MultiplyAddAdjacent(
							Avx2.MultiplyAddAdjacent(vector, s_multiplicationMask),
							Vector256<short>.One),
						Vector256<int>.Zero),
					Vector256<int>.Zero);
				b += a * 32 + unchecked((uint)Sse2.ConvertToInt32(Sse2.Add(sumsVector.GetLower(), sumsVector.GetUpper())));
				var sumVector256 = Avx2.SumAbsoluteDifferences(vector, Vector256<byte>.Zero);
				var sumVector128 = Sse2.Add(sumVector256.GetLower(), sumVector256.GetUpper()).AsUInt64();
				a += (uint)(sumVector128[0] + sumVector128[1]);
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