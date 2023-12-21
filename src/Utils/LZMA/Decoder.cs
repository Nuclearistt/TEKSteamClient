using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TEKSteamClient.Utils.LZMA;

/// <summary>LZMA decoder implementation optimized for decompressing Steam chunks and refactored to use latest .NET/C# features.</summary>
/// <remarks>Credits to Igor Pavlov for the LZMA algorithm and its original C# implementation, and to SteamKit for the "train" functionality (not sure where they took it from, doesn't look like part of original LZMA).</remarks>
internal struct Decoder
{
	public Decoder() { }
	private byte[] _windowBuffer = [];
	private LenDecoder _lenDecoder = new();
	private LenDecoder _repLenDecoder = new();
	private LitDecoder _litDecoder;
	private readonly uint[] _bitStates = GC.AllocateUninitializedArray<uint>(546);
	private readonly RangeBitTreeDecoder _posAlignDecoder = new(4);
	private readonly RangeBitTreeDecoder[] _posSlotDecoders = [new(6), new(6), new(6), new(6)];
	/// <summary>Decodes a chunk of data, or optionally patches it upon existing chunk.</summary>
	/// <param name="input">Compressed chunk (or patch) data.</param>
	/// <param name="output">The buffer that receives decompressed chunk data.</param>
	/// <param name="trainData">Old chunk data to train upon for applying patch.</param>
	/// <returns><see langword="true"/> if decoding succeeds; otherwise, <see langword="false"/>.</returns>
	public bool Decode(ReadOnlySpan<byte> input, Span<byte> output, [Optional]ReadOnlySpan<byte> trainData)
	{
		ref byte inputRef = ref MemoryMarshal.GetReference(input);
		int dictSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref inputRef, 8));
		int dictSizeCheck = dictSize;
		if (dictSizeCheck is 0)
			dictSizeCheck++;
		if (dictSize < 0x1000)
			dictSize = 0x1000;
		if (dictSize > _windowBuffer.Length)
			_windowBuffer = GC.AllocateUninitializedArray<byte>(dictSize);
		var window = new LZWindow(_windowBuffer, output);
		int quotient = input[7] / 9;
		_litDecoder.Initialize(quotient % 5, input[7] % 9);
		int numPosStates = 1 << (quotient / 5);
		_lenDecoder.Initialize(numPosStates);
		_repLenDecoder.Initialize(numPosStates);
		int posStateMask = numPosStates - 1;
		int trainSize = 0;
		if (trainData.Length > 0)
			trainSize = window.Train(trainData);
		var rangeDecoder = new RangeDecoder(input[12..]);
		Span<uint> bitStates = new(_bitStates);
		bitStates.Fill(1024);
		Span<uint> matchBitStates = bitStates[..192];
		Span<uint> posBitStates = bitStates.Slice(192, 114);
		Span<uint> repBitStates = bitStates.Slice(306, 12);
		Span<uint> repG0BitStates = bitStates.Slice(318, 12);
		Span<uint> repG1BitStates = bitStates.Slice(330, 12);
		Span<uint> repG2BitStates = bitStates.Slice(342, 12);
		Span<uint> rep0LongBitStates = bitStates[354..];
		_posAlignDecoder.Initialize();
		foreach (var decoder in _posSlotDecoders)
			decoder.Initialize();
		int rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
		int pos = 0;
		int state = 0;
		if (trainData.Length is 0)
		{
			if (rangeDecoder.DecodeBitState(ref matchBitStates[0]) is not 0)
				return false;
			window.PutByte(_litDecoder.Decode(ref rangeDecoder, 0, 0));
			pos++;
		}
		while (pos < output.Length)
		{
			int posState = pos & posStateMask;
			if (rangeDecoder.DecodeBitState(ref matchBitStates[(state << 4) + posState]) is 0)
			{
				byte prevByte = window.GetByte(0);
				window.PutByte(state < 7 ? _litDecoder.Decode(ref rangeDecoder, pos, prevByte) : _litDecoder.Decode(ref rangeDecoder, pos, prevByte, window.GetByte(rep0)));
				if (state < 4)
					state = 0;
				else
					state -= state < 10 ? 3 : 6;
				pos++;
				continue;
			}
			int len;
			if (rangeDecoder.DecodeBitState(ref repBitStates[state]) is 0)
			{
				rep3 = rep2;
				rep2 = rep1;
				rep1 = rep0;
				len = _lenDecoder.Decode(ref rangeDecoder, posState) + 2;
				state = state < 7 ? 7 : 10;
				int posSlot = _posSlotDecoders[len < 6 ? len - 2 : 3].Decode(ref rangeDecoder);
				if (posSlot > 3)
				{
					int numDirectBits = (posSlot >> 1) - 1;
					rep0 = (2 | (posSlot & 1)) << numDirectBits;
					if (posSlot < 14)
					{
						int index = 1;
						int indexOffset = rep0 - posSlot - 1;
						int symbol = 0;
						for (int i = 0; i < numDirectBits; i++)
						{
							int bit = rangeDecoder.DecodeBitState(ref posBitStates[indexOffset + index]);
							index = (index << 1) + bit;
							symbol |= bit << i;
						}
						rep0 += symbol;
					}
					else
						rep0 += (rangeDecoder.Decode(numDirectBits - 4) << 4) + _posAlignDecoder.ReverseDecode(ref rangeDecoder);
				}
				else
					rep0 = posSlot;
			}
			else
			{
				if (rangeDecoder.DecodeBitState(ref repG0BitStates[state]) is 0)
				{
					if (rangeDecoder.DecodeBitState(ref rep0LongBitStates[(state << 4) + posState]) is 0)
					{
						state = state < 7 ? 9 : 11;
						window.PutByte(window.GetByte(rep0));
						pos++;
						continue;
					}
				}
				else
				{
					int dist;
					if (rangeDecoder.DecodeBitState(ref repG1BitStates[state]) is 0)
						dist = rep1;
					else
					{
						if (rangeDecoder.DecodeBitState(ref repG2BitStates[state]) is 0)
							dist = rep2;
						else
						{
							dist = rep3;
							rep3 = rep2;
						}
						rep2 = rep1;
					}
					rep1 = rep0;
					rep0 = dist;
				}
				len = _repLenDecoder.Decode(ref rangeDecoder, posState) + 2;
				state = state < 7 ? 8 : 11;
			}
			if (rep0 >= trainSize + pos || rep0 >= dictSizeCheck)
			{
				if (rep0 is int.MaxValue)
					break;
				else
					return false;
			}
			window.CopyBlock(rep0, len);
			pos += len;
		}
		window.Flush();
		return true;
	}
}