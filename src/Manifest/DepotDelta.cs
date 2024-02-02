using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace TEKSteamClient.Manifest;

/// <summary>Steam depot delta that stores all kinds of changes between two different states of a depot.</summary>
internal class DepotDelta
{
	/// <summary>Creates a new depot delta object using data from staging tree(s) created during validation or update delta computation.</summary>
	/// <param name="manifest">Target manifest referenced by delta's trees.</param>
	/// <param name="acquisitionTree">Acquisition staging tree root.</param>
	/// <param name="auxiliaryTree">Auxiliary staging tree root.</param>
	public DepotDelta(DepotManifest manifest, DirectoryEntry.AcquisitionStaging acquisitionTree, [Optional]DirectoryEntry.AuxiliaryStaging? auxiliaryTree)
	{
		int numChunks = 0;
		int numFiles = 0;
		int numDirs = 1;
		int numDownloadFiles = 0;
		long chunkBufferFileSize = 0;
		long downloadSize = 0;
		long downloadCacheSize = 0;
		void countAcq(in DirectoryEntry dir, DirectoryEntry.AcquisitionStaging acquisitionDir)
		{
			numFiles += acquisitionDir.Files.Count;
			numDirs += acquisitionDir.Subdirectories.Count;
			foreach (var acquisitionFile in acquisitionDir.Files)
			{
				numChunks += acquisitionFile.Chunks.Count;
				var file = manifest.FileBuffer[acquisitionFile.Index];
				if (acquisitionFile.Chunks.Count is 0)
				{
					numDownloadFiles++;
					downloadCacheSize += file.Size;
					foreach (var chunk in file.Chunks)
						downloadSize += chunk.CompressedSize;
				}
				else
					for (int i = 0; i < acquisitionFile.Chunks.Count; i++)
					{
						int index = acquisitionFile.Chunks[i].Index;
						var chunk = manifest.ChunkBuffer[index];
						downloadSize += chunk.CompressedSize;
						acquisitionFile.Chunks[i] = new(index) { Offset = chunkBufferFileSize };
						chunkBufferFileSize += chunk.UncompressedSize;
					}
			}
			foreach (var subdir in acquisitionDir.Subdirectories)
				countAcq(in manifest.DirectoryBuffer[subdir.Index], subdir);
		}
		countAcq(in manifest.Root, acquisitionTree);
		ChunkBufferFileSize = chunkBufferFileSize;
		DownloadSize = downloadSize;
		_acqFileBuffer = new FileEntry.AcquisitionEntry[numFiles];
		_acqChunkBuffer = GC.AllocateUninitializedArray<FileEntry.AcquisitionEntry.ChunkEntry>(numChunks);
		_acqDirBuffer = new DirectoryEntry.AcquisitionEntry[numDirs];
		int chunkIndex = 0;
		int fileIndex = 0;
		int dirIndex = 1;
		void writeDirAcq(DirectoryEntry.AcquisitionStaging dir, int index)
		{
			_acqDirBuffer[index] = new()
			{
				IsNew = dir.IsNew,
				Index = dir.Index,
				Files = new(_acqFileBuffer, fileIndex, dir.Files.Count),
				Subdirectories = new(_acqDirBuffer, dirIndex, dir.Subdirectories.Count)
			};
			foreach (var file in dir.Files)
			{
				file.Chunks.CopyTo(new Span<FileEntry.AcquisitionEntry.ChunkEntry>(_acqChunkBuffer, chunkIndex, file.Chunks.Count));
				_acqFileBuffer[fileIndex++] = new()
				{
					Index = file.Index,
					Chunks = new(_acqChunkBuffer, chunkIndex, file.Chunks.Count)
				};
				chunkIndex += file.Chunks.Count;
			}
			var subdirs = dir.Subdirectories;
			int subdirStartIndex = dirIndex;
			dirIndex += subdirs.Count;
			for (int i = 0; i < subdirs.Count; i++)
				writeDirAcq(subdirs[i], subdirStartIndex + i);
		}
		writeDirAcq(acquisitionTree, 0);
		if (auxiliaryTree is null)
		{
			NumDownloadFiles = numDownloadFiles + (chunkBufferFileSize > 0 ? 1 : 0);
			DownloadCacheSize = downloadCacheSize + chunkBufferFileSize;
			_auxDirBuffer = [ new() { Index = 1 } ];
			return;
		}
		int numRemovals = 0;
		int numRemovalsWithDirs = 0;
		numFiles = 0;
		int numTransferOperations = 0;
		numDirs = 1;
		int maxRelocSize = 0x200000;
		long intermediateFileMaxSize = 0;
		long numFileTransferOperations = 0;
		void countAux(DirectoryEntry.AuxiliaryStaging dir)
		{
			if (dir.FilesToRemove is not null)
			{
				numRemovals += dir.FilesToRemove.Count;
				numRemovalsWithDirs += dir.FilesToRemove.Count;
				if (dir.FilesToRemove.Count is 0)
					numRemovalsWithDirs++;
			}
			numFiles += dir.Files.Count;
			numDirs += dir.Subdirectories.Count;
			foreach (var file in dir.Files)
			{
				numTransferOperations += file.TransferOperations.Count;
				numFileTransferOperations += file.TransferOperations.Count;
				long intermediateFileSize = 0;
				foreach (var transferOperation in file.TransferOperations)
				{
					if (transferOperation is FileEntry.AuxiliaryStaging.ChunkPatchEntry chunkPatch)
					{
						if (chunkPatch.UseIntermediateFile)
						{
							intermediateFileSize += chunkPatch.Size;
							numFileTransferOperations++;
						}
					}
					else if (transferOperation is FileEntry.AuxiliaryStaging.RelocationEntry reloc)
					{
						if (reloc.Size > maxRelocSize)
							maxRelocSize = (int)reloc.Size;
						if (reloc.UseIntermediateFile)
						{
							intermediateFileSize += reloc.Size;
							numFileTransferOperations++;
						}
					}
				}
				if (intermediateFileSize > intermediateFileMaxSize)
					intermediateFileMaxSize = intermediateFileSize;
			}
			foreach (var subdir in dir.Subdirectories)
				countAux(subdir);
		}
		countAux(auxiliaryTree);
		MaxTransferBufferSize = maxRelocSize;
		NumDownloadFiles = numDownloadFiles + (chunkBufferFileSize > 0 ? 1 : 0) + (intermediateFileMaxSize > 0 ? 1 : 0);
		DownloadCacheSize = downloadCacheSize + chunkBufferFileSize + intermediateFileMaxSize;
		NumRemovals = numRemovalsWithDirs;
		IntermediateFileSize = intermediateFileMaxSize;
		NumTransferOperations = numFileTransferOperations;
		_auxRemovalBuffer = GC.AllocateUninitializedArray<int>(numRemovals);
		_auxFileBuffer = new FileEntry.AuxiliaryEntry[numFiles];
		_auxTransferOperationBuffer = GC.AllocateUninitializedArray<FileEntry.AuxiliaryEntry.ITransferOperation>(numTransferOperations);
		_auxDirBuffer = new DirectoryEntry.AuxiliaryEntry[numDirs];
		int removalIndex = 0;
		fileIndex = 0;
		int transferOperationIndex = 0;
		dirIndex = 1;
		void writeDirAux(DirectoryEntry.AuxiliaryStaging dir, int index)
		{
			var filesToRemove = dir.FilesToRemove;
			_auxDirBuffer[index] = new()
			{
				Index = dir.Index,
				FilesToRemove = filesToRemove is null ? null : new(_auxRemovalBuffer, removalIndex, filesToRemove.Count),
				Files = new(_auxFileBuffer, fileIndex, dir.Files.Count),
				Subdirectories = new(_auxDirBuffer, dirIndex, dir.Subdirectories.Count)
			};
			if (filesToRemove is not null)
			{
				filesToRemove.CopyTo(new Span<int>(_auxRemovalBuffer, removalIndex, filesToRemove.Count));
				removalIndex += filesToRemove.Count;
			}
			foreach (var file in dir.Files)
			{
				_auxFileBuffer[fileIndex++] = new()
				{
					Index = file.Index,
					TransferOperations = new(_auxTransferOperationBuffer, transferOperationIndex, file.TransferOperations.Count),
				};
				long intermediateFileOffset = 0;
				foreach (var transferOperation in file.TransferOperations)
				{
					if (transferOperation is FileEntry.AuxiliaryStaging.ChunkPatchEntry chunkPatch)
					{
						_auxTransferOperationBuffer[transferOperationIndex++] = new FileEntry.AuxiliaryEntry.ChunkPatchEntry()
						{
							ChunkIndex = chunkPatch.ChunkIndex,
							PatchChunkIndex = chunkPatch.PatchChunkIndex,
							IntermediateFileOffset = chunkPatch.UseIntermediateFile ? intermediateFileOffset : -1
						};
						if (chunkPatch.UseIntermediateFile)
							intermediateFileOffset += chunkPatch.Size;
					}
					else if (transferOperation is FileEntry.AuxiliaryStaging.RelocationEntry reloc)
					{
						_auxTransferOperationBuffer[transferOperationIndex++] = new FileEntry.AuxiliaryEntry.RelocationEntry()
						{
							SourceOffset = reloc.SourceOffset,
							TargetOffset = reloc.TargetOffset,
							IntermediateFileOffset = reloc.UseIntermediateFile ? intermediateFileOffset : -1,
							Size = (int)reloc.Size
						};
						if (reloc.UseIntermediateFile)
							intermediateFileOffset += reloc.Size;
					}
				}
			}
			var subdirs = dir.Subdirectories;
			int subdirStartIndex = dirIndex;
			dirIndex += subdirs.Count;
			for (int i = 0; i < subdirs.Count; i++)
				writeDirAux(subdirs[i], subdirStartIndex + i);
		}
		writeDirAux(auxiliaryTree, 0);
	}
	/// <summary>Creates a new depot delta object by reading an .scdelta file.</summary>
	/// <param name="filePath">Path to the depot delta file.</param>
	/// <exception cref="SteamException">The file is corrupted (hash mismatch).</exception>
	public DepotDelta(string filePath)
	{
		Span<byte> byteSpan;
		Span<int> buffer;
		using (var fileHandle = File.OpenHandle(filePath))
		{
			byteSpan = GC.AllocateUninitializedArray<byte>((int)RandomAccess.GetLength(fileHandle));
			buffer = MemoryMarshal.Cast<byte, int>(byteSpan);
			RandomAccess.Read(fileHandle, byteSpan, 0);
		}
		Span<byte> hash = stackalloc byte[4];
		XxHash32.Hash(byteSpan[4..], hash);
		if (buffer[0] != Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(hash)))
		{
			File.Delete(filePath);
			throw new SteamException(SteamException.ErrorType.DepotDeltaCorrupted);
		}
		ref int bufferRef = ref MemoryMarshal.GetReference(buffer);
		NumDownloadFiles = buffer[1];
		ChunkBufferFileSize = Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 8));
		DownloadSize = Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 16));
		DownloadCacheSize = Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 24));
		_acqFileBuffer = new FileEntry.AcquisitionEntry[buffer[8]];
		_acqChunkBuffer = GC.AllocateUninitializedArray<FileEntry.AcquisitionEntry.ChunkEntry>(buffer[9]);
		_acqDirBuffer = new DirectoryEntry.AcquisitionEntry[buffer[10]];
		bool includeAux;
		int offset;
		if (buffer[11] is 0)
		{
			_auxDirBuffer = [ new() { Index = 1 } ];
			includeAux = false;
			offset = 12;
		}
		else
		{
			_auxDirBuffer = new DirectoryEntry.AuxiliaryEntry[buffer[11]];
			MaxTransferBufferSize = buffer[12];
			NumRemovals = buffer[13];
			IntermediateFileSize = Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 56));
			NumTransferOperations = Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 64));
			_auxRemovalBuffer = GC.AllocateUninitializedArray<int>(buffer[18]);
			_auxFileBuffer = new FileEntry.AuxiliaryEntry[buffer[19]];
			_auxTransferOperationBuffer = GC.AllocateUninitializedArray<FileEntry.AuxiliaryEntry.ITransferOperation>(buffer[20]);
			includeAux = true;
			offset = 21;
		}
		for (int i = 0, chunkOffset = 0; i < _acqFileBuffer.Length; i++)
		{
			int numChunks = buffer[offset + 1];
			_acqFileBuffer[i] = new()
			{
				Index = buffer[offset],
				Chunks = new(_acqChunkBuffer, chunkOffset, numChunks)
			};
			chunkOffset += numChunks;
			offset += 2;
		}
		for (int i = 0; i < _acqChunkBuffer.Length; i++)
		{
			_acqChunkBuffer[i] = new(buffer[offset]) { Offset = Unsafe.ReadUnaligned<long>(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset + 1))) };
			offset += 3;
		}
		int fileOffset = 0, dirOffset = 1;
		void loadAcqDir(Span<int> buffer, int index)
		{
			int entryOffset = offset + index * 4;
			int numFiles = buffer[entryOffset + 2];
			int numSubdirs = buffer[entryOffset + 3];
			_acqDirBuffer[index] = new()
			{
				IsNew = buffer[entryOffset] is not 0,
				Index = buffer[entryOffset + 1],
				Files = new(_acqFileBuffer, fileOffset, numFiles),
				Subdirectories = new(_acqDirBuffer, dirOffset, numSubdirs)
			};
			fileOffset += numFiles;
			int subdirStartIndex = dirOffset;
			dirOffset += numSubdirs;
			for (int i = 0; i < numSubdirs; i++)
				loadAcqDir(buffer, subdirStartIndex + i);
		}
		loadAcqDir(buffer, 0);
		offset += _acqDirBuffer.Length * 4;
		if (includeAux)
		{
			Unsafe.CopyBlockUnaligned(
				ref Unsafe.As<int, byte>(ref MemoryMarshal.GetArrayDataReference(_auxRemovalBuffer!)),
				ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset)),
				(uint)_auxRemovalBuffer!.Length * sizeof(int));
			offset += _auxRemovalBuffer.Length;
			for (int i = 0, transferOperationOffset = 0; i < _auxFileBuffer!.Length; i++)
			{
				int numTransferOperations = buffer[offset + 1];
				_auxFileBuffer[i] = new()
				{
					Index = buffer[offset],
					TransferOperations = new(_auxTransferOperationBuffer!, transferOperationOffset, numTransferOperations),
				};
				transferOperationOffset += numTransferOperations;
				offset += 2;
			}
			for (int i = 0; i < _auxTransferOperationBuffer!.Length; i++)
			{
				if (buffer[offset++] is 0)
				{
					_auxTransferOperationBuffer[i] = new FileEntry.AuxiliaryEntry.ChunkPatchEntry()
					{
						ChunkIndex = buffer[offset],
						PatchChunkIndex = buffer[offset + 1],
						IntermediateFileOffset = Unsafe.ReadUnaligned<long>(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset + 2)))
					};
					offset += 4;
				}
				else
				{
					_auxTransferOperationBuffer[i] = new FileEntry.AuxiliaryEntry.RelocationEntry()
					{
						SourceOffset = Unsafe.ReadUnaligned<long>(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset))),
						TargetOffset = Unsafe.ReadUnaligned<long>(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset + 2))),
						IntermediateFileOffset = Unsafe.ReadUnaligned<long>(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset + 4))),
						Size = buffer[offset + 6]
					};
					offset += 7;
				}
			}
			int removalOffset = 0;
			fileOffset = 0;
			dirOffset = 1;
			void loadAuxDir(Span<int> buffer, int index)
			{
				int entryOffset = offset + index * 4;
				int numRemovals = buffer[entryOffset + 1];
				int numFiles = buffer[entryOffset + 2];
				int numSubdirs = buffer[entryOffset + 3];
				_auxDirBuffer[index] = new()
				{
					Index = buffer[entryOffset],
					FilesToRemove = numRemovals >= 0 ? new(_auxRemovalBuffer, removalOffset, numRemovals) : null,
					Files = new(_auxFileBuffer, fileOffset, numFiles),
					Subdirectories = new(_auxDirBuffer, dirOffset, numSubdirs)
				};
				if (numRemovals >= 0)
					removalOffset += numRemovals;
				fileOffset += numFiles;
				int subdirStartIndex = dirOffset;
				dirOffset += numSubdirs;
				for (int i = 0; i < numSubdirs; i++)
					loadAuxDir(buffer, subdirStartIndex + i);
			}
			loadAuxDir(buffer, 0);
		}
	}
	/// <summary>Buffer storing indexes of files to remove for <see cref="AuxiliaryTree"/>.</summary>
	private readonly int[]? _auxRemovalBuffer;
	/// <summary>Buffer storing file entries for <see cref="AcquisitionTree"/>.</summary>
	private readonly FileEntry.AcquisitionEntry[] _acqFileBuffer;
	/// <summary>Buffer storing chunk entries for <see cref="AcquisitionTree"/>.</summary>
	private readonly FileEntry.AcquisitionEntry.ChunkEntry[] _acqChunkBuffer;
	/// <summary>Buffer storing file entries for <see cref="AuxiliaryTree"/>.</summary>
	private readonly FileEntry.AuxiliaryEntry[]? _auxFileBuffer;
	/// <summary>Buffer storing transfer operation entries for <see cref="AuxiliaryTree"/>.</summary>
	private readonly FileEntry.AuxiliaryEntry.ITransferOperation[]? _auxTransferOperationBuffer;
	/// <summary>Buffer storing directory entries for <see cref="AcquisitionTree"/>.</summary>
	private readonly DirectoryEntry.AcquisitionEntry[] _acqDirBuffer;
	/// <summary>Buffer storing directory entries for <see cref="AuxiliaryTree"/>.</summary>
	private readonly DirectoryEntry.AuxiliaryEntry[] _auxDirBuffer;
	/// <summary>Maximum size of a transfer operation data region.</summary>
	public int MaxTransferBufferSize { get; }
	/// <summary>Number of files that are created during preallocation.</summary>
	public int NumDownloadFiles { get; }
	/// <summary>Number of files and directories that are removed.</summary>
	public int NumRemovals { get; }
	/// <summary>Total size of chunks in files that are not downloaded entirely and hence are stored in a chunk buffer file.</summary>
	public long ChunkBufferFileSize { get; }
	/// <summary>Total amount of data that must be downloaded from CDN servers.</summary>
	public long DownloadSize { get; }
	/// <summary>Total amount of disk space occupied by files that must be downloaded.</summary>
	public long DownloadCacheSize { get; }
	/// <summary>Max size of patching/relocation intermediate file.</summary>
	public long IntermediateFileSize { get; }
	/// <summary>Total number of file read/write operation pairs during patching and chunk relocation.</summary>
	public long NumTransferOperations { get; }
	/// <summary>Tree of items that must be acquired.</summary>
	public ref readonly DirectoryEntry.AcquisitionEntry AcquisitionTree => ref _acqDirBuffer[0];
	/// <summary>Auxiliary item tree.</summary>
	public ref readonly DirectoryEntry.AuxiliaryEntry AuxiliaryTree => ref _auxDirBuffer[0];
	/// <summary>Writes depot delta data to an .scdelta file.</summary>
	/// <param name="filePath">Path to the file that will be created.</param>
	public void WriteToFile(string filePath)
	{
		bool includeAux = _auxDirBuffer[0].Index is 0;
		int headerSize = includeAux ? 21 : 12;
		int totalSize = headerSize + _acqFileBuffer.Length * 2 + _acqChunkBuffer.Length * 3 + _acqDirBuffer.Length * 4;
		if (includeAux)
		{
			totalSize += _auxRemovalBuffer!.Length + _auxFileBuffer!.Length * 2 + _auxDirBuffer!.Length * 4;
			foreach (var transferOperation in _auxTransferOperationBuffer!)
				totalSize += transferOperation is FileEntry.AuxiliaryEntry.ChunkPatchEntry ? 5 : 8;
		}
		Span<int> buffer = GC.AllocateUninitializedArray<int>(totalSize);
		ref int bufferRef = ref MemoryMarshal.GetReference(buffer);
		buffer[1] = NumDownloadFiles;
		Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 8)) = ChunkBufferFileSize;
		Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 16)) = DownloadSize;
		Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 24)) = DownloadCacheSize;
		buffer[8] = _acqFileBuffer.Length;
		buffer[9] = _acqChunkBuffer.Length;
		buffer[10] = _acqDirBuffer.Length;
		buffer[11] = includeAux ? _auxDirBuffer.Length : 0;
		if (includeAux)
		{
			buffer[12] = MaxTransferBufferSize;
			buffer[13] = NumRemovals;
			Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 56)) = IntermediateFileSize;
			Unsafe.As<int, long>(ref Unsafe.AddByteOffset(ref bufferRef, 64)) = NumTransferOperations;
			buffer[18] = _auxRemovalBuffer!.Length;
			buffer[19] = _auxFileBuffer!.Length;
			buffer[20] = _auxTransferOperationBuffer!.Length;
		}
		int offset = headerSize;
		foreach (var entry in _acqFileBuffer)
		{
			buffer[offset++] = entry.Index;
			buffer[offset++] = entry.Chunks.Count;
		}
		foreach (var entry in _acqChunkBuffer)
		{
			buffer[offset++] = entry.Index;
			Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset)), entry.Offset);
			offset += 2;
		}
		foreach (var entry in _acqDirBuffer)
		{
			buffer[offset++] = entry.IsNew ? 1 : 0;
			buffer[offset++] = entry.Index;
			buffer[offset++] = entry.Files.Count;
			buffer[offset++] = entry.Subdirectories.Count;
		}
		if (includeAux)
		{
			Unsafe.CopyBlockUnaligned(
				ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset)),
				ref Unsafe.As<int, byte>(ref MemoryMarshal.GetArrayDataReference(_auxRemovalBuffer!)),
				(uint)_auxRemovalBuffer!.Length * sizeof(int));
			offset += _auxRemovalBuffer.Length;
			foreach (var entry in _auxFileBuffer!)
			{
				buffer[offset++] = entry.Index;
				buffer[offset++] = entry.TransferOperations.Count;
			}
			foreach (var transferOperation in _auxTransferOperationBuffer!)
			{
				if (transferOperation is FileEntry.AuxiliaryEntry.ChunkPatchEntry chunkPatch)
				{
					buffer[offset++] = 0;
					buffer[offset++] = chunkPatch.ChunkIndex;
					buffer[offset++] = chunkPatch.PatchChunkIndex;
					Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset)), chunkPatch.IntermediateFileOffset);
					offset += 2;
				}
				else if (transferOperation is FileEntry.AuxiliaryEntry.RelocationEntry reloc)
				{
					buffer[offset++] = 1;
					Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset)), reloc.SourceOffset);
					offset += 2;
					Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset)), reloc.TargetOffset);
					offset += 2;
					Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref bufferRef, offset)), reloc.IntermediateFileOffset);
					offset += 2;
					buffer[offset++] = reloc.Size;
				}
			}
			foreach (var entry in _auxDirBuffer)
			{
				buffer[offset++] = entry.Index;
				buffer[offset++] = entry.FilesToRemove?.Count ?? -1;
				buffer[offset++] = entry.Files.Count;
				buffer[offset++] = entry.Subdirectories.Count;
			}
		}
		Span<byte> byteSpan = MemoryMarshal.AsBytes(buffer);
		XxHash32.Hash(byteSpan[4..], byteSpan);
		using var fileHandle = File.OpenHandle(filePath, FileMode.Create, FileAccess.Write, preallocationSize: byteSpan.Length);
		RandomAccess.Write(fileHandle, byteSpan, 0);
	}
}