using System.Runtime.CompilerServices;

namespace TEKSteamClient.Utils.LZMA;

internal readonly struct RangeBitTreeDecoder(int numBitLevels)
{
	private readonly int _numBitLevels = numBitLevels;
	private readonly uint[] _bitStates = new uint[1 << numBitLevels];

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Initialize() => new Span<uint>(_bitStates).Fill(1024);
	public int Decode(ref RangeDecoder rangeDecoder)
	{
		int temp = 1;
		for (int i = 0; i < _numBitLevels; i++)
			temp = (temp << 1) + rangeDecoder.DecodeBitState(ref _bitStates[temp]);
		return temp - _bitStates.Length;
	}
	public int ReverseDecode(ref RangeDecoder rangeDecoder)
	{
		int symbol = 0;
		for (int i = 0, index = 1; i < _numBitLevels; i++)
		{
			int bit = rangeDecoder.DecodeBitState(ref _bitStates[index]);
			index = (index << 1) + bit;
			symbol |= bit << i;
		}
		return symbol;
	}
}