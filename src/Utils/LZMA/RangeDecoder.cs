using System.Runtime.CompilerServices;

namespace TEKSteamClient.Utils.LZMA;

internal ref struct RangeDecoder
{
	public RangeDecoder(ReadOnlySpan<byte> data)
	{
		_data = data;
		//Unrolled loop
		_code = (_code << 8) | NextByte();
		_code = (_code << 8) | NextByte();
		_code = (_code << 8) | NextByte();
		_code = (_code << 8) | NextByte();
		_code = (_code << 8) | NextByte();
	}
	private int _byteIndex;
	private uint _code;
	private uint _range = uint.MaxValue;
	private readonly ReadOnlySpan<byte> _data;
	public int Decode(int numTotalBits)
	{
		int result = 0;
		for (int i = 0; i < numTotalBits; i++)
		{
			_range >>= 1;
			uint temp = (_code - _range) >> 31;
			_code -= _range & (temp - 1);
			result = (result << 1) | (1 - (int)temp);
			if (_range < 16777216)
			{
				_code = (_code << 8) | NextByte();
				_range <<= 8;
			}
		}
		return result;
	}
	public int DecodeBitState(ref uint bitState)
	{
		uint newBound = (_range >> 11) * bitState;
		if (_code < newBound)
		{
			_range = newBound;
			bitState += (2048 - bitState) >> 5;
			if (_range < 16777216)
			{
				_code = (_code << 8) | NextByte();
				_range <<= 8;
			}
			return 0;
		}
		else
		{
			_code -= newBound;
			_range -= newBound;
			bitState -= bitState >> 5;
			if (_range < 16777216)
			{
				_code = (_code << 8) | NextByte();
				_range <<= 8;
			}
			return 1;
		}
	}
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public uint NextByte() => _byteIndex < _data.Length ? _data[_byteIndex++] : uint.MaxValue;
}