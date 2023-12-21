using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Google.Protobuf;

namespace TEKSteamClient.CM.Messages;

/// <summary>Steam CM protobuf-serialized message with <typeparamref name="TBody"/> as its body.</summary>
/// <param name="type">Type of the message.</param>
internal class Message<TBody>(MessageType type) where TBody : IMessage<TBody>, new()
{
	/// <summary>Type of the message.</summary>
	private readonly MessageType _type = type;
	/// <summary>Header of the message.</summary>
	public MessageHeader? Header { get; set; }
	/// <summary>Body of the message.</summary>
	public TBody Body { get; } = new TBody();
	/// <summary>Deserializes message body from the given byte span.</summary>
	/// <param name="data">The span containing serizalized message data.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void DeserializeBody(ReadOnlySpan<byte> data) => Body.MergeFrom(data);
	/// <summary>Serializes the message into a buffer.</summary>
	/// <param name="buffer">The buffer to write message data to. If it's too small, a new buffer of sufficient size will be allocated.</param>
	/// <returns>Size of serialized message data in bytes.</returns>
	public int Serialize(ref byte[] buffer)
	{
		int headerSize = Header!.CalculateSize();
		int bodySize = Body.CalculateSize();
		int messageSize = 8 + headerSize + bodySize;
		if (buffer.Length < messageSize)
		{
			int newSize = 0x400;
			while (newSize < messageSize)
				newSize *= 2;
			buffer = GC.AllocateUninitializedArray<byte>(newSize);
		}
		ref byte bufferRef = ref MemoryMarshal.GetArrayDataReference(buffer);
		Unsafe.As<byte, uint>(ref bufferRef) = (uint)_type | 0x80000000;
		Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 4)) = headerSize;
		Header.WriteTo(new Span<byte>(buffer, 8, headerSize));
		Body.WriteTo(new Span<byte>(buffer, 8 + headerSize, bodySize));
		return messageSize;
	}
}