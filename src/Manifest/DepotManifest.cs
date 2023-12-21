using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static TEKSteamClient.Manifest.Payload.Types.File.Types;

namespace TEKSteamClient.Manifest;

/// <summary>Steam depot manifest.</summary>
public class DepotManifest
{
	/// <summary>Creates a new depot manifest object from its compressed and protobuf-serialized data.</summary>
	/// <param name="compressedData">Buffer containing compressed manifest data.</param>
	/// <param name="item">Identifier of the item that the manifest belongs to.</param>
	/// <exception cref="SteamException">An error has occurred while making the manifest.</exception>
	internal DepotManifest(byte[] compressedData, ItemIdentifier item)
	{
		Item = item;
		byte[] decompressedData;
		//Unzip the data
		using (var zipArchive = new ZipArchive(new MemoryStream(compressedData)))
		{
			var entry = zipArchive.Entries[0];
			if (entry.Length < 8)
				throw new SteamException(SteamException.ErrorType.ManifestCorrupted, 0);
			decompressedData = GC.AllocateUninitializedArray<byte>((int)entry.Length);
			using var entryStream = entry.Open();
			entryStream.ReadExactly(decompressedData);
		}
		//Verify signatures and read proto messages
		ref byte dataRef = ref MemoryMarshal.GetArrayDataReference(decompressedData);
		if (Unsafe.As<byte, uint>(ref dataRef) is not 0x71F617D0)
			throw new SteamException(SteamException.ErrorType.ManifestCorrupted, 1);
		int payloadSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref dataRef, 4));
		int metadataBlockOffset = 8 + payloadSize;
		if (Unsafe.As<byte, uint>(ref Unsafe.AddByteOffset(ref dataRef, metadataBlockOffset)) is not 0x1F4812BE)
			throw new SteamException(SteamException.ErrorType.ManifestCorrupted, 2);
		var metadata = Metadata.Parser.ParseFrom(new ReadOnlySpan<byte>(decompressedData, metadataBlockOffset + 8, Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref dataRef, metadataBlockOffset + 4))));
		Id = metadata.Id;
		DataSize = (long)metadata.UncompressedSize;
		var files = new List<Payload.Types.File>(Payload.Parser.ParseFrom(new ReadOnlySpan<byte>(decompressedData, 8, payloadSize)).Files);
		//Decrypt filenames if needed
		if (metadata.FilenamesEncrypted)
		{
			if (!CDNClient.DepotDecryptionKeys.TryGetValue(item.DepotId, out var decryptionKey))
				throw new SteamException(SteamException.ErrorType.DepotDecryptionKeyMissing);
			using var aes = Aes.Create();
			aes.Key = decryptionKey;
			Span<byte> encryptedName = stackalloc byte[512];
			Span<byte> decryptedName = stackalloc byte[512];
			Span<byte> iv = stackalloc byte[16];
			foreach (var file in files)
			{
				if (!Convert.TryFromBase64String(file.Name, encryptedName, out int bytesDecoded))
					throw new SteamException(SteamException.ErrorType.ManifestCorrupted, 3);
				aes.DecryptEcb(encryptedName[..16], iv, PaddingMode.None);
				bytesDecoded = aes.DecryptCbc(encryptedName[16..bytesDecoded], iv, decryptedName);
				file.Name = Encoding.UTF8.GetString(decryptedName[..--bytesDecoded]).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
			}
		}
		else
			foreach (var file in files)
				file.Name = file.Name.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
		//Create and populate fields with data from payload
		files.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
		int numChunks = 0;
		int numDirs = 1;
		int numFiles = 0;
		foreach (var file in files)
		{
			_nameBufferSize += Encoding.UTF8.GetByteCount(Path.GetFileName(file.Name));
			if ((file.Flags & FileFlag.Directory) is not 0)
				numDirs++;
			else
			{
				numChunks += file.Chunks.Count;
				numFiles++;
			}
		}
		ChunkBuffer = GC.AllocateUninitializedArray<ChunkEntry>(numChunks);
		DirectoryBuffer = new DirectoryEntry[numDirs];
		FileBuffer = new FileEntry[numFiles];
		int chunkOffset = 0;
		int fileOffset = 0;
		int dirOffset = 1;
		void loadDirectory(int index, int payloadEntryIndex)
		{
			string path;
			int dirNumSepChars;
			if (payloadEntryIndex < 0)
			{
				path = string.Empty;
				dirNumSepChars = -1;
			}
			else
			{
				path = files[payloadEntryIndex].Name;
				dirNumSepChars = path.AsSpan().Count(Path.DirectorySeparatorChar);
			}
			int startIndex = payloadEntryIndex + 1;
			int fileStartOffset = fileOffset;
			int subdirStartOffset = dirOffset;
			int entryIndex = payloadEntryIndex;
			while (++entryIndex < files.Count)
			{
				var file = files[entryIndex];
				if (!file.Name.StartsWith(path, StringComparison.Ordinal))
					break;
				int numSepChars = file.Name.AsSpan().Count(Path.DirectorySeparatorChar);
				if (numSepChars - dirNumSepChars is not 1 || numSepChars > 0 && file.Name[path.Length] != Path.DirectorySeparatorChar)
					continue;
				if ((file.Flags & FileFlag.Directory) is not 0)
				{
					dirOffset++;
					continue;
				}
				int chunkStartOffset = chunkOffset;
				int numChunks = file.Chunks.Count;
				foreach (var chunk in file.Chunks)
				    ChunkBuffer[chunkOffset++] = new()
				    {
				        Gid = new(chunk.Gid.Span),
				        Offset = chunk.Offset,
				        CompressedSize = chunk.CompressedSize,
				        UncompressedSize = chunk.UncompressedSize,
						Checksum = chunk.Checksum
				    };
				new Span<ChunkEntry>(ChunkBuffer, chunkStartOffset, numChunks).Sort();
				FileBuffer[fileOffset++] = new()
				{
				    Name = numSepChars > 0 ? Path.GetFileName(file.Name) : file.Name,
					Size = file.Size,
				    Chunks = new(ChunkBuffer, chunkStartOffset, numChunks)
				};
			}
			int numFiles = fileOffset - fileStartOffset;
			int numSubdirs = dirOffset - subdirStartOffset;
			int endIndex = entryIndex;
			int j = subdirStartOffset;
			for (int i = startIndex; i < endIndex; i++)
			{
				var file = files[i];
				if ((file.Flags & FileFlag.Directory) is not 0)
				{
					int numSepChars = file.Name.AsSpan().Count(Path.DirectorySeparatorChar);
					if (numSepChars - dirNumSepChars is 1 && (numSepChars < 1 || file.Name[path.Length] == Path.DirectorySeparatorChar))
						loadDirectory(j++, i);
				}
			}
			DirectoryBuffer[index] = new()
			{
				Name = dirNumSepChars > 0 ? Path.GetFileName(path) : path,
				Files = new(FileBuffer, fileStartOffset, numFiles),
				Subdirectories = new(DirectoryBuffer, subdirStartOffset, numSubdirs)
			};
		}
		loadDirectory(0, -1);
		files.Clear();
	}
	/// <summary>Creates a new depot manifest object by reading an .scmanifest file.</summary>
	/// <param name="filePath">Path to the manifest file.</param>
	/// <param name="item">Identifier of the item that the manifest belongs to.</param>
	/// <param name="id">ID of the manifest to read.</param>
	/// <exception cref="SteamException">An error has occurred while reading the manifest.</exception>
	public DepotManifest(string filePath, ItemIdentifier item, ulong id)
	{
		Item = item;
		Id = id;
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
			throw new SteamException(SteamException.ErrorType.ManifestCorrupted);
		}
		ChunkBuffer = GC.AllocateUninitializedArray<ChunkEntry>(Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 4)));
		FileBuffer = new FileEntry[Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 8))];
		DirectoryBuffer = new DirectoryEntry[Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 12))];
		DataSize = Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, 16));
		int offset = 24;
		int entriesSize = FileBuffer.Length * 20 + DirectoryBuffer.Length * 16;
		int nameOffset = 24 + ChunkBuffer.Length * 40 + entriesSize;
		_nameBufferSize = buffer.Length - nameOffset;
		ref ChunkEntry chunkBufferRef = ref MemoryMarshal.GetArrayDataReference(ChunkBuffer);
		for (int i = 0; i < ChunkBuffer.Length; i++)
		{
			ref byte entryRef = ref Unsafe.As<ChunkEntry, byte>(ref Unsafe.Add(ref chunkBufferRef, i));
			Unsafe.CopyBlock(ref entryRef, ref Unsafe.AddByteOffset(ref bufferRef, offset), 20);
			offset += 20;
			Unsafe.CopyBlockUnaligned(ref Unsafe.AddByteOffset(ref entryRef, 32), ref Unsafe.AddByteOffset(ref bufferRef, offset), 20);
			offset += 20;
		}
		Span<int> entriesSpan = MemoryMarshal.Cast<byte, int>(buffer.Slice(offset, entriesSize));
		ref int spanRef = ref MemoryMarshal.GetReference(entriesSpan);
		offset = 0;
		for (int i = 0, chunkOffset = 0; i < FileBuffer.Length; i++)
		{
			int nameLength = entriesSpan[offset + 2];
			int numChunks = entriesSpan[offset + 3];
			FileBuffer[i] = new()
			{
				Name = Encoding.UTF8.GetString(buffer.Slice(nameOffset, nameLength)),
				Size = Unsafe.As<int, long>(ref Unsafe.Add(ref spanRef, offset)),
				Chunks = new(ChunkBuffer, chunkOffset, numChunks)
			};
			offset += 4;
			nameOffset += nameLength;
			chunkOffset += numChunks;
		}
		string[] dirNames = new string[DirectoryBuffer.Length];
		for (int i = 0; i < dirNames.Length; i++)
		{
			int nameLength = entriesSpan[offset + i * 3];
			dirNames[i] = Encoding.UTF8.GetString(buffer.Slice(nameOffset, nameLength));
			nameOffset += nameLength;
		}
		int fileOffset = 0, directoryOffset = 1;
		void loadDir(Span<byte> buffer, Span<int> entriesSpan, int index)
		{
			int entryOffset = offset + index * 3 + 1;
			int numFiles = entriesSpan[entryOffset++];
			int numSubdirs = entriesSpan[entryOffset];
			DirectoryBuffer[index] = new()
			{
				Name = dirNames[index],
				Files = new(FileBuffer, fileOffset, numFiles),
				Subdirectories = new(DirectoryBuffer, directoryOffset, numSubdirs)
			};
			fileOffset += numFiles;
			int subdirStartIndex = directoryOffset;
			directoryOffset += numSubdirs;
			for (int i = 0; i < numSubdirs; i++)
				loadDir(buffer, entriesSpan, subdirStartIndex + i);
		}
		loadDir(buffer, entriesSpan, 0);
	}
	/// <summary>Total size of all filenames in UTF-8 encoding.</summary>
	private readonly int _nameBufferSize;
	/// <summary>Buffer containing all chunk entries of the manifest.</summary>
	internal readonly ChunkEntry[] ChunkBuffer;
	/// <summary>Buffer containing all file entries of the manifest.</summary>
	internal readonly FileEntry[] FileBuffer;
	/// <summary>Buffer containing all directory entries of the manifest.</summary>
	internal readonly DirectoryEntry[] DirectoryBuffer;
	/// <summary>Total size of all files listed in the manifest.</summary>
	public long DataSize { get; }
	/// <summary>ID of the manifest.</summary>
	public ulong Id { get; }
	/// <summary>Root directory entry of the manifest.</summary>
	public ref readonly DirectoryEntry Root => ref DirectoryBuffer[0];
	/// <summary>Identifier of the item that the manifest belongs to.</summary>
	public ItemIdentifier Item { get; }
	/// <summary>Writes manifest data to an .scmanifest file.</summary>
	/// <param name="filePath">Path to the file that will be created.</param>
	public void WriteToFile(string filePath)
	{
		int entriesSize = FileBuffer.Length * 16 + DirectoryBuffer.Length * 12;
		int nameOffset = 24 + ChunkBuffer.Length * 40 + entriesSize;
		Span<byte> buffer = GC.AllocateUninitializedArray<byte>(nameOffset + _nameBufferSize);
		ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
		Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 4)) = ChunkBuffer.Length;
		Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 8)) = FileBuffer.Length;
		Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 12)) = DirectoryBuffer.Length;
		Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, 16)) = DataSize;
		int offset = 24;
		foreach (var chunk in ChunkBuffer)
		{
			ref byte chunkRef = ref Unsafe.As<ChunkEntry, byte>(ref Unsafe.AsRef(in chunk));
			Unsafe.CopyBlock(ref Unsafe.AddByteOffset(ref bufferRef, offset), ref chunkRef, 20);
			offset += 20;
			Unsafe.CopyBlockUnaligned(ref Unsafe.AddByteOffset(ref bufferRef, offset), ref Unsafe.AddByteOffset(ref chunkRef, 32), 20);
			offset += 20;
		}
		Span<int> entriesSpan = MemoryMarshal.Cast<byte, int>(buffer.Slice(offset, entriesSize));
		ref int spanRef = ref MemoryMarshal.GetReference(entriesSpan);
		offset = 0;
		foreach (var file in FileBuffer)
		{
			int nameLength = Encoding.UTF8.GetBytes(file.Name, buffer[nameOffset..]);
			nameOffset += nameLength;
			Unsafe.As<int, long>(ref Unsafe.Add(ref spanRef, offset)) = file.Size;
			offset += 2;
			entriesSpan[offset++] = nameLength;
			entriesSpan[offset++] = file.Chunks.Count;
		}
		foreach (var dir in DirectoryBuffer)
		{
			int nameLength = Encoding.UTF8.GetBytes(dir.Name, buffer[nameOffset..]);
			nameOffset += nameLength;
			entriesSpan[offset++] = nameLength;
			entriesSpan[offset++] = dir.Files.Count;
			entriesSpan[offset++] = dir.Subdirectories.Count;
		}
		XxHash32.Hash(buffer[4..], buffer);
		using var fileHandle = File.OpenHandle(filePath, FileMode.Create, FileAccess.Write, preallocationSize: buffer.Length);
		RandomAccess.Write(fileHandle, buffer, 0);
	}
}