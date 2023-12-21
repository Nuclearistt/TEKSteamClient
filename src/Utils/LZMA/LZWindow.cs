using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TEKSteamClient.Utils.LZMA;

internal ref struct LZWindow(Span<byte> buffer, Span<byte> output)
{
	private int _outputOffset;
	private int _pos;
	private int _streamPos;
	private readonly Span<byte> _buffer = buffer;
	private readonly Span<byte> _output = output;
	public void CopyBlock(int offset, int length)
	{
		for (int pos = _pos - offset - 1; length > 0; length--)
		{
			if (pos >= _buffer.Length)
				pos = 0;
			_buffer[_pos++] = _buffer[pos++];
			if (_pos == _buffer.Length)
				Flush();
		}
	}
	public void Flush()
	{
		int size = _pos - _streamPos;
		if (size is 0)
			return;
		Unsafe.CopyBlockUnaligned(
			ref Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(_output), _outputOffset),
			ref Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(_buffer), _streamPos),
			(uint)size);
		_outputOffset += size;
		if (_pos == _buffer.Length)
			_pos = 0;
		_streamPos = _pos;
	}
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void PutByte(byte value)
	{
		_buffer[_pos++] = value;
		if (_pos == _buffer.Length)
			Flush();
	}
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public readonly byte GetByte(int offset) => _buffer[_pos - offset - 1];
	public int Train(ReadOnlySpan<byte> trainData)
	{
		int trainSize = Math.Min(trainData.Length, _buffer.Length);
		int size = trainSize;
		int offset = trainData.Length - size;
		while (size > 0)
		{
			int bytesToRead = Math.Min(_buffer.Length - _pos, size);
			trainData.Slice(offset, bytesToRead).CopyTo(_buffer.Slice(_pos, bytesToRead));
			offset += bytesToRead;
			size -= bytesToRead;
			_pos += bytesToRead;
			_streamPos += bytesToRead;
			if (_pos == _buffer.Length)
				_streamPos = _pos = 0;
		}
		return trainSize;
	}
}