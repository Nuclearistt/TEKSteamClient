namespace TEKSteamClient.Utils.LZMA;

internal struct LenDecoder
{
	public LenDecoder() { }
	private uint _bitStateA;
	private uint _bitStateB;
	private readonly RangeBitTreeDecoder _highDecoder = new(8);
	private readonly RangeBitTreeDecoder[] _lowDecoder = [new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3)];
	private readonly RangeBitTreeDecoder[] _midDecoder = [new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3), new(3)];
	public void Initialize(int numPosStates)
	{
		_bitStateB = _bitStateA = 1024;
		for (int i = 0; i < numPosStates; i++)
		{
			_lowDecoder[i].Initialize();
			_midDecoder[i].Initialize();
		}
		_highDecoder.Initialize();
	}
	public int Decode(ref RangeDecoder rangeDecoder, int posState) => rangeDecoder.DecodeBitState(ref _bitStateA) is 0
		? _lowDecoder[posState].Decode(ref rangeDecoder)
		: (8 +
			(rangeDecoder.DecodeBitState(ref _bitStateB) is 0
			? _midDecoder[posState].Decode(ref rangeDecoder)
			: (8 + _highDecoder.Decode(ref rangeDecoder))));
}