using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TEKSteamClient.CM;
using TEKSteamClient.Manifest;
using TEKSteamClient.Utils;

[module:SkipLocalsInit]

namespace TEKSteamClient;

/// <summary>Manages a Steam app installation.</summary>
public class AppManager
{
	/// <summary>Creates a new app manager for specified Steam app.</summary>
	/// <param name="appId">ID of the Steam app.</param>
	/// <param name="installationPath">Path to the root directory of app installation.</param>
	/// <param name="workshopContentPath">Path to the workshop content installation directory.</param>
	public AppManager(uint appId, string installationPath, [Optional]string? workshopContentPath)
	{
		AppId = appId;
		InstallationPath = installationPath;
		_scDataPath = Path.Combine(installationPath, "SCData");
		WorkshopContentPath = workshopContentPath ?? Path.Combine(_scDataPath, "Workshop");
		CdnClient = new()
		{
			DownloadsDirectory = Path.Combine(_scDataPath, "Downloads"),
			ManifestsDirectory = Path.Combine(_scDataPath, "Manifests"),
			CmClient = CmClient
		};
		CdnClient.ProgressInitiated += (type, totalValue, initialValue) => ProgressInitiated?.Invoke(type, totalValue, initialValue);
		CdnClient.ProgressUpdated += (newValue) => ProgressUpdated?.Invoke(newValue);
		CdnClient.StatusUpdated += (newStatus) => StatusUpdated?.Invoke(newStatus);
		if (!Directory.Exists(_scDataPath))
		{
			Directory.CreateDirectory(_scDataPath);
			File.SetAttributes(_scDataPath, (File.GetAttributes(_scDataPath) & ~FileAttributes.ReadOnly) | FileAttributes.Hidden);
			Directory.CreateDirectory(CdnClient.DownloadsDirectory);
			Directory.CreateDirectory(CdnClient.ManifestsDirectory);
		}
		else
		{
			if (!Directory.Exists(CdnClient.DownloadsDirectory))
				Directory.CreateDirectory(CdnClient.DownloadsDirectory);
			if (!Directory.Exists(CdnClient.ManifestsDirectory))
				Directory.CreateDirectory(CdnClient.ManifestsDirectory);
		}
		if (!Directory.Exists(WorkshopContentPath))
			Directory.CreateDirectory(WorkshopContentPath);
		var attributes = File.GetAttributes(_scDataPath);
		if (attributes.HasFlag(FileAttributes.ReadOnly))
			File.SetAttributes(_scDataPath, attributes & ~FileAttributes.ReadOnly);
	}
	/// <summary>Path to SCData directory.</summary>
	private readonly string _scDataPath;
	/// <summary>ID of the Steam app.</summary>
	public uint AppId { get; }
	/// <summary>Path to the root directory of app installation.</summary>
	public string InstallationPath { get; }
	/// <summary>Path to the workshop content installation directory.</summary>
	public string WorkshopContentPath { get; }
	/// <summary>Steam CDN client used to download content from CDN network.</summary>
	public CDNClient CdnClient { get; }
	/// <summary>Steam CM client used to get data from CM network.</summary>
	public CMClient CmClient { get; } = new() { EnsureLogOn = true };
	/// <summary>Performs transition of item installation to target version.</summary>
	/// <param name="state">State of the item</param>
	/// <param name="sourceManifest">The source manifest.</param>
	/// <param name="targetManifest">The target manifest.</param>
	/// <param name="patch">Patch to apply to source data.</param>
	/// <param name="delta">Delta object that lists changes.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	private void Commit(ItemState state, DepotManifest? sourceManifest, DepotManifest targetManifest, DepotPatch? patch, DepotDelta delta, CancellationToken cancellationToken)
	{
		string localPath = state.Id.WorkshopItemId is 0 ? InstallationPath : Path.Combine(WorkshopContentPath, state.Id.WorkshopItemId.ToString());
		if (state.Status <= ItemState.ItemStatus.Patching && delta.NumTransferOperations > 0)
			PatchAndRelocateChunks(state, localPath, sourceManifest!, targetManifest, patch, delta, cancellationToken);
		if (state.Status <= ItemState.ItemStatus.WritingNewData)
			WriteNewData(state, localPath, targetManifest, delta, cancellationToken);
		if (delta.NumRemovals > 0)
			RemoveOldFiles(state, localPath, sourceManifest!, targetManifest, delta, cancellationToken);
	}
	/// <summary>Performs chunk patching and relocation.</summary>
	/// <param name="state">State of the item.</param>
	/// <param name="localPath">Path to the local item installation directory.</param>
	/// <param name="sourceManifest">The source manifest.</param>
	/// <param name="targetManifest">The target manifest.</param>
	/// <param name="patch">Patch to apply to source data.</param>
	/// <param name="delta">Delta object that lists chunks to be patched and relocated.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	/// <exception cref="SteamException">Item installation is corrupted.</exception>
	private void PatchAndRelocateChunks(ItemState state, string localPath, DepotManifest sourceManifest, DepotManifest targetManifest, DepotPatch? patch, DepotDelta delta, CancellationToken cancellationToken)
	{
		byte[] buffer = GC.AllocateUninitializedArray<byte>(delta.MaxTransferBufferSize);
		SafeFileHandle? intermediateFileHandle = null;
		var decoder = new Utils.LZMA.Decoder();
		void processDir(in DirectoryEntry dir, in DirectoryEntry.AuxiliaryEntry auxiliaryDir, string path, int recursionLevel)
		{
			int index;
			if (state.ProgressIndexStack.Count > recursionLevel)
				index = state.ProgressIndexStack[recursionLevel];
			else
			{
				state.ProgressIndexStack.Add(0);
				index = 0;
			}
			for (; index < auxiliaryDir.Files.Count; index++)
			{
				var auxiliaryFile = auxiliaryDir.Files[index];
				if (auxiliaryFile.TransferOperations.Count is 0)
					continue;
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = index;
					return;
				}
				var file = dir.Files[auxiliaryFile.Index];
				string filePath = Path.Combine(path, file.Name);
				var attributes = File.GetAttributes(filePath);
				if (attributes.HasFlag(FileAttributes.ReadOnly))
					File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
				using var fileHandle = File.OpenHandle(filePath, access: FileAccess.ReadWrite, options: FileOptions.RandomAccess);
				long fileSize = RandomAccess.GetLength(fileHandle);
				int transferOpRecLevel = recursionLevel + 1;
				int transferOpIndex;
				if (state.ProgressIndexStack.Count > transferOpRecLevel)
					transferOpIndex = state.ProgressIndexStack[transferOpRecLevel];
				else
				{
					state.ProgressIndexStack.Add(0);
					transferOpIndex = 0;
				}
				var transferOperations = auxiliaryFile.TransferOperations;
				for (; transferOpIndex < transferOperations.Count; transferOpIndex++)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						state.ProgressIndexStack[transferOpRecLevel] = transferOpIndex;
						state.ProgressIndexStack[recursionLevel] = index;
						return;
					}
					var transferOperation = transferOperations[transferOpIndex];
					if (transferOperation is FileEntry.AuxiliaryEntry.ChunkPatchEntry chunkPatch)
					{
						var sourceChunk = sourceManifest.ChunkBuffer[patch!.Chunks[chunkPatch.PatchChunkIndex].SourceChunkIndex];
						var targetChunk = file.Chunks[chunkPatch.ChunkIndex];
						if (sourceChunk.Offset + sourceChunk.UncompressedSize > fileSize)
						{
							state.Status = ItemState.ItemStatus.Corrupted;
							state.ProgressIndexStack.Clear();
							state.DisplayProgress = 0;
							state.SaveToFile();
							throw new SteamException(SteamException.ErrorType.InstallationCorrupted, 0);
						}
						var span = new Span<byte>(buffer, 0, sourceChunk.UncompressedSize);
						RandomAccess.Read(fileHandle, span, sourceChunk.Offset);
						if (Adler.ComputeChecksum(span) != sourceChunk.Checksum)
						{
							state.Status = ItemState.ItemStatus.Corrupted;
							state.ProgressIndexStack.Clear();
							state.DisplayProgress = 0;
							state.SaveToFile();
							throw new SteamException(SteamException.ErrorType.InstallationCorrupted, 1);
						}
						if (chunkPatch.IntermediateFileOffset < 0)
						{
							var targetSpan = new Span<byte>(buffer, sourceChunk.UncompressedSize, targetChunk.UncompressedSize);
							if (!decoder.Decode(patch.Chunks[chunkPatch.PatchChunkIndex].Data.Span, targetSpan, span))
							{
								state.ProgressIndexStack.Clear();
								state.DisplayProgress = 0;
								state.Status = ItemState.ItemStatus.Corrupted;
								state.SaveToFile();
								throw new SteamException(SteamException.ErrorType.InstallationCorrupted, 2);
							}
							RandomAccess.Write(fileHandle, targetSpan, targetChunk.Offset);
						}
						else
						{
							if (targetChunk.UncompressedSize < sourceChunk.UncompressedSize)
							{
								var targetSpan = new Span<byte>(buffer, sourceChunk.UncompressedSize, targetChunk.UncompressedSize);
								if (!decoder.Decode(patch.Chunks[chunkPatch.PatchChunkIndex].Data.Span, targetSpan, span))
								{
									state.ProgressIndexStack.Clear();
									state.DisplayProgress = 0;
									state.Status = ItemState.ItemStatus.Corrupted;
									state.SaveToFile();
									throw new SteamException(SteamException.ErrorType.InstallationCorrupted, 2);
								}
								RandomAccess.Write(intermediateFileHandle!, targetSpan, chunkPatch.IntermediateFileOffset);
							}
							else
								RandomAccess.Write(intermediateFileHandle!, span, chunkPatch.IntermediateFileOffset);
						}
					}
					else if (transferOperation is FileEntry.AuxiliaryEntry.RelocationEntry reloc)
					{
						if (reloc.SourceOffset + reloc.Size > fileSize)
						{
							state.ProgressIndexStack.Clear();
							state.DisplayProgress = 0;
							state.Status = ItemState.ItemStatus.Corrupted;
							state.SaveToFile();
							throw new SteamException(SteamException.ErrorType.InstallationCorrupted, 0);
						}
						var span = new Span<byte>(buffer, 0, reloc.Size);
						RandomAccess.Read(fileHandle, span, reloc.SourceOffset);
						if (reloc.IntermediateFileOffset < 0)
							RandomAccess.Write(fileHandle, span, reloc.TargetOffset);
						else
							RandomAccess.Write(intermediateFileHandle!, span, reloc.IntermediateFileOffset);
					}
					ProgressUpdated?.Invoke(++state.DisplayProgress);
				}
				transferOpIndex -= transferOperations.Count;
				for (; transferOpIndex < transferOperations.Count; transferOpIndex++)
				{
					var transferOperation = transferOperations[transferOpIndex];
					if (transferOperation is FileEntry.AuxiliaryEntry.ChunkPatchEntry cp)
					{
						if (cp.IntermediateFileOffset < 0)
							continue;
					}
					else if (transferOperation is FileEntry.AuxiliaryEntry.RelocationEntry r && r.IntermediateFileOffset < 0)
						continue;
					if (cancellationToken.IsCancellationRequested)
					{
						state.ProgressIndexStack[transferOpRecLevel] = transferOperations.Count + transferOpIndex;
						state.ProgressIndexStack[recursionLevel] = index;
						return;
					}
					if (transferOperation is FileEntry.AuxiliaryEntry.ChunkPatchEntry chunkPatch)
					{
						var sourceChunk = sourceManifest.ChunkBuffer[patch!.Chunks[chunkPatch.PatchChunkIndex].SourceChunkIndex];
						var targetChunk = file.Chunks[chunkPatch.ChunkIndex];
						if (targetChunk.UncompressedSize < sourceChunk.UncompressedSize)
						{
							var span = new Span<byte>(buffer, 0, targetChunk.UncompressedSize);
							RandomAccess.Read(intermediateFileHandle!, span, chunkPatch.IntermediateFileOffset);
							RandomAccess.Write(fileHandle, span, targetChunk.Offset);
						}
						else
						{
							var span = new Span<byte>(buffer, 0, sourceChunk.UncompressedSize);
							RandomAccess.Read(intermediateFileHandle!, span, chunkPatch.IntermediateFileOffset);
							var targetSpan = new Span<byte>(buffer, sourceChunk.UncompressedSize, targetChunk.UncompressedSize);
							if (!decoder.Decode(patch.Chunks[chunkPatch.PatchChunkIndex].Data.Span, targetSpan, span))
							{
								state.ProgressIndexStack.Clear();
								state.DisplayProgress = 0;
								state.Status = ItemState.ItemStatus.Corrupted;
								state.SaveToFile();
								throw new SteamException(SteamException.ErrorType.InstallationCorrupted, 2);
							}
							RandomAccess.Write(fileHandle, targetSpan, targetChunk.Offset);
						}
					}
					else if (transferOperation is FileEntry.AuxiliaryEntry.RelocationEntry reloc)
					{
						var span = new Span<byte>(buffer, 0, reloc.Size);
						RandomAccess.Read(intermediateFileHandle!, span, reloc.IntermediateFileOffset);
						RandomAccess.Write(fileHandle, span, reloc.TargetOffset);
					}
					ProgressUpdated?.Invoke(++state.DisplayProgress);
				}
				state.ProgressIndexStack.RemoveAt(transferOpRecLevel);
			}
			index -= auxiliaryDir.Files.Count;
			for (; index < auxiliaryDir.Subdirectories.Count; index++)
			{
				var auxiliarySubdir = auxiliaryDir.Subdirectories[index];
				if (auxiliarySubdir.FilesToRemove.HasValue && auxiliarySubdir.FilesToRemove.Value.Count is 0)
					continue;
				var subdir = dir.Subdirectories[auxiliarySubdir.Index];
				processDir(in subdir, in auxiliarySubdir, Path.Combine(path, subdir.Name), recursionLevel + 1);
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = auxiliaryDir.Files.Count + index;
					return;
				}
			}
			state.ProgressIndexStack.RemoveAt(recursionLevel);
		}
		if (state.Status is not ItemState.ItemStatus.Patching)
		{
			state.Status = ItemState.ItemStatus.Patching;
			state.ProgressIndexStack.Clear();
			state.DisplayProgress = 0;
		}
		StatusUpdated?.Invoke(Status.Patching);
		ProgressInitiated?.Invoke(ProgressType.Percentage, delta.NumTransferOperations, state.DisplayProgress);
		if (delta.IntermediateFileSize > 0)
			intermediateFileHandle = File.OpenHandle(Path.Combine(CdnClient.DownloadsDirectory!, $"{state.Id}.screlocpatchcache"), FileMode.OpenOrCreate, FileAccess.ReadWrite, options: FileOptions.SequentialScan);
		processDir(in targetManifest.Root, in delta.AuxiliaryTree,localPath, 0);
		intermediateFileHandle?.Dispose();
		if (cancellationToken.IsCancellationRequested)
		{
			state.SaveToFile();
			throw new OperationCanceledException(cancellationToken);
		}
		if (delta.IntermediateFileSize > 0)
			File.Delete(Path.Combine(CdnClient.DownloadsDirectory!, $"{state.Id}.screlocpatchcache"));
	}
	/// <summary>Deletes files and directories that have been removed from the target manifest.</summary>
	/// <param name="state">State of the item.</param>
	/// <param name="localPath">Path to the local item installation directory.</param>
	/// <param name="sourceManifest">The source manifest.</param>
	/// <param name="targetManifest">The target manifest.</param>
	/// <param name="delta">Delta object that lists files and directories to be removed.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	private void RemoveOldFiles(ItemState state, string localPath, DepotManifest sourceManifest, DepotManifest targetManifest, DepotDelta delta, CancellationToken cancellationToken)
	{
		void processDir(in DirectoryEntry dir, in DirectoryEntry.AuxiliaryEntry auxiliaryDir, string path, int recursionLevel)
		{
			int index;
			if (state.ProgressIndexStack.Count > recursionLevel)
				index = state.ProgressIndexStack[recursionLevel];
			else
			{
				state.ProgressIndexStack.Add(0);
				index = 0;
			}
			if (auxiliaryDir.FilesToRemove.HasValue)
			{
				var filesToRemove = auxiliaryDir.FilesToRemove.Value;
				for (; index < filesToRemove.Count; index++)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						state.ProgressIndexStack[recursionLevel] = index;
						return;
					}
					File.Delete(Path.Combine(path, sourceManifest.FileBuffer[filesToRemove[index]].Name));
					ProgressUpdated?.Invoke(++state.DisplayProgress);
				}
				index -= auxiliaryDir.Files.Count;
			}
			for (; index < auxiliaryDir.Subdirectories.Count; index++)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = auxiliaryDir.Files.Count + index;
					return;
				}
				var auxiliarySubdir = auxiliaryDir.Subdirectories[index];
				if (auxiliarySubdir.FilesToRemove.HasValue && auxiliarySubdir.FilesToRemove.Value.Count is 0)
				{
					Directory.Delete(Path.Combine(path, sourceManifest.DirectoryBuffer[auxiliarySubdir.Index].Name), true);
					ProgressUpdated?.Invoke(++state.DisplayProgress);
					continue;
				}
				var subdir = dir.Subdirectories[auxiliarySubdir.Index];
				processDir(in subdir, in auxiliarySubdir, Path.Combine(path, subdir.Name), recursionLevel + 1);
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = auxiliaryDir.Files.Count + index;
					return;
				}
			}
			state.ProgressIndexStack.RemoveAt(recursionLevel);
		}
		if (state.Status is not ItemState.ItemStatus.RemovingOldFiles)
		{
			state.Status = ItemState.ItemStatus.RemovingOldFiles;
			state.ProgressIndexStack.Clear();
			state.DisplayProgress = 0;
		}
		StatusUpdated?.Invoke(Status.RemovingOldFiles);
		ProgressInitiated?.Invoke(ProgressType.Numeric, delta.NumRemovals, state.DisplayProgress);
		processDir(in targetManifest.Root, in delta.AuxiliaryTree, localPath, 0);
		if (cancellationToken.IsCancellationRequested)
		{
			state.SaveToFile();
			throw new OperationCanceledException(cancellationToken);
		}
	}
	/// <summary>Moves acquired files and chunks to app installation.</summary>
	/// <param name="state">State of the item.</param>
	/// <param name="localPath">Path to the local item installation directory.</param>
	/// <param name="manifest">The target manifest.</param>
	/// <param name="delta">Delta object that lists data to be written.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	private void WriteNewData(ItemState state, string localPath, DepotManifest manifest, DepotDelta delta, CancellationToken cancellationToken)
	{
		byte[] buffer = GC.AllocateUninitializedArray<byte>(0x100000);
		SafeFileHandle? chunkBufferFileHandle = null;
		void writeDir(in DirectoryEntry dir, in DirectoryEntry.AcquisitionEntry acquisitionDir, string downloadPath, string localPath, int recursionLevel)
		{
			static long countTotalDirSize(in DirectoryEntry dir)
			{
				long result = 0;
				foreach (var file in dir.Files)
					result += file.Size;
				foreach (var subdir in dir.Subdirectories)
					result += countTotalDirSize(in subdir);
				return result;
			}
			if (acquisitionDir.IsNew)
			{
				Directory.Move(downloadPath, localPath);
				state.DisplayProgress += countTotalDirSize(in dir);
				ProgressUpdated?.Invoke(state.DisplayProgress);
				return;
			}
			int index;
			if (state.ProgressIndexStack.Count > recursionLevel)
				index = state.ProgressIndexStack[recursionLevel];
			else
			{
				state.ProgressIndexStack.Add(0);
				index = 0;
			}
			for (; index < acquisitionDir.Files.Count; index++)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = index;
					return;
				}
				var acquisitonFile = acquisitionDir.Files[index];
				var file = dir.Files[acquisitonFile.Index];
				if (acquisitonFile.Chunks.Count is 0)
				{
					string destinationFile = Path.Combine(localPath, file.Name);
					if (File.Exists(destinationFile))
						File.Delete(destinationFile);
					File.Move(Path.Combine(downloadPath, file.Name), destinationFile);
					if (file.Flags is not 0)
					{
						var attributes = (FileAttributes)0;
						if (file.Flags.HasFlag(FileEntry.Flag.ReadOnly))
							attributes = FileAttributes.ReadOnly;
						if (file.Flags.HasFlag(FileEntry.Flag.Hidden))
							attributes |= FileAttributes.Hidden;
						if (attributes is not 0)
							File.SetAttributes(destinationFile, attributes);
						if (file.Flags.HasFlag(FileEntry.Flag.Executable) && !OperatingSystem.IsWindows())
							File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(destinationFile) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
					}
					state.DisplayProgress += file.Size;
					ProgressUpdated?.Invoke(state.DisplayProgress);
				}
				else
				{
					int chunkRecLevel = recursionLevel + 1;
					int chunkIndex;
					if (state.ProgressIndexStack.Count > chunkRecLevel)
						chunkIndex = state.ProgressIndexStack[chunkRecLevel];
					else
					{
						state.ProgressIndexStack.Add(0);
						chunkIndex = 0;
					}
					string filePath = Path.Combine(localPath, file.Name);
					var attributes = File.GetAttributes(filePath);
					if (attributes.HasFlag(FileAttributes.ReadOnly))
						File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
					using var fileHandle = File.OpenHandle(filePath, access: FileAccess.Write, options: FileOptions.RandomAccess);
					for (; chunkIndex < acquisitonFile.Chunks.Count; chunkIndex++)
					{
						if (cancellationToken.IsCancellationRequested)
						{
							state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
							state.ProgressIndexStack[recursionLevel] = index;
							return;
						}
						var acquisitionChunk = acquisitonFile.Chunks[chunkIndex];
						var chunk = file.Chunks[acquisitionChunk.Index];
						var span = new Span<byte>(buffer, 0, chunk.UncompressedSize);
						RandomAccess.Read(chunkBufferFileHandle!, span, acquisitionChunk.Offset);
						RandomAccess.Write(fileHandle, span, chunk.Offset);
						state.DisplayProgress += chunk.UncompressedSize;
						ProgressUpdated?.Invoke(state.DisplayProgress);
					}
					state.ProgressIndexStack.RemoveAt(chunkRecLevel);
				}
			}
			index -= acquisitionDir.Files.Count;
			for (; index < acquisitionDir.Subdirectories.Count; index++)
			{
				var acquisitionSubdir = acquisitionDir.Subdirectories[index];
				var subdir = dir.Subdirectories[acquisitionSubdir.Index];
				writeDir(in subdir, in acquisitionSubdir, Path.Combine(downloadPath, subdir.Name), Path.Combine(localPath, subdir.Name), recursionLevel + 1);
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = acquisitionDir.Files.Count + index;
					return;
				}
			}
			state.ProgressIndexStack.RemoveAt(recursionLevel);
		}
		if (state.Status is not ItemState.ItemStatus.WritingNewData)
		{
			state.Status = ItemState.ItemStatus.WritingNewData;
			state.ProgressIndexStack.Clear();
			state.DisplayProgress = 0;
		}
		StatusUpdated?.Invoke(Status.WritingNewData);
		ProgressInitiated?.Invoke(ProgressType.Binary, delta.DownloadCacheSize - delta.IntermediateFileSize, state.DisplayProgress);
		if (delta.ChunkBufferFileSize > 0)
			chunkBufferFileHandle = File.OpenHandle(Path.Combine(CdnClient.DownloadsDirectory!, $"{state.Id}.scchunkbuffer"), options: FileOptions.SequentialScan);
		writeDir(in manifest.Root, in delta.AcquisitionTree, Path.Combine(CdnClient.DownloadsDirectory!, state.Id.ToString()), localPath, 0);
		chunkBufferFileHandle?.Dispose();
		if (cancellationToken.IsCancellationRequested)
		{
			state.SaveToFile();
			throw new OperationCanceledException(cancellationToken);
		}
		string downloadDirPath = Path.Combine(CdnClient.DownloadsDirectory!, state.Id.ToString());
		if (Directory.Exists(downloadDirPath))
			Directory.Delete(downloadDirPath, true);
		if (delta.ChunkBufferFileSize > 0)
			File.Delete(Path.Combine(CdnClient.DownloadsDirectory!, $"{state.Id}.scchunkbuffer"));
	}
	/// <summary>Computes difference between source and target manifests' contents</summary>
	/// <param name="sourceManifest">Source manifest to compute difference from.</param>
	/// <param name="targetManifest">Target manifest to compute difference to.</param>
	/// <param name="patch">Patch to apply to source version data.</param>
	/// <returns>Depot delta object containing difference between the manifests' contents.</returns>
	private DepotDelta ComputeUpdateDelta(DepotManifest sourceManifest, DepotManifest targetManifest, [Optional]DepotPatch? patch)
	{
		var acquisitionRoot = new DirectoryEntry.AcquisitionStaging(0, false);
		var auxiliaryRoot = new DirectoryEntry.AuxiliaryStaging(0);
		void processDir(in DirectoryEntry sourceDir, in DirectoryEntry targetDir, DirectoryEntry.AcquisitionStaging acquisitionDir, DirectoryEntry.AuxiliaryStaging auxiliaryDir)
		{
			int i = 0, targetOffset = 0;
			for (; i < sourceDir.Files.Count && i + targetOffset < targetDir.Files.Count; i++) //Range intersecting both directories
			{
				int targetIndex = i + targetOffset;
				var sourceFile = sourceDir.Files[i];
				var targetFile = targetDir.Files[targetIndex];
				int difference = string.Compare(sourceFile.Name, targetFile.Name, StringComparison.Ordinal);
				if (difference < 0) //File is present in the source directory but has been removed from the target one
				{
					(auxiliaryDir.FilesToRemove ??= []).Add(sourceDir.Files.Offset + i);
					targetOffset--;
				}
				else if (difference > 0) //A new file has been added to the target directory
				{
					acquisitionDir.Files.Add(new(targetIndex));
					targetOffset++;
					i--;
				}
				else
				{
					bool resized = false;
					var acquisitionFile = new FileEntry.AcquisitionStaging(targetIndex);
					var auxiliaryFile = new FileEntry.AuxiliaryStaging(targetIndex);
					int j = 0, targetChunkOffset = 0;
					for (; j < sourceFile.Chunks.Count && j + targetChunkOffset < targetFile.Chunks.Count; j++) //Range intersecting both files
					{
						int targetChunkIndex = j + targetChunkOffset;
						var sourceChunk = sourceFile.Chunks[j];
						var targetChunk = targetFile.Chunks[targetChunkIndex];
						int chunkDifference = sourceChunk.Gid.CompareTo(targetChunk.Gid);
						if (chunkDifference < 0) //Chunk has been removed in target version of the file
						{
							resized = true;
							targetChunkOffset--;
						}
						else if (chunkDifference > 0) //A new chunk has been added to the file or patched from previous version
						{
							if (patch is null)
								acquisitionFile.Chunks.Add(new(targetChunkIndex));
							else
							{
								int patchChunkIndex = Array.BinarySearch(patch.Chunks, new PatchChunkEntry
								{
									SourceChunkIndex = 0,
									TargetChunkIndex = targetFile.Chunks.Offset + targetChunkIndex,
									Data = default
								});
								if (patchChunkIndex >= 0)
									auxiliaryFile.ChunkPatches.Add(new()
									{
										UseIntermediateFile = true,
										ChunkIndex = targetChunkIndex,
										PatchChunkIndex = patchChunkIndex,
										Size = Math.Min(targetChunk.UncompressedSize, sourceManifest.ChunkBuffer[patch.Chunks[patchChunkIndex].SourceChunkIndex].UncompressedSize)
									});
								else
									acquisitionFile.Chunks.Add(new(targetChunkIndex));
							}
							targetChunkOffset++;
							j--;
						}
						else if (sourceChunk.Offset != targetChunk.Offset) //A chunk has been relocated
							auxiliaryFile.Relocations.Add(new()
							{
								UseIntermediateFile = true,
								SourceOffset = sourceChunk.Offset,
								TargetOffset = targetChunk.Offset,
								Size = targetChunk.UncompressedSize
							});
					}
					if (j < sourceFile.Chunks.Count)
						resized = true;
					for (j += targetChunkOffset; j < targetFile.Chunks.Count; j++) //Add remaining chunks that are unique to the target version of file
					{
						if (patch is null)
							acquisitionFile.Chunks.Add(new(j));
						else
						{
							int patchChunkIndex = Array.BinarySearch(patch.Chunks, new PatchChunkEntry
							{
								SourceChunkIndex = 0,
								TargetChunkIndex = targetFile.Chunks.Offset + j,
								Data = default
							});
							if (patchChunkIndex >= 0)
								auxiliaryFile.ChunkPatches.Add(new()
								{
									UseIntermediateFile = true,
									ChunkIndex = j,
									PatchChunkIndex = patchChunkIndex,
									Size = Math.Min(targetFile.Chunks[j].UncompressedSize, sourceManifest.ChunkBuffer[patch.Chunks[patchChunkIndex].SourceChunkIndex].UncompressedSize)
								});
							else
								acquisitionFile.Chunks.Add(new(j));
						}
					}
					if (acquisitionFile.Chunks.Count > 0)
					{
						if (acquisitionFile.Chunks.Count == targetFile.Chunks.Count)
							acquisitionFile.Chunks.Clear();
						acquisitionDir.Files.Add(acquisitionFile);
					}
					if (auxiliaryFile.ChunkPatches.Count > 0 || auxiliaryFile.Relocations.Count > 0)
					{
						var chunkPatches = auxiliaryFile.ChunkPatches;
						var relocations = auxiliaryFile.Relocations;
						//Batch relocation operations to reduce CPU load when computing weights and number of IO requests
						if (relocations.Count > 0)
						{
							relocations.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
							for (int k = 0; k < relocations.Count; k++)
							{
								var reloc = relocations[k];
								int batchSize = 0;
								long distance = reloc.TargetOffset - reloc.SourceOffset;
								long nextOffset = reloc.SourceOffset + reloc.Size;
								for (int l = k + 1; l < relocations.Count; l++)
								{
									var nextEntry = relocations[l];
									if (nextEntry.SourceOffset == nextOffset && nextEntry.TargetOffset - nextEntry.SourceOffset == distance)
									{
										nextOffset += nextEntry.Size;
										batchSize++;
									}
									else
										break;
								}
								if (batchSize > 0)
								{
									relocations.RemoveRange(k + 1, batchSize);
									reloc.Size = nextOffset - reloc.SourceOffset;
								}
							}
						}
						//Create and populate an array of transfer operations
						var transferOperations = new FileEntry.AuxiliaryStaging.TransferOperation[chunkPatches.Count + relocations.Count];
						int index = 0;
						foreach (var chunkPatch in chunkPatches)
						{
							var sourceChunk = sourceManifest.ChunkBuffer[patch!.Chunks[chunkPatch.PatchChunkIndex].SourceChunkIndex];
							var targetChunk = targetFile.Chunks[chunkPatch.ChunkIndex];
							transferOperations[index++] = new()
							{
								SourceStart = sourceChunk.Offset,
								SourceEnd = sourceChunk.Offset + sourceChunk.UncompressedSize,
								TargetStart = targetChunk.Offset,
								TargetEnd = targetChunk.Offset + targetChunk.UncompressedSize,
								Object = chunkPatch
							};
						}
						foreach (var reloc in relocations)
							transferOperations[index++] = new()
							{
								SourceStart = reloc.SourceOffset,
								SourceEnd = reloc.SourceOffset + reloc.Size,
								TargetStart = reloc.TargetOffset,
								TargetEnd = reloc.TargetOffset + reloc.Size,
								Object = reloc
							};
						chunkPatches.Clear();
						relocations.Clear();
						//Compute weights and use them to sort the array
						for (int k = 0; k < transferOperations.Length; k++)
						{
							var operation = transferOperations[k];
							for (int l = k + 1; l < transferOperations.Length; l++)
							{
								var other = transferOperations[l];
								if (operation.TargetStart < other.SourceEnd && other.SourceStart < operation.TargetEnd)
									other.Weight++;
								if (operation.SourceStart < other.TargetEnd && other.TargetStart < operation.SourceEnd)
									operation.Weight++;
							}
						}
						Array.Sort(transferOperations);
						//Disable use of intermediate file where possible
						for (int k = 0; k < transferOperations.Length; k++)
						{
							var operation = transferOperations[k];
							bool overlap = false;
							for (int l = k + 1; l < transferOperations.Length; l++)
							{
								var other = transferOperations[l];
								if (other.SourceStart < operation.TargetEnd && operation.TargetStart < other.SourceEnd)
								{
									overlap = true;
									break;
								}
							}
							if (!overlap)
							{
								if (operation.Object is FileEntry.AuxiliaryStaging.ChunkPatchEntry chunkPatch)
									chunkPatch.UseIntermediateFile = false;
								else if (operation.Object is FileEntry.AuxiliaryStaging.RelocationEntry reloc)
									reloc.UseIntermediateFile = false;
							}
						}
						//Copy operation object references into a list
						var transferOpsList = auxiliaryFile.TransferOperations;
						transferOpsList.Capacity = transferOperations.Length;
						foreach (var transferOperation in transferOperations)
							transferOpsList.Add(transferOperation.Object);
						//Split too large relocations into several 0.5 GB ones
						for (int k = 0; k < transferOpsList.Count; k++)
						{
							if (transferOpsList[k] is not FileEntry.AuxiliaryStaging.RelocationEntry reloc)
								continue;
							if (reloc.Size > 0x20000000)
							{
								long remainder = reloc.Size - 0x20000000;
								long offset = reloc.Size = 0x20000000;
								while (remainder > 0)
								{
									long size = Math.Min(remainder, 0x20000000);
									transferOpsList.Insert(++k, new FileEntry.AuxiliaryStaging.RelocationEntry()
									{
										UseIntermediateFile = reloc.UseIntermediateFile,
										SourceOffset = reloc.SourceOffset + offset,
										TargetOffset = reloc.TargetOffset + offset,
										Size = size
									});
									offset += size;
									remainder -= size;
								}
							}
						}
					}
					if (resized || auxiliaryFile.TransferOperations.Count > 0)
						auxiliaryDir.Files.Add(auxiliaryFile);
				}
			}
			int numRemainingFiles = sourceDir.Files.Count - i;
			if (numRemainingFiles > 0) //Remove remaining files that are unique to the source directory
			{
				auxiliaryDir.FilesToRemove = new(numRemainingFiles);
				int fileIndex = sourceDir.Files.Offset + i;
				for (int j = 0; j < numRemainingFiles; j++)
					auxiliaryDir.FilesToRemove.Add(fileIndex++);
			}
			for (int j = i + targetOffset; j < targetDir.Files.Count; j++) //Add remaining files that are unique to the target directory
				acquisitionDir.Files.Add(new(j));
			static void addSubdir(DirectoryEntry.AcquisitionStaging acquisitionDir, in DirectoryEntry subdir, int subdirIndex)
			{
				var acquisitionSubdir = new DirectoryEntry.AcquisitionStaging(subdirIndex, true);
				acquisitionSubdir.Files.Capacity = subdir.Files.Count;
				for (int i = 0; i < subdir.Files.Count; i++)
					acquisitionSubdir.Files.Add(new(i));
				acquisitionSubdir.Subdirectories.Capacity = subdir.Subdirectories.Count;
				for (int i = 0; i < subdir.Subdirectories.Count; i++)
					addSubdir(acquisitionSubdir, subdir.Subdirectories[i], i);
				acquisitionDir.Subdirectories.Add(acquisitionSubdir);
			}
			i = 0;
			targetOffset = 0;
			for (; i < sourceDir.Subdirectories.Count && i + targetOffset < targetDir.Subdirectories.Count; i++) //Range intersecting both directories
			{
				int targetIndex = i + targetOffset;
				var sourceSubdir = sourceDir.Subdirectories[i];
				var targetSubdir = targetDir.Subdirectories[targetIndex];
				int difference = string.Compare(sourceSubdir.Name, targetSubdir.Name, StringComparison.Ordinal);
				if (difference < 0) //Subdirectory is present in the source directory but has been removed from the target one
				{
					auxiliaryDir.Subdirectories.Add(new(sourceDir.Subdirectories.Offset + i) { FilesToRemove = [] });
					targetOffset--;
				}
				else if (difference > 0) //A new subdirectory has been added to the target directory
				{
					addSubdir(acquisitionDir, in targetSubdir, targetIndex);
					targetOffset++;
					i--;
				}
				else
				{
					var acquisitionSubdir = new DirectoryEntry.AcquisitionStaging(targetIndex, false);
					var auxiliarySubdir = new DirectoryEntry.AuxiliaryStaging(targetIndex);
					processDir(in sourceSubdir, in targetSubdir, acquisitionSubdir, auxiliarySubdir);
					if (acquisitionSubdir.Files.Count > 0 || acquisitionSubdir.Subdirectories.Count > 0)
						acquisitionDir.Subdirectories.Add(acquisitionSubdir);
					if (auxiliarySubdir.FilesToRemove is not null || auxiliarySubdir.Files.Count > 0 || auxiliarySubdir.Subdirectories.Count > 0)
						auxiliaryDir.Subdirectories.Add(auxiliarySubdir);
				}
			}
			int numRemainingSubdirs = sourceDir.Subdirectories.Count - i;
			if (numRemainingSubdirs > 0) //Remove remaining subdirectories that are unique to the source directory
			{
				int subdirIndex = sourceDir.Subdirectories.Offset + i;
				for (int j = 0; j < numRemainingFiles; j++)
					auxiliaryDir.Subdirectories.Add(new(subdirIndex++) { FilesToRemove = [] });
			}
			for (int j = i + targetOffset; j < targetDir.Subdirectories.Count; j++) //Add remaining subdirectories that are unique to the target directory
				addSubdir(acquisitionDir, targetDir.Subdirectories[j], j);
		}
		processDir(in sourceManifest.Root, in targetManifest.Root, acquisitionRoot, auxiliaryRoot);
		return new(targetManifest, acquisitionRoot, auxiliaryRoot);
	}
	/// <summary>Validates item installation to find missing chunks.</summary>
	/// <param name="state">State of the item.</param>
	/// <param name="manifest">The manifest to compare installation with.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	/// <returns>Depot delta object containing index tree for missing chunks.</returns>
	private DepotDelta Validate(ItemState state, DepotManifest manifest, CancellationToken cancellationToken)
	{
		string cachePath = Path.Combine(CdnClient.DownloadsDirectory!, $"{manifest.Item}-{manifest.Id}.scvcache");
		var cache = File.Exists(cachePath) ? new ValidationCache(cachePath) : new();
		byte[] buffer = GC.AllocateUninitializedArray<byte>(0x100000);
		void copyDirToStagingAndCount(in DirectoryEntry directory, DirectoryEntry.AcquisitionStaging stagingDir)
		{
			var files = directory.Files;
			cache.FilesMissing += files.Count;
			for (int i = 0; i < files.Count; i++)
			{
				state.DisplayProgress += files[i].Size;
				stagingDir.Files.Add(new(i));
			}
			var subdirs = directory.Subdirectories;
			for (int i = 0; i < subdirs.Count; i++)
			{
				var stagingSubDir = new DirectoryEntry.AcquisitionStaging(i, true);
				copyDirToStagingAndCount(subdirs[i], stagingSubDir);
				stagingDir.Subdirectories.Add(stagingSubDir);
			}
		}
		void validateDir(in DirectoryEntry directory, DirectoryEntry.AcquisitionStaging stagingDir, string path, int recursionLevel)
		{
			if (!Directory.Exists(path))
			{
				int prevFilesMissing = cache.FilesMissing;
				stagingDir.IsNew = true;
				copyDirToStagingAndCount(in directory, stagingDir);
				if (cache.FilesMissing - prevFilesMissing > 0)
				{
					ProgressUpdated?.Invoke(state.DisplayProgress);
					ValidationCounterUpdated?.Invoke(cache.FilesMissing, ValidationCounterType.Missing);
				}
				return;
			}
			if (directory.Files.Count is 0 && directory.Subdirectories.Count is 0)
				return;
			int index;
			int continueType;
			if (state.ProgressIndexStack.Count > recursionLevel)
			{
				index = state.ProgressIndexStack[recursionLevel];
				continueType = index >= directory.Files.Count ? 2 : 1;
			}
			else
			{
				state.ProgressIndexStack.Add(0);
				index = 0;
				continueType = 0;
			}
			for (; index < directory.Files.Count; index++)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = index;
					return;
				}
				var file = directory.Files[index];
				string filePath = Path.Combine(path, file.Name);
				if (!File.Exists(filePath))
				{
					stagingDir.Files.Add(new(index));
					cache.FilesMissing++;
					state.DisplayProgress += file.Size;
					ProgressUpdated?.Invoke(state.DisplayProgress);
					ValidationCounterUpdated?.Invoke(cache.FilesMissing, ValidationCounterType.Missing);
					continue;
				}
				var stagingFile = continueType is 1 ? (stagingDir.Files.Find(f => f.Index == index) ?? new FileEntry.AcquisitionStaging(index)) : new FileEntry.AcquisitionStaging(index);
				using var fileHandle = File.OpenHandle(filePath, options: FileOptions.RandomAccess);
				int chunkRecLevel = recursionLevel + 1;
				int chunkIndex;
				if (state.ProgressIndexStack.Count > chunkRecLevel)
					chunkIndex = state.ProgressIndexStack[chunkRecLevel];
				else
				{
					state.ProgressIndexStack.Add(0);
					chunkIndex = 0;
				}
				long fileSize = RandomAccess.GetLength(fileHandle);
				var chunks = file.Chunks;
				var span = new Span<byte>(buffer);
				for (; chunkIndex < chunks.Count; chunkIndex++)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
						state.ProgressIndexStack[recursionLevel] = index;
						return;
					}
					var chunk = chunks[chunkIndex];
					if (chunk.Offset + chunk.UncompressedSize > fileSize)
						stagingFile.Chunks.Add(new(chunkIndex));
					else
					{
						var chunkSpan = span[..chunk.UncompressedSize];
						RandomAccess.Read(fileHandle, chunkSpan, chunk.Offset);
						if (Adler.ComputeChecksum(chunkSpan) != chunk.Checksum)
							stagingFile.Chunks.Add(new(chunkIndex));
					}
					state.DisplayProgress += chunk.UncompressedSize;
					ProgressUpdated?.Invoke(state.DisplayProgress);
				}
				state.ProgressIndexStack.RemoveAt(chunkRecLevel);
				if (stagingFile.Chunks.Count is not 0)
				{
					if (stagingFile.Chunks.Count == file.Chunks.Count)
						stagingFile.Chunks.Clear();
					stagingDir.Files.Add(stagingFile);
					ValidationCounterUpdated?.Invoke(++cache.FilesMismatching, ValidationCounterType.Mismatching);
				}
				else
					ValidationCounterUpdated?.Invoke(++cache.FilesMatching, ValidationCounterType.Matching);
			}
			index -= directory.Files.Count;
			for (; index < directory.Subdirectories.Count; index++)
			{
				var subdir = directory.Subdirectories[index];
				var stagingSubdir = continueType is 2 ? (stagingDir.Subdirectories.Find(sd => sd.Index == index) ?? new DirectoryEntry.AcquisitionStaging(index, false)) : new DirectoryEntry.AcquisitionStaging(index, false);
				validateDir(in subdir, stagingSubdir, Path.Combine(path, subdir.Name), recursionLevel + 1);	
				if (continueType is 2)
					continueType = 0;
				else if (stagingSubdir.IsNew || stagingSubdir.Files.Count > 0 || stagingSubdir.Subdirectories.Count > 0)
					stagingDir.Subdirectories.Add(stagingSubdir);
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = directory.Files.Count + index;
					return;
				}
			}
			state.ProgressIndexStack.RemoveAt(recursionLevel);
			return;
		}
		string basePath = state.Id.WorkshopItemId is 0 ? InstallationPath : Path.Combine(WorkshopContentPath, state.Id.WorkshopItemId.ToString());
		if (state.Status is not ItemState.ItemStatus.Validating)
		{
			state.Status = ItemState.ItemStatus.Validating;
			state.ProgressIndexStack.Clear();
			state.DisplayProgress = 0;
		}
		StatusUpdated?.Invoke(Status.Validating);
		ProgressInitiated?.Invoke(ProgressType.Percentage, manifest.DataSize, state.DisplayProgress);
		validateDir(in manifest.Root, cache.AcquisitionTree, basePath, 0);
		if (cancellationToken.IsCancellationRequested)
		{
			cache.WriteToFile(cachePath);
			state.SaveToFile();
			throw new OperationCanceledException(cancellationToken);
		}
		if (File.Exists(cachePath))
			File.Delete(cachePath);
		return new(manifest, cache.AcquisitionTree);
	}
	/// <summary>Updates item to target version.</summary>
	/// <param name="item">ID of the item to update.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	/// <param name="targetManifestId">ID of the manifest to update to, if not specified the latest is used.</param>
	/// <returns><see langword="true"/> if depot installation is already up to date, <see langword="false"/> if an update has been performed.</returns>
	public bool Update(ItemIdentifier item, CancellationToken cancellationToken, [Optional]ulong targetManifestId)
	{
		if (targetManifestId is 0)
		{
			if (item.WorkshopItemId is 0)
				targetManifestId = CmClient.GetDepotManifestIds(AppId).TryGetValue(item.DepotId, out ulong mId) ? mId : throw new SteamException(SteamException.ErrorType.DepotManifestIdNotFound);
			else
			{
				var details = CmClient.GetWorkshopItemDetails(item.WorkshopItemId);
				if (details.Length is 0)
					throw new SteamException(SteamException.ErrorType.CMFailedToGetWorkshopItemDetails);
				targetManifestId = details[0].ManifestId;
				if (targetManifestId is 0)
					targetManifestId = CmClient.GetWorkshopItemManifestId(AppId, item.WorkshopItemId);
			}
		}
		var state = new ItemState(item, Path.Combine(_scDataPath, $"{item}.scitemstate"));
		if (state.CurrentManifestId is 0)
			return Validate(item, cancellationToken, targetManifestId); //Cannot update when source version is unknown
		if (state.CurrentManifestId == targetManifestId)
			return true;
		var sourceManifest = CdnClient.GetManifest(AppId, item, state.CurrentManifestId, cancellationToken);
		var targetManifest = CdnClient.GetManifest(AppId, item, targetManifestId, cancellationToken);
		var patch = CmClient.GetPatchAvailability(AppId, item.DepotId, sourceManifest.Id, targetManifest.Id)
			? CdnClient.GetPatch(AppId, item, sourceManifest, targetManifest, cancellationToken)
			: null;
		string deltaFilePath = Path.Combine(CdnClient.DownloadsDirectory!, $"{item}-{sourceManifest.Id}-{targetManifest.Id}.scdelta");
		DepotDelta delta;
		if (state.Status < ItemState.ItemStatus.Preallocating)
		{
			delta = ComputeUpdateDelta(sourceManifest, targetManifest, patch);
			delta.WriteToFile(deltaFilePath);
		}
		else
			delta = new(deltaFilePath);
		if (state.Status <= ItemState.ItemStatus.Preallocating)
			CdnClient.Preallocate(state, targetManifest, delta, cancellationToken);
		if (state.Status <= ItemState.ItemStatus.Downloading)
			CdnClient.DownloadContent(state, targetManifest, delta, cancellationToken);
		Commit(state, sourceManifest, targetManifest, patch, delta, cancellationToken);
		state.Status = ItemState.ItemStatus.Installed;
		state.CurrentManifestId = targetManifestId;
		state.DisplayProgress = 0;
		state.ProgressIndexStack.Clear();
		state.SaveToFile();
		File.Delete(deltaFilePath);
		if (patch is not null)
			File.Delete(Path.Combine(CdnClient.DownloadsDirectory!, $"{item}-{sourceManifest.Id}-{targetManifest.Id}.scpatch"));
		return false;
	}
	/// <summary>Verifies item files, downloads and installs missing data.</summary>
	/// <param name="item">ID of the item to validate.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	/// <param name="manifestId">ID of the manifest to perform validation for, if not specified the latest is used.</param>
	/// <returns><see langword="true"/> if item installation is already up to date, <see langword="false"/> if mismatches have been found and fixed.</returns>
	public bool Validate(ItemIdentifier item, CancellationToken cancellationToken, [Optional]ulong manifestId)
	{
		if (manifestId is 0)
		{
			if (item.WorkshopItemId is 0)
				manifestId = CmClient.GetDepotManifestIds(AppId).TryGetValue(item.DepotId, out ulong mId) ? mId : throw new SteamException(SteamException.ErrorType.DepotManifestIdNotFound);
			else
			{
				var details = CmClient.GetWorkshopItemDetails(item.WorkshopItemId);
				if (details.Length is 0)
					throw new SteamException(SteamException.ErrorType.CMFailedToGetWorkshopItemDetails);
				manifestId = details[0].ManifestId;
				if (manifestId is 0)
					manifestId = CmClient.GetWorkshopItemManifestId(AppId, item.WorkshopItemId);
			}
		}
		var state = new ItemState(item, Path.Combine(_scDataPath, $"{item}.scitemstate"));
		var manifest = CdnClient.GetManifest(AppId, item, manifestId, cancellationToken);
		string deltaFilePath = Path.Combine(CdnClient.DownloadsDirectory!, $"{item}-{manifestId}.scdelta");
		DepotDelta delta;
		if (state.Status <= ItemState.ItemStatus.Validating)
		{
			delta = Validate(state, manifest, cancellationToken);
			if (delta.AcquisitionTree.Files.Count is 0 && delta.AcquisitionTree.Subdirectories.Count is 0)
			{
				state.Status = ItemState.ItemStatus.Installed;
				state.CurrentManifestId = manifestId;
				state.DisplayProgress = 0;
				state.ProgressIndexStack.Clear();
				state.SaveToFile();
				return true;
			}
			delta.WriteToFile(deltaFilePath);
		}
		else
			delta = new(deltaFilePath);
		if (state.Status <= ItemState.ItemStatus.Preallocating)
			CdnClient.Preallocate(state, manifest, delta, cancellationToken);
		if (state.Status <= ItemState.ItemStatus.Downloading)
			CdnClient.DownloadContent(state, manifest, delta, cancellationToken);
		Commit(state, null, manifest, null, delta, cancellationToken);
		state.Status = ItemState.ItemStatus.Installed;
		state.CurrentManifestId = manifestId;
		state.DisplayProgress = 0;
		state.ProgressIndexStack.Clear();
		state.SaveToFile();
		File.Delete(deltaFilePath);
		return false;
	}
	/// <summary>Called when a progress is being set up.</summary>
	public event ProgressInitiatedHandler? ProgressInitiated;
	/// <summary>Called when progress' current value is updated.</summary>
	public event ProgressUpdatedHandler? ProgressUpdated;
	/// <summary>Called when app manager status is updated.</summary>
	public event StatusUpdatedHandler? StatusUpdated;
	/// <summary>Called when a validation file counter value is updated.</summary>
	public event ValidationCounterUpdatedHandler? ValidationCounterUpdated;
}