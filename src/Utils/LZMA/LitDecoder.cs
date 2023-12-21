using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TEKSteamClient.Utils.LZMA;

internal struct LitDecoder
{
	private int _numPrevBits;
	private int _posMask;
	private uint[,] _bitStates;
	public void Initialize(int numPosBits, int numPrevBits)
	{
		int posMask = (1 << numPosBits) - 1;
		if (_posMask != posMask || _numPrevBits != numPrevBits)
		{
			_numPrevBits = numPrevBits;
			_posMask = posMask;
			_bitStates = new uint[1 << (numPrevBits + numPosBits), 768];
		}
		MemoryMarshal.CreateSpan(ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetArrayDataReference(_bitStates)), _bitStates.Length).Fill(1024);
	}
	public readonly byte Decode(ref RangeDecoder randeDecoder, int pos, int prevByte)
	{
		int decoderIndex = ((pos & _posMask) << _numPrevBits) + (prevByte >> (8 - _numPrevBits));
		int symbol = 1;
		while (symbol < 256)
			symbol = (symbol << 1) | randeDecoder.DecodeBitState(ref _bitStates[decoderIndex, symbol]);
		return (byte)symbol;
	}
	public readonly byte Decode(ref RangeDecoder rangeDecoder, int pos, int prevByte, int matchByte)
	{
		int decoderIndex = ((pos & _posMask) << _numPrevBits) + (prevByte >> (8 - _numPrevBits));
		int symbol = 1;
		while (symbol < 256)
		{
			int matchBit = (matchByte >> 7) & 1;
			int bit = rangeDecoder.DecodeBitState(ref _bitStates[decoderIndex, ((matchBit + 1) << 8) + symbol]);
			matchByte <<= 1;
			symbol = (symbol << 1) | bit;
			if (matchBit != bit)
			{
				while (symbol < 256)
					symbol = (symbol << 1) | rangeDecoder.DecodeBitState(ref _bitStates[decoderIndex, symbol]);
				break;
			}
		}
		return (byte)symbol;
	}
}