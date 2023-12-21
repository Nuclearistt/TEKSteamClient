using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Google.Protobuf;
using TEKSteamClient.Utils;

namespace TEKSteamClient.Manifest;

/// <summary>Steam depot patch.</summary>
public class DepotPatch
{
	/// <summary>Creates a new depot patch object from its encrypted and protobuf-serialized data.</summary>
	/// <param name="encryptedData">Buffer containing encrypted patch data.</param>
	/// <param name="item">Identifier of the item that the patch belongs to.</param>
	/// <param name="sourceManifest">Source manifest that describes data before patching.</param>
	/// <param name="targetManifest">Target manifest that describes data after patching.</param>
	/// <exception cref="SteamException">An error has occurred while making the manifest.</exception>
	internal DepotPatch(byte[] encryptedData, ItemIdentifier item, DepotManifest sourceManifest, DepotManifest targetManifest)
	{
		Item = item;
		SourceManifestId = sourceManifest.Id;
		TargetManifestId = targetManifest.Id;
		byte[] decryptedData = GC.AllocateUninitializedArray<byte>(encryptedData.Length - 16);
		int decryptedDataSize;
		//Decrypt the data
		using (var aes = Aes.Create())
		{
			if (!CDNClient.DepotDecryptionKeys.TryGetValue(item.DepotId, out var decryptionKey))
				throw new SteamException(SteamException.ErrorType.DepotDecryptionKeyMissing);
			aes.Key = decryptionKey;
			Span<byte> iv = stackalloc byte[16];
			aes.DecryptEcb(new ReadOnlySpan<byte>(encryptedData, 0, 16), iv, PaddingMode.None);
			decryptedDataSize = aes.DecryptCbc(new ReadOnlySpan<byte>(encryptedData, 16, encryptedData.Length - 16), iv, decryptedData);
		}
		//Verify signature and read proto message
		ref byte dataRef = ref MemoryMarshal.GetArrayDataReference(decryptedData);
		if (decryptedData.Length < 8 || Unsafe.As<byte, uint>(ref dataRef) is not 0x502F15E5)
			throw new SteamException(SteamException.ErrorType.PatchCorrupted);
		int protoSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref dataRef, 4));
		var patch = Patch.Parser.ParseFrom(new ReadOnlySpan<byte>(decryptedData, 8, protoSize));
		//Create and populate buffers with data from proto patch
		int dataSize;
		if (patch.DataAfterProto)
		{
			int dataOffset = 12 + protoSize;
			dataSize = decryptedDataSize - dataOffset;
			var data = new ReadOnlySpan<byte>(decryptedData);
			foreach (var chunk in patch.Chunks)
			{
				chunk.Data = ByteString.CopyFrom(data.Slice(dataOffset, chunk.DataSize));
				dataOffset += chunk.DataSize;
			}
		}
		else
		{
			dataSize = 0;
			foreach (var chunk in patch.Chunks)
				dataSize += chunk.DataSize;
		}
		_dataBuffer = GC.AllocateUninitializedArray<byte>(dataSize);
		Chunks = new PatchChunkEntry[patch.Chunks.Count];
		var sourceManifestChunks = GC.AllocateUninitializedArray<GidAndIndex>(sourceManifest.ChunkBuffer.Length);
		for (int i = 0; i < sourceManifestChunks.Length; i++)
			sourceManifestChunks[i] = new()
			{
				Gid = sourceManifest.ChunkBuffer[i].Gid,
				Index = i
			};
		var targetManifestChunks = GC.AllocateUninitializedArray<GidAndIndex>(targetManifest.ChunkBuffer.Length);
		for (int i = 0; i < targetManifestChunks.Length; i++)
			targetManifestChunks[i] = new()
			{
				Gid = targetManifest.ChunkBuffer[i].Gid,
				Index = i
			};
		Array.Sort(sourceManifestChunks);
		Array.Sort(targetManifestChunks);
		for (int i = 0; i < Chunks.Length; i++)
		{
			var chunk = patch.Chunks[i];
			Chunks[i] = new()
			{
				SourceChunkIndex = sourceManifestChunks[Array.BinarySearch(sourceManifestChunks, new() { Gid = new SHA1Hash(chunk.SourceGid.Span), Index = 0 })].Index,
				TargetChunkIndex = targetManifestChunks[Array.BinarySearch(targetManifestChunks, new() { Gid = new SHA1Hash(chunk.TargetGid.Span), Index = 0 })].Index,
				Data = chunk.Data.Memory
			};
		}
		Array.Sort(Chunks);
		ref byte bufferRef = ref MemoryMarshal.GetArrayDataReference(_dataBuffer);
		nint offset = 0;
		foreach (var chunk in Chunks)
		{
			Unsafe.CopyBlockUnaligned(ref Unsafe.AddByteOffset(ref bufferRef, offset), ref MemoryMarshal.GetReference(chunk.Data.Span), (uint)chunk.Data.Length);
			offset += chunk.Data.Length;
		}
	}
	/// <summary>Creates a new depot patch object by reading an .scpatch file.</summary>
	/// <param name="filePath">Path to the patch file.</param>
	/// <param name="item">Identifier of the item that the patch belongs to.</param>
	/// <param name="sourceManifestId">ID of the source manifest.</param>
	/// <param name="targetManifestId">ID of the target manifest.</param>
	/// <exception cref="SteamException">An error has occurred while reading the patch.</exception>
	public DepotPatch(string filePath, ItemIdentifier item, ulong sourceManifestId, ulong targetManifestId)
	{
		Item = item;
		SourceManifestId = sourceManifestId;
		TargetManifestId = targetManifestId;
		Span<byte> buffer;
		using (var fileHandle = File.OpenHandle(filePath))
		{
			buffer = GC.AllocateUninitializedArray<byte>((int)RandomAccess.GetLength(fileHandle));
			RandomAccess.Read(fileHandle, buffer, 0);
		}
		ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
		Span<byte> hash = stackalloc byte[4];
		XxHash32.Hash(buffer[4..], hash);
		if (Unsafe.As<byte, uint>(ref bufferRef) != Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(hash)))
		{
			File.Delete(filePath);
			throw new SteamException(SteamException.ErrorType.PatchCorrupted);
		}
		Chunks = new PatchChunkEntry[Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 4))];
		_dataBuffer = GC.AllocateUninitializedArray<byte>(buffer.Length - Chunks.Length * 12 - 8);
		Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetArrayDataReference(_dataBuffer), ref Unsafe.AddByteOffset(ref bufferRef, Chunks.Length * 12 + 8), (uint)_dataBuffer.Length);
		Span<int> entriesSpan = MemoryMarshal.Cast<byte, int>(buffer.Slice(8, Chunks.Length * 12));
		for (int i = 0, offset = 0, dataOffset = 0; i < Chunks.Length; i++)
		{
			int dataSize = entriesSpan[offset + 2];
			Chunks[i] = new()
			{
				SourceChunkIndex = entriesSpan[offset],
				TargetChunkIndex = entriesSpan[offset + 1],
				Data = new(_dataBuffer, dataOffset, dataSize)
			};
			offset += 3;
			dataOffset += dataSize;
		}
	}
	/// <summary>Buffer containing all patch data.</summary>
	private readonly byte[] _dataBuffer;
	/// <summary>ID of the source manifest.</summary>
	public ulong SourceManifestId { get; }
	/// <summary>ID of the target manifest.</summary>
	public ulong TargetManifestId { get; }
	/// <summary>Identifier of the item that the patch belongs to.</summary>
	public ItemIdentifier Item { get; }
	/// <summary>Patch chunk entries stored in the patch.</summary>
	public PatchChunkEntry[] Chunks { get; }
	/// <summary>Writes patch data to an .scpatch file.</summary>
	/// <param name="filePath">Path to the file that will be created.</param>
	public void WriteToFile(string filePath)
	{
		Span<byte> buffer = GC.AllocateUninitializedArray<byte>(8 + Chunks.Length * 12 + _dataBuffer.Length);
		ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
		Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 4)) = Chunks.Length;
		nint offset = 8;
		foreach (var chunk in Chunks)
		{
			Unsafe.WriteUnaligned(ref Unsafe.AddByteOffset(ref bufferRef, offset), Unsafe.As<PatchChunkEntry, ulong>(ref Unsafe.AsRef(in chunk)));
			offset += 8;
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, offset)) = chunk.Data.Length;
			offset += 4;
		}
		Unsafe.CopyBlockUnaligned(ref Unsafe.AddByteOffset(ref bufferRef, offset), ref MemoryMarshal.GetArrayDataReference(_dataBuffer), (uint)_dataBuffer.Length);
		XxHash32.Hash(buffer[4..], buffer);
		using var fileHandle = File.OpenHandle(filePath, FileMode.Create, FileAccess.Write, preallocationSize: buffer.Length);
		RandomAccess.Write(fileHandle, buffer, 0);
	}
	/// <summary>Util struct for binary search that contains GID and index of a chunk.</summary>
	private readonly struct GidAndIndex : IComparable<GidAndIndex>
	{
		public required SHA1Hash Gid { get; init; }
		public required int Index { get; init; }
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int CompareTo(GidAndIndex other) => Gid.CompareTo(other.Gid);
	}
}