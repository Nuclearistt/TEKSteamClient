using System.IO.Hashing;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TEKSteamClient.CM.Messages.Bodies;
using TEKSteamClient.Manifest;
using TEKSteamClient.Utils;

namespace TEKSteamClient;

/// <summary>Maintains the list of Steam CDN servers and downloads their content.</summary>
public class CDNClient
{
	/// <summary>CDN server list.</summary>
	private string[] _servers = [];
	/// <summary>HTTP client that downloads manifests and patches from the CDN.</summary>
	private readonly HttpClient s_client = new() { DefaultRequestVersion = HttpVersion.Version20, Timeout = TimeSpan.FromSeconds(10) };
	/// <summary>Decryption keys to use for decrypting depots' content.</summary>
	public static readonly Dictionary<uint, byte[]> DepotDecryptionKeys = [];
	/// <summary>Path to directory where downloaded files are stored.</summary>
	public string? DownloadsDirectory { get; init; }
	/// <summary>Path to directory where manifest files are stored.</summary>
	public string? ManifestsDirectory { get; init; }
	/// <summary>CM client used to get CDN server list and manifest request codes.</summary>
	public required CM.CMClient CmClient { get; init; }
	/// <summary>
	/// Number of servers that clients will simultaneously use when downloading depot content. The default value is <code>Math.Max(Environment.ProcessorCount * 6, 50)</code>.
	/// It is also the number of download threads, scale it accordingly to your network bandwidth and CPU capabilities.
	/// </summary>
	public static int NumDownloadServers { get; set; } = Math.Max(Environment.ProcessorCount * 6, 50);
	/// <summary>Gets CDN server list if necessary.</summary>
	private void CheckServerList()
	{
		if (_servers.Length > NumDownloadServers)
			return;
		var servers = new List<CDNServersResponse.Types.Server>(NumDownloadServers + 1);
		while (servers.Count < NumDownloadServers + 1)
			servers.AddRange(Array.FindAll(CmClient.GetCDNServers(), s => s.Type is "SteamCache" or "CDN" && s.HttpsSupport is "mandatory" or "optional"));
		servers.Sort((left, right) =>
		{
			int result = (right.PreferredServer ? 1 : 0) - (left.PreferredServer ? 1 : 0);
			if (result is 0)
			{
				result = left.Load.CompareTo(right.Load);
				if (result is 0)
					result = (right.HttpsSupport is "mandatory" ? 1 : 0) - (left.HttpsSupport is "mandatory" ? 1 : 0);
			}
			return result;
		});
		_servers = new string[servers.Count];
		for (int i = 0; i < servers.Count; i++)
			_servers[i] = servers[i].Host;
	}
	private static void DownloadThreadProcedure(object? arg)
	{
		var context = (DownloadThreadContext)arg!;
		byte[] buffer = GC.AllocateUninitializedArray<byte>(0x400000);
		using var aes = Aes.Create();
		aes.Key = DepotDecryptionKeys[context.SharedContext.DepotId];
		var lzmaDecoder = new Utils.LZMA.Decoder();
		var httpClient = context.SharedContext.HttpClients[context.Index];
		var token = context.Cts.Token;
		var downloadBuffer = new Memory<byte>(buffer, 0, 0x200000);
		var bufferSpan = new Span<byte>(buffer);
		var ivSpan = (ReadOnlySpan<byte>)bufferSpan[..16];
		var decryptedIvSpan = bufferSpan.Slice(0x3FFFF0, 16);
		var decryptedDataSpan = new Span<byte>(buffer, 0x200000, 0x1FFFF0);
		bool resumedContext = context.ChunkContext.FilePath is not null;
		int fallbackServerIndex = NumDownloadServers;
		for (;;)
		{
			if (resumedContext)
				resumedContext = false;
			else
			{
				context.ChunkContext = context.SharedContext.GetNextChunk(context.ChunkContext.CompressedSize);
				if (context.ChunkContext.FilePath is null)
					return;
			}
			int compressedSize = context.ChunkContext.CompressedSize;
			int uncompressedSize = context.ChunkContext.UncompressedSize;
			var uri = new Uri(context.ChunkContext.Gid.ToString(), UriKind.Relative);
			Exception? exception = null;
			var dataSpan = (ReadOnlySpan<byte>)bufferSpan[16..compressedSize];
			var uncompressedDataSpan = bufferSpan[..uncompressedSize];
			for (int i = 0; i < 5; i++) //5 attempts, after which the thread fails
			{
				if (token.IsCancellationRequested)
					return;
				try
				{
					//Download encrypted chunk data
					var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20 };
					using var response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
					using var content = response.EnsureSuccessStatusCode().Content;
					using var stream = content.ReadAsStream(token);
					int bytesRead;
					using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
					cts.CancelAfter(60000);
					try
					{
						var task = stream.ReadAtLeastAsync(downloadBuffer, compressedSize, false, cts.Token);
						if (task.IsCompletedSuccessfully)
							bytesRead = task.GetAwaiter().GetResult();
						else
							bytesRead = task.AsTask().GetAwaiter().GetResult();
					}
					catch (OperationCanceledException oce)
					{
						if (oce.CancellationToken == token)
							return;
						exception = new TimeoutException();
						continue;
					}
					if (bytesRead != compressedSize)
					{
						exception = new InvalidDataException($"Downloaded chunk data size doesn't match expected [URL: {httpClient.BaseAddress}/{request.RequestUri}]");
						continue;
					}
					//Decrypt the data
					aes.DecryptEcb(ivSpan, decryptedIvSpan, PaddingMode.None);
					int decryptedDataSize = aes.DecryptCbc(dataSpan, decryptedIvSpan, decryptedDataSpan);
					//Decompress the data
					if (!lzmaDecoder.Decode(decryptedDataSpan[..decryptedDataSize], uncompressedDataSpan))
					{
						exception = new InvalidDataException("LZMA decoding failed");
						continue;
					}
					if (Adler.ComputeChecksum(uncompressedDataSpan) != context.ChunkContext.Checksum)
					{
						exception = new InvalidDataException("Adler checksum mismatch");
						continue;
					}
					exception = null;
				}
				catch (OperationCanceledException) { return; }
				catch (HttpRequestException hre) when (hre.StatusCode > HttpStatusCode.InternalServerError) 
				{
					if (fallbackServerIndex is 0)
					{
						httpClient = context.SharedContext.HttpClients[context.Index];
						fallbackServerIndex = NumDownloadServers;
					}
					else
					{
						httpClient = context.SharedContext.HttpClients[fallbackServerIndex];
						if (++fallbackServerIndex == context.SharedContext.HttpClients.Length)
							fallbackServerIndex = 0;
					}
				}
				catch (Exception e)
				{
					exception = e;
					continue;
				}
			}
			if (exception is not null)
			{
				context.SharedContext.Exception = exception;
				context.Cts.Cancel();
				return;
			}
			//Write acquired chunk data to the file
			var handle = context.ChunkContext.FileHandle;
			RandomAccess.Write(handle.Handle, uncompressedDataSpan, context.ChunkContext.FileOffset);
			handle.Release();
		}
	}
	/// <summary>Downloads depot content chunks.</summary>
	/// <param name="state">State of the item.</param>
	/// <param name="manifest">The target manifest.</param>
	/// <param name="delta">Delta object that lists data to be downloaded.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	internal void DownloadContent(ItemState state, DepotManifest manifest, DepotDelta delta, CancellationToken cancellationToken)
	{
		if (state.Status is not ItemState.ItemStatus.Downloading)
		{
			state.Status = ItemState.ItemStatus.Downloading;
			state.ProgressIndexStack.Clear();
			state.DisplayProgress = 0;
		}
		StatusUpdated?.Invoke(Status.Downloading);
		if (!DepotDecryptionKeys.ContainsKey(state.Id.DepotId))
			throw new SteamException(SteamException.ErrorType.DepotDecryptionKeyMissing);
		CheckServerList();
		var sharedContext = new DownloadContext(state, manifest, delta, DownloadsDirectory!, ProgressUpdated) { HttpClients = new HttpClient[_servers.Length] };
		for (int i = 0; i < _servers.Length; i++)
			sharedContext.HttpClients[i] = new()
			{
				BaseAddress = new($"https://{_servers[i]}/depot/{state.Id}/chunk/"),
				DefaultRequestVersion = HttpVersion.Version20,
				Timeout = TimeSpan.FromSeconds(10)
			};
		var contexts = new DownloadThreadContext[NumDownloadServers];
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		for (int i = 0; i < contexts.Length; i++)
			contexts[i] = new()
			{
				Index = i,
				Cts = linkedCts,
				SharedContext = sharedContext
			};
		string chunkContextsFilePath = Path.Join(DownloadsDirectory!, $"{state.Id}.scchcontexts");
		if (File.Exists(chunkContextsFilePath))
		{
			Span<byte> buffer;
			using (var fileHandle = File.OpenHandle(chunkContextsFilePath))
			{
				buffer = GC.AllocateUninitializedArray<byte>((int)RandomAccess.GetLength(fileHandle));
				RandomAccess.Read(fileHandle, buffer, 0);
			}
			nint offset = 0;
			int pathOffset = contexts.Length * 48;
			for (int i = 0; i < contexts.Length; i++)
				contexts[i].ChunkContext = ChunkContext.LoadFromBuffer(buffer, ref offset, ref pathOffset);
		}
		ProgressInitiated?.Invoke(ProgressType.Binary, delta.DownloadSize, state.DisplayProgress);
		var threads = new Thread[contexts.Length];
		for (int i = 0; i < threads.Length; i++)
			threads[i] = new(DownloadThreadProcedure) { Name = $"{state.Id} Download Thread {i}" };
		for (int i = 0; i < threads.Length; i++)
			threads[i].UnsafeStart(contexts[i]);
		foreach (var thread in threads)
			thread.Join();
		sharedContext.CurrentFileHandle?.Handle.Close();
		sharedContext.ChunkBufferFileHandle?.Handle.Close();
		foreach (var client in sharedContext.HttpClients)
			client.Dispose();
		if (linkedCts.IsCancellationRequested)
		{
			state.SaveToFile();
			int contextsFileSize = 0;
			foreach (var context in contexts)
				contextsFileSize += 48 + Encoding.UTF8.GetByteCount(context.ChunkContext.FilePath);
			Span<byte> buffer = new byte[contextsFileSize];
			nint offset = 0;
			int pathOffset = contexts.Length * 48;
			foreach (var context in contexts)
				context.ChunkContext.WriteToBuffer(buffer, ref offset, ref pathOffset);
			using var fileHandle = File.OpenHandle(chunkContextsFilePath, FileMode.Create, FileAccess.Write, preallocationSize: buffer.Length);
			RandomAccess.Write(fileHandle, buffer, 0);
			var exception = sharedContext.Exception;
			throw exception switch
			{
				null => new OperationCanceledException(linkedCts.Token),
				SteamException => exception,
				_ => new SteamException(SteamException.ErrorType.DownloadFailed, exception)
			};
		}
	}
	/// <summary>Preallocates all files for the download on the disk.</summary>
	/// <param name="state">State of the item.</param>
	/// <param name="manifest">The target manifest.</param>
	/// <param name="delta">Delta object that lists files to be preallocated.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	internal void Preallocate(ItemState state, DepotManifest manifest, DepotDelta delta, CancellationToken cancellationToken)
	{
		void preallocDir(in DirectoryEntry.AcquisitionEntry dir, string path, int recursionLevel)
		{
			int index;
			if (state.ProgressIndexStack.Count > recursionLevel)
				index = state.ProgressIndexStack[recursionLevel];
			else
			{
				state.ProgressIndexStack.Add(0);
				index = 0;
				if (dir.IsNew || dir.Files.Any(a => a.Chunks.Count is 0))
					Directory.CreateDirectory(path);
			}
			for (; index < dir.Files.Count; index++)
			{
				var acquisitonFile = dir.Files[index];
				if (acquisitonFile.Chunks.Count is not 0)
					continue;
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = index;
					return;
				}
				var file = manifest.FileBuffer[acquisitonFile.Index];
				var handle = File.OpenHandle(Path.Join(path, file.Name), FileMode.Create, FileAccess.Write, preallocationSize: file.Size);
				RandomAccess.SetLength(handle, file.Size);
				handle.Dispose();
				ProgressUpdated?.Invoke(++state.DisplayProgress);
			}
			index -= dir.Files.Count;
			for (; index < dir.Subdirectories.Count; index++)
			{
				var subdir = dir.Subdirectories[index];
				preallocDir(in subdir, Path.Join(path, manifest.DirectoryBuffer[subdir.Index].Name), recursionLevel + 1);
				if (cancellationToken.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = dir.Files.Count + index;
					return;
				}
			}
			state.ProgressIndexStack.RemoveAt(recursionLevel);
		}
		if (state.Status is not ItemState.ItemStatus.Preallocating)
		{
			state.Status = ItemState.ItemStatus.Preallocating;
			state.ProgressIndexStack.Clear();
			state.DisplayProgress = 0;
		}
		StatusUpdated?.Invoke(Status.Preallocating);
		if (new DriveInfo(DownloadsDirectory!).AvailableFreeSpace < delta.DownloadCacheSize)
			throw new SteamNotEnoughDiskSpaceException(delta.DownloadCacheSize);
		ProgressInitiated?.Invoke(ProgressType.Numeric, delta.NumDownloadFiles, state.DisplayProgress);
		preallocDir(in delta.AcquisitionTree, Path.Join(DownloadsDirectory!, state.Id.ToString()), 0);
		if (cancellationToken.IsCancellationRequested)
		{
			state.SaveToFile();
			throw new OperationCanceledException(cancellationToken);
		}
		if (delta.ChunkBufferFileSize > 0)
		{
			var handle = File.OpenHandle(Path.Join(DownloadsDirectory!, $"{state.Id}.scchunkbuffer"), FileMode.Create, FileAccess.Write, preallocationSize: delta.ChunkBufferFileSize);
			RandomAccess.SetLength(handle, delta.ChunkBufferFileSize);
			handle.Dispose();
			ProgressUpdated?.Invoke(++state.DisplayProgress);
		}
		if (delta.IntermediateFileSize > 0)
		{
			var handle = File.OpenHandle(Path.Join(DownloadsDirectory!, $"{state.Id}.screlocpatchcache"), FileMode.Create, FileAccess.Write, preallocationSize: delta.IntermediateFileSize);
			RandomAccess.SetLength(handle, delta.IntermediateFileSize);
			handle.Dispose();
			ProgressUpdated?.Invoke(++state.DisplayProgress);
		}
	}
	/// <summary>Gets specified manifest by reading it from file in <see cref="ManifestsDirectory"/> if available or downloading from a CDN server.</summary>
	/// <param name="appId">ID of the app that the manifest belongs to.</param>
	/// <param name="item">Identifier of the item that the manifest belongs to.</param>
	/// <param name="manifestId">ID of the manifest to get.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	/// <returns>A depot manifest object.</returns>
	/// <exception cref="SteamException">Download failed.</exception>
	public DepotManifest GetManifest(uint appId, ItemIdentifier item, ulong manifestId, CancellationToken cancellationToken)
	{
		if (ManifestsDirectory is not null)
		{
			string filePath = Path.Join(ManifestsDirectory, $"{item}-{manifestId}.scmanifest");
			if (File.Exists(filePath))
			{
				StatusUpdated?.Invoke(Status.LoadingManifest);
				return new(filePath, item, manifestId);
			}
		}
		CheckServerList();
		StatusUpdated?.Invoke(Status.DownloadingManifest);
		ulong requestCode = CmClient.GetManifestRequestCode(appId, item.DepotId, manifestId);
		byte[]? buffer = null;
		Exception? exception = null;
		foreach (var server in _servers)
			try
			{
				var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{server}/depot/{item.DepotId}/manifest/{manifestId}/5/{requestCode}")) { Version = HttpVersion.Version20 };
				using var response = s_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).GetAwaiter().GetResult().EnsureSuccessStatusCode();
				uint? crc = uint.TryParse(response.Headers.TryGetValues("x-content-crc", out var headerValue) ? headerValue.FirstOrDefault() : null, out uint value) ? value : null;
				using var content = response.Content;
				int size = (int)(content.Headers.ContentLength ?? throw new NullReferenceException("Content-Length is missing"));
				buffer ??= GC.AllocateUninitializedArray<byte>(size);
				ProgressInitiated?.Invoke(ProgressType.Binary, size);
				using var stream = content.ReadAsStream(cancellationToken);
				for (int offset = 0; offset < size;)
				{
					cancellationToken.ThrowIfCancellationRequested();
					offset += stream.Read(new Span<byte>(buffer, offset, size - offset));
					ProgressUpdated?.Invoke(offset);
				}
				if (crc.HasValue)
				{
					Span<byte> contentCrc = stackalloc byte[4];
					Crc32.Hash(buffer, contentCrc);
					if (crc.Value != Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(contentCrc)))
						throw new SteamException(SteamException.ErrorType.ManifestCrcMismatch);
				}
				StatusUpdated?.Invoke(Status.LoadingManifest);
				var result = new DepotManifest(buffer, item);
				if (ManifestsDirectory is not null)
					result.WriteToFile(Path.Join(ManifestsDirectory, $"{item}-{manifestId}.scmanifest"));
				return result;
			}
			catch (Exception e)
			{
				if (e is TaskCanceledException && cancellationToken.IsCancellationRequested)
					throw new OperationCanceledException(cancellationToken);
				if (e is OperationCanceledException)
					throw;
				exception = e;
			}
		throw new SteamException(SteamException.ErrorType.FailedToDownloadManifest, exception);
	}
	/// <summary>Gets specified patch by reading it from file in <see cref="DownloadsDirectory"/> if available or downloading from a CDN server.</summary>
	/// <param name="appId">ID of the app that the patch belongs to.</param>
	/// <param name="item">Identifier of the item that the patch belongs to.</param>
	/// <param name="sourceManifest">Source manifest.</param>
	/// <param name="targetManifest">Target manifest.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	/// <returns>A depot patch object.</returns>
	/// <exception cref="SteamException">Download failed.</exception>
	public DepotPatch GetPatch(uint appId, ItemIdentifier item, DepotManifest sourceManifest, DepotManifest targetManifest, CancellationToken cancellationToken)
	{
		if (DownloadsDirectory is not null)
		{
			string filePath = Path.Join(DownloadsDirectory, $"{item}-{sourceManifest.Id}-{targetManifest.Id}.scpatch");
			if (File.Exists(filePath))
			{
				StatusUpdated?.Invoke(Status.LoadingPatch);
				return new(filePath, item, sourceManifest.Id, targetManifest.Id);
			}
		}
		CheckServerList();
		StatusUpdated?.Invoke(Status.DownloadingPatch);
		byte[]? buffer = null;
		Exception? exception = null;
		foreach (var server in _servers)
			try
			{
				var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{server}/depot/{item.DepotId}/patch/{sourceManifest.Id}/{targetManifest.Id}")) { Version = HttpVersion.Version20 };
				using var response = s_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).GetAwaiter().GetResult().EnsureSuccessStatusCode();
				uint? crc = uint.TryParse(response.Headers.TryGetValues("x-content-crc", out var headerValue) ? headerValue.FirstOrDefault() : null, out uint value) ? value : null;
				using var content = response.Content;
				int size = (int)(content.Headers.ContentLength ?? throw new NullReferenceException("Content-Length is missing"));
				buffer ??= GC.AllocateUninitializedArray<byte>(size);
				ProgressInitiated?.Invoke(ProgressType.Binary, size);
				using var stream = content.ReadAsStream(cancellationToken);
				for (int offset = 0; offset < size;)
				{
					cancellationToken.ThrowIfCancellationRequested();
					offset += stream.Read(new Span<byte>(buffer, offset, size - offset));
					ProgressUpdated?.Invoke(offset);
				}
				if (crc.HasValue)
				{
					Span<byte> contentCrc = stackalloc byte[4];
					Crc32.Hash(buffer, contentCrc);
					if (crc.Value != Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(contentCrc)))
						throw new SteamException(SteamException.ErrorType.PatchCrcMismatch);
				}
				StatusUpdated?.Invoke(Status.LoadingPatch);
				var result = new DepotPatch(buffer, item, sourceManifest, targetManifest);
				if (DownloadsDirectory is not null)
					result.WriteToFile(Path.Join(DownloadsDirectory, $"{item}-{sourceManifest.Id}-{targetManifest.Id}.scpatch"));
				return result;
			}
			catch (Exception e)
			{
				if (e is TaskCanceledException && cancellationToken.IsCancellationRequested)
					throw new OperationCanceledException(cancellationToken);
				if (e is OperationCanceledException)
					throw;
				exception = e;
			}
		throw new SteamException(SteamException.ErrorType.FailedToDownloadPatch, exception);
	}
	/// <summary>Called when a progress is being set up.</summary>
	public event ProgressInitiatedHandler? ProgressInitiated;
	/// <summary>Called when progress' current value is updated.</summary>
	public event ProgressUpdatedHandler? ProgressUpdated;
	/// <summary>Called when client status is updated.</summary>
	public event StatusUpdatedHandler? StatusUpdated;
	/// <summary>Context containing all the data needed to download a chunk.</summary>
	private readonly struct ChunkContext
	{
		/// <summary>Size of LZMA-compressed chunk data.</summary>
		public required int CompressedSize { get; init; }
		/// <summary>Size of uncompressed chunk data.</summary>
		public required int UncompressedSize { get; init; }
		/// <summary>Adler checksum of chunk data.</summary>
		public required uint Checksum { get; init; }
		/// <summary>Offset of chunk data from the beginning of containing file.</summary>
		public required long FileOffset { get; init; }
		/// <summary>Path to the file to download chunk to.</summary>
		public required string FilePath { get; init; }
		/// <summary>Handle for the file to download chunk to.</summary>
		public required LimitedUseFileHandle FileHandle { get; init; }
		/// <summary>GID of the chunk.</summary>
		public required SHA1Hash Gid { get; init; }
		/// <summary>Writes context data to a buffer.</summary>
		/// <param name="buffer">Buffer to write data to.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/> to write context data to.</param>
		/// <param name="pathOffset">Offset into <paramref name="buffer"/> to write file path to.</param>
		public void WriteToBuffer(Span<byte> buffer, ref nint offset, ref int pathOffset)
		{
			ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
			nint entryOffset = offset;
			Unsafe.As<byte, SHA1Hash>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset)) = Gid;
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 24)) = CompressedSize;
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 28)) = UncompressedSize;
			Unsafe.As<byte, uint>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 32)) = Checksum;
			int pathLength = Encoding.UTF8.GetBytes(FilePath, buffer[pathOffset..]);
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 36)) = pathLength;
			pathOffset += pathLength;
			Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 40)) = FileOffset;
			offset += 48;
		}
		/// <summary>Loads context data from a buffer.</summary>
		/// <param name="buffer">Buffer to read data from.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/> to read context data from.</param>
		/// <param name="pathOffset">Offset into <paramref name="buffer"/> to read file path from.</param>
		/// <returns>Loaded chunk context.</returns>
		public static ChunkContext LoadFromBuffer(ReadOnlySpan<byte> buffer, ref nint offset, ref int pathOffset)
		{
			ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
			nint entryOffset = offset;
			var gid = new SHA1Hash(buffer.Slice(offset.ToInt32(), 20));
			int compressedSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 24));
			int uncompressedSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 28));
			uint checksum = Unsafe.As<byte, uint>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 32));
			int pathLength = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 36));
			string filePath = Encoding.UTF8.GetString(buffer.Slice(pathOffset, pathLength));
			pathOffset += pathLength;
			long fileOffset = Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 40));
			offset += 48;
			return new()
			{
				CompressedSize = compressedSize,
				UncompressedSize = uncompressedSize,
				Checksum = checksum,
				FileOffset = fileOffset,
				FilePath = filePath,
				FileHandle = new(File.OpenHandle(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.RandomAccess | FileOptions.Asynchronous), 1),
				Gid = gid
			};
		}
	}
	/// <summary>File handle wrapper that releases the handle after the last chunk has been written to the file.</summary>
	/// <param name="fileHandle">File handle.</param>
	/// <param name="numChunks">The number of chunks that will be written to the file.</param>
	private class LimitedUseFileHandle(SafeFileHandle fileHandle, int numChunks)
	{
		/// <summary>The number of chunks left to be written to the file.</summary>
		public int ChunksLeft { get; private set; } = numChunks;
		/// <summary>File handle.</summary>
		public SafeFileHandle Handle { get; } = fileHandle;
		/// <summary>Decrements the number of chunks left to write and releases the handle if it becomes zero.</summary>
		public void Release()
		{
			if (--ChunksLeft is 0)
				Handle.Dispose();
		}
	}
	/// <summary>Shared context for download threads.</summary>
	private class DownloadContext
	{
		/// <summary>Initializes a download context for given delta and state and sets up index stack.</summary>
		public DownloadContext(ItemState state, DepotManifest manifest, DepotDelta delta, string basePath, ProgressUpdatedHandler? handler)
		{
			_manifest = manifest;
			_state = state;
			static int getDirTreeDepth(in DirectoryEntry.AcquisitionEntry dir)
			{
				int maxChildDepth = 0;
				foreach (var subdir in dir.Subdirectories)
				{
					int childDepth = getDirTreeDepth(in subdir);
					if (childDepth > maxChildDepth)
						maxChildDepth = childDepth;
				}
				return 1 + maxChildDepth;
			}
			int dirTreeDepth = getDirTreeDepth(in delta.AcquisitionTree);
			_pathTree = new string[dirTreeDepth + 1];
			_pathTree[0] = Path.Join(basePath, state.Id.ToString());
			_currentDirTree = new DirectoryEntry.AcquisitionEntry[dirTreeDepth];
			_currentDirTree[0] = delta.AcquisitionTree;
			var indexStack = state.ProgressIndexStack;
			if (indexStack.Count is 0)
			{
				bool findFirstChunk(in DirectoryEntry.AcquisitionEntry dir)
				{
					var files = dir.Files;
					for (int i = 0; i < files.Count; i++)
					{
						var file = files[i];
						if (file.Chunks.Count > 0 || manifest.FileBuffer[file.Index].Chunks.Count > 0)
						{
							state.ProgressIndexStack.Add(i);
							state.ProgressIndexStack.Add(0);
							return true;
						}
					}
					int stackIndex = state.ProgressIndexStack.Count;
					state.ProgressIndexStack.Add(0);
					var subdirs = dir.Subdirectories;
					for (int i = 0; i < subdirs.Count; i++)
						if (findFirstChunk(subdirs[i]))
						{
							state.ProgressIndexStack[stackIndex] = i;
							return true;
						}
					state.ProgressIndexStack.RemoveAt(stackIndex);
					return false;
				}
				findFirstChunk(in _currentDirTree[0]);
			}
			for (int i = 0; i < state.ProgressIndexStack.Count - 2; i++)
			{
				_currentDirTree[i + 1] = _currentDirTree[i].Subdirectories[state.ProgressIndexStack[i]];
				_pathTree[i + 1] = manifest.DirectoryBuffer[_currentDirTree[i + 1].Index].Name;
			}
			_currentFile = _currentDirTree[state.ProgressIndexStack.Count - 2].Files[state.ProgressIndexStack[^2]];
			_pathTree[state.ProgressIndexStack.Count - 1] = manifest.FileBuffer[_currentFile.Index].Name;
			if (delta.ChunkBufferFileSize > 0)
			{
				_chunkBufferFilePath = Path.Join(basePath, $"{state.Id}.scchunkbuffer");
				ChunkBufferFileHandle = new(File.OpenHandle(_chunkBufferFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, options: FileOptions.RandomAccess | FileOptions.Asynchronous), int.MaxValue);
			}
			if (state.ProgressIndexStack[^1] > 0)
			{
				if (_currentFile.Chunks.Count is 0)
				{
					_currentFilePath = Path.Join(_pathTree);
					CurrentFileHandle = new(File.OpenHandle(_currentFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.RandomAccess | FileOptions.Asynchronous),
						(_currentFile.Chunks.Count is 0 ? manifest.FileBuffer[_currentFile.Index].Chunks.Count : _currentFile.Chunks.Count) - state.ProgressIndexStack[^1]);
				}
				else
				{
					_currentFilePath = _chunkBufferFilePath!;
					CurrentFileHandle = ChunkBufferFileHandle!;
				}
			}
			_progressUpdatedHandler = handler;
		}
		/// <summary>Path to the currently selected file.</summary>
		private string? _currentFilePath;
		/// <summary>Entry for the currently selected file.</summary>
		private FileEntry.AcquisitionEntry _currentFile;
		/// <summary>Path to the chunk buffer file.</summary>
		private readonly string? _chunkBufferFilePath;
		/// <summary>Array of directory and file names to compose path from.</summary>
		private readonly string?[] _pathTree;
		/// <summary>Path to the chunk buffer file.</summary>
		private readonly DepotManifest _manifest;
		/// <summary>Path to the chunk buffer file.</summary>
		private readonly DirectoryEntry.AcquisitionEntry[] _currentDirTree;
		/// <summary>Item state.</summary>
		private readonly ItemState _state;
		/// <summary>Called when progress value is updated.</summary>
		private readonly ProgressUpdatedHandler? _progressUpdatedHandler;
		/// <summary>Gets item depot ID.</summary>
		public uint DepotId => _state.Id.DepotId;
		/// <summary>Exception thrown by one of the download threads.</summary>
		public Exception? Exception { get; set; }
		/// <summary>HTTP clients for all CDN servers.</summary>
		public required HttpClient[] HttpClients { get; init; }
		/// <summary>Handle for the chunk buffer file.</summary>
		public LimitedUseFileHandle? ChunkBufferFileHandle { get; }
		/// <summary>Handle for the currently selected file.</summary>
		public LimitedUseFileHandle? CurrentFileHandle { get; private set; }
		/// <summary>Submits progress for the previous chunk, gets context for the next chunk or <see langword="default"/> if the are no more chunks and moves index stack to the next chunk.</summary>
		public ChunkContext GetNextChunk(long previousChunkSize)
		{
			var indexStack = _state.ProgressIndexStack;
			lock (this)
			{
				if (previousChunkSize > 0)
				{
					_state.DisplayProgress += previousChunkSize;
					_progressUpdatedHandler?.Invoke(_state.DisplayProgress);
				}
				if (indexStack.Count is 0)
					return default;
				int chunkIndex = indexStack[^1];
				if (chunkIndex is 0)
				{
					if (_currentFile.Chunks.Count is 0)
					{
						_currentFilePath = Path.Join(_pathTree);
						CurrentFileHandle = new(File.OpenHandle(_currentFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.RandomAccess | FileOptions.Asynchronous), _manifest.FileBuffer[_currentFile.Index].Chunks.Count);
					}
					else
					{
						_currentFilePath = _chunkBufferFilePath!;
						CurrentFileHandle = ChunkBufferFileHandle!;
					}
				}
				ChunkEntry chunk;
				long chunkBufferOffset;
				int numChunks;
				if (_currentFile.Chunks.Count is 0)
				{
					var chunks = _manifest.FileBuffer[_currentFile.Index].Chunks;
					chunk = chunks[chunkIndex];
					chunkBufferOffset = -1;
					numChunks = chunks.Count;
				}
				else
				{
					var acquisitionChunk = _currentFile.Chunks[chunkIndex];
					chunk = _manifest.ChunkBuffer[acquisitionChunk.Index];
					chunkBufferOffset = acquisitionChunk.Offset;
					numChunks = _currentFile.Chunks.Count;
				}
				if (++chunkIndex == numChunks)
				{
					bool contextRestored = false;
					int lastDirLevel = indexStack.Count - 2;
					bool findNextChunk(in DirectoryEntry.AcquisitionEntry dir, int recursionLevel)
					{
						if (contextRestored || recursionLevel == lastDirLevel)
						{
							var files = dir.Files;
							for (int i = contextRestored ? 0 : indexStack[recursionLevel] + 1; i < files.Count; i++)
							{
								var file = files[i];
								if (file.Chunks.Count > 0 || _manifest.FileBuffer[file.Index].Chunks.Count > 0)
								{
									if (contextRestored)
									{
										indexStack.Add(i);
										indexStack.Add(0);
									}
									else
									{
										indexStack[recursionLevel] = i;
										indexStack[recursionLevel + 1] = 0;
									}
									return true;
								}
							}
							if (!contextRestored)
							{
								indexStack.RemoveRange(recursionLevel, 2);
								contextRestored = true;
							}
						}
						if (contextRestored)
							indexStack.Add(0);
						var subdirs = dir.Subdirectories;
						for (int i = contextRestored ? 0 : indexStack[recursionLevel]; i < subdirs.Count; i++)
							if (findNextChunk(subdirs[i], recursionLevel + 1))
							{
								indexStack[recursionLevel] = i;
								return true;
							}
						indexStack.RemoveAt(recursionLevel);
						return false;
					}
					if (!findNextChunk(in _currentDirTree[0], 0))
						indexStack.Clear();
					for (int i = 0; i < indexStack.Count - 2; i++)
					{
						_currentDirTree[i + 1] = _currentDirTree[i].Subdirectories[indexStack[i]];
						_pathTree[i + 1] = _manifest.DirectoryBuffer[_currentDirTree[i + 1].Index].Name;
					}
					_currentFile = _currentDirTree[indexStack.Count - 2].Files[indexStack[^2]];
					_pathTree[indexStack.Count - 1] = _manifest.FileBuffer[_currentFile.Index].Name;
					for (int i = indexStack.Count; i < _pathTree.Length; i++)
						_pathTree[i] = null;
				}
				else
					indexStack[^1] = chunkIndex;
				return new()
				{
					CompressedSize = chunk.CompressedSize,
					UncompressedSize = chunk.UncompressedSize,
					Checksum = chunk.Checksum,
					FileOffset = chunkBufferOffset >= 0 ? chunkBufferOffset : chunk.Offset,
					FilePath = _currentFilePath!,
					FileHandle = CurrentFileHandle!,
					Gid = chunk.Gid
				};
			}
		}
	}
	/// <summary>Individual context for download threads.</summary>
	private class DownloadThreadContext
	{
		/// <summary>Thread index, used to select download server.</summary>
		public required int Index { get; init; }
		/// <summary>Cancellation token source for all download threads.</summary>
		public required CancellationTokenSource Cts { get; init; }
		/// <summary>Current chunk context.</summary>
		public ChunkContext ChunkContext { get; set; }
		/// <summary>Shared download context.</summary>
		public required DownloadContext SharedContext { get; init; }
	}
}