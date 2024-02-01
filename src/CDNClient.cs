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
	private Uri[] _servers = [];
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
	/// Number of servers that clients will simultaneously use when downloading depot content. The default value is <see cref="Environment.ProcessorCount"/>.
	/// The product of <see cref="NumDownloadServers"/> and <see cref="NumRequestsPerServer"/> is the number of simultaneous download tasks, scale it
	/// accordingly to your network bandwidth and CPU capabilities.
	/// </summary>
	public static int NumDownloadServers { get; set; } = Environment.ProcessorCount;
	/// <summary>
	/// Number of simultaneous download tasks created per server. The default value is 4.
	/// The product of <see cref="NumDownloadServers"/> and <see cref="NumRequestsPerServer"/> is the number of simultaneous download tasks, scale it
	/// accordingly to your network bandwidth and CPU capabilities.
	/// </summary>
	public static int NumRequestsPerServer { get; set; } = 4;
	/// <summary>Gets CDN server list if necessary.</summary>
	private void CheckServerList()
	{
		if (_servers.Length >= NumDownloadServers)
			return;
		var servers = new List<CDNServersResponse.Types.Server>(NumDownloadServers);
		while (servers.Count < NumDownloadServers)
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
		_servers = new Uri[servers.Count];
		for (int i = 0; i < servers.Count; i++)
			_servers[i] = new(string.Concat("https://", servers[i].Host));
	}
	/// <summary>Downloads, decrypts, decompresses and writes chunk specified in the context.</summary>
	/// <param name="arg">An <see cref="AcquisitionTaskContext"/> object.</param>
	private static async Task AcquireChunk(object? arg)
	{
		var context = (AcquisitionTaskContext)arg!;
		byte[] buffer = context.Buffer;
		int compressedSize = context.CompressedSize;
		int uncompressedSize = context.UncompressedSize;
		var cancellationToken = context.CancellationToken;
		var aes = context.Aes;
		Exception? exception = null;
		var progress = context.Progress;
		var downloadBuffer = new Memory<byte>(buffer, 0, 0x200000);
		for (int i = 0; i < 5; i++) //5 attempts, after which task fails
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				//Download encrypted chunk data
				var request = new HttpRequestMessage(HttpMethod.Get, context.RequestUri) { Version = HttpVersion.Version20 };
				using var response = await context.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
				using var content = response.EnsureSuccessStatusCode().Content;
				using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				int bytesRead;
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				cts.CancelAfter(60000);
				try { bytesRead = await stream.ReadAtLeastAsync(downloadBuffer, compressedSize, false, cts.Token).ConfigureAwait(false); }
				catch (OperationCanceledException oce)
				{
					if (oce.CancellationToken == cancellationToken)
						throw;
					throw new TimeoutException();
				}
				catch (AggregateException ae) when (ae.InnerException is OperationCanceledException oce)
				{
					if (oce.CancellationToken == cancellationToken)
						throw;
					throw new TimeoutException();
				}
				if (bytesRead != compressedSize)
				{
					exception = new InvalidDataException($"Downloaded chunk data size doesn't match expected [URL: {context.HttpClient.BaseAddress}/{request.RequestUri}]");
					continue;
				}
				//Decrypt the data
				aes.DecryptEcb(new ReadOnlySpan<byte>(buffer, 0, 16), new Span<byte>(buffer, 0x3FFFF0, 16), PaddingMode.None);
				int decryptedDataSize = aes.DecryptCbc(new ReadOnlySpan<byte>(buffer, 16, compressedSize - 16), new ReadOnlySpan<byte>(buffer, 0x3FFFF0, 16), new Span<byte>(buffer, 0x200000, 0x1FFFF0));
				//Decompress the data
				if (!context.LzmaDecoder.Decode(new ReadOnlySpan<byte>(buffer, 0x200000, decryptedDataSize), new Span<byte>(buffer, 0, uncompressedSize)))
				{
					exception = new InvalidDataException("LZMA decoding failed");
					continue;
				}
				if (Adler.ComputeChecksum(new ReadOnlySpan<byte>(buffer, 0, uncompressedSize)) != context.Checksum)
				{
					exception = new InvalidDataException("Adler checksum mismatch");
					continue;
				}
				exception = null;
			}
			catch (OperationCanceledException) { throw; }
			catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { throw ae.InnerException; }
			catch (Exception e)
			{
				exception = e;
				continue;
			}
		}
		if (exception is not null)
			throw exception;
		//Write acquired chunk data to the file
		var handle = context.FileHandle;
		await RandomAccess.WriteAsync(handle.Handle, new ReadOnlyMemory<byte>(buffer, 0, uncompressedSize), context.FileOffset, cancellationToken).ConfigureAwait(false);
		handle.Release();
		progress.SubmitChunk(compressedSize);
	}
	/// <summary>Downloads depot content chunks.</summary>
	/// <param name="state">State of the item.</param>
	/// <param name="manifest">The target manifest.</param>
	/// <param name="delta">Delta object that lists data to be downloaded.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	internal void DownloadContent(ItemState state, DepotManifest manifest, DepotDelta delta, CancellationToken cancellationToken)
	{
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var contexts = new AcquisitionTaskContext[NumDownloadServers * NumRequestsPerServer];
		var tasks = new Task?[contexts.Length];
		int numResumedContexts = 0;
		Exception? exception = null;
		string? chunkBufferFilePath = null;
		LimitedUseFileHandle? chunkBufferFileHandle = null;
		string baseRequestUrl = $"depot/{state.Id.DepotId}/chunk/";
		void downloadDir(in DirectoryEntry.AcquisitionEntry dir, string path, int recursionLevel)
		{
			int index;
			if (state.ProgressIndexStack.Count > recursionLevel)
				index = state.ProgressIndexStack[recursionLevel];
			else
			{
				state.ProgressIndexStack.Add(0);
				index = 0;
			}
			for (; index < dir.Files.Count; index++)
			{
				var acquisitonFile = dir.Files[index];
				var file = manifest.FileBuffer[acquisitonFile.Index];
				if (file.Size is 0)
					continue;
				if (linkedCts.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = index;
					return;
				}
				int chunkRecLevel = recursionLevel + 1;
				int chunkIndex;
				if (state.ProgressIndexStack.Count > chunkRecLevel)
					chunkIndex = state.ProgressIndexStack[chunkRecLevel];
				else
				{
					state.ProgressIndexStack.Add(0);
					chunkIndex = 0;
				}
				if (acquisitonFile.Chunks.Count is 0)
				{
					string filePath = Path.Join(path, file.Name);
					var chunks = file.Chunks;
					LimitedUseFileHandle? handle;
					if (numResumedContexts > 0)
					{
						handle = null;
						for (int i = 0; i < numResumedContexts; i++)
							if (contexts[i].FilePath == filePath)
							{
								handle = contexts[i].FileHandle;
								break;
							}
						numResumedContexts = 0;
						handle ??= new(File.OpenHandle(filePath, FileMode.OpenOrCreate, FileAccess.Write, options: FileOptions.RandomAccess | FileOptions.Asynchronous), chunks.Count);
					}
					else
						handle = new(File.OpenHandle(filePath, FileMode.OpenOrCreate, FileAccess.Write, options: FileOptions.RandomAccess | FileOptions.Asynchronous), chunks.Count);
					for (; chunkIndex < chunks.Count; chunkIndex++)
					{
						if (linkedCts.IsCancellationRequested)
						{
							state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
							state.ProgressIndexStack[recursionLevel] = index;
							return;
						}
						var chunk = chunks[chunkIndex];
						int contextIndex = -1;
						for (int i = 0; i < contexts.Length; i++)
						{
							var task = tasks[i];
							if (task is null)
							{
								contextIndex = i;
								break;
							}
							if (task.IsCompleted)
							{
								if (task.IsFaulted)
								{
									exception = task.Exception;
									linkedCts.Cancel();
									state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
									state.ProgressIndexStack[recursionLevel] = index;
									return;
								}
								contextIndex = i;
								break;
							}
						}
						if (contextIndex < 0)
						{
							try { contextIndex = Task.WaitAny(tasks!, linkedCts.Token); }
							catch (OperationCanceledException)
							{
								state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
								state.ProgressIndexStack[recursionLevel] = index;
								return;
							}
							var task = tasks[contextIndex]!;
							if (task.IsFaulted)
							{
								exception = task.Exception;
								linkedCts.Cancel();
								state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
								state.ProgressIndexStack[recursionLevel] = index;
								return;
							}
						}
						var context = contexts[contextIndex];
						context.CompressedSize = chunk.CompressedSize;
						context.UncompressedSize = chunk.UncompressedSize;
						context.Checksum = chunk.Checksum;
						context.FileOffset = chunk.Offset;
						context.FilePath = filePath;
						context.RequestUri = new(string.Concat(baseRequestUrl, chunk.Gid.ToString()), UriKind.Relative);
						context.FileHandle = handle;
						tasks[contextIndex] = Task.Factory.StartNew(AcquireChunk, context, TaskCreationOptions.DenyChildAttach).Result;
					}
				}
				else
				{
					var acquisitionChunks = acquisitonFile.Chunks;
					for (; chunkIndex < acquisitionChunks.Count; chunkIndex++)
					{
						if (linkedCts.IsCancellationRequested)
						{
							state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
							state.ProgressIndexStack[recursionLevel] = index;
							return;
						}
						var acquisitionChunk = acquisitionChunks[chunkIndex];
						var chunk = manifest.ChunkBuffer[acquisitionChunk.Index];
						int contextIndex = -1;
						for (int i = 0; i < contexts.Length; i++)
						{
							var task = tasks[i];
							if (task is null)
							{
								contextIndex = i;
								break;
							}
							if (task.IsCompleted)
							{
								if (task.IsFaulted)
								{
									exception = task.Exception;
									linkedCts.Cancel();
									state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
									state.ProgressIndexStack[recursionLevel] = index;
									return;
								}
								contextIndex = i;
								break;
							}
						}
						if (contextIndex < 0)
						{
							try
							{ contextIndex = Task.WaitAny(tasks!, linkedCts.Token); }
							catch (OperationCanceledException)
							{
								state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
								state.ProgressIndexStack[recursionLevel] = index;
								return;
							}
							var task = tasks[contextIndex]!;
							if (task.IsFaulted)
							{
								exception = task.Exception;
								linkedCts.Cancel();
								state.ProgressIndexStack[chunkRecLevel] = chunkIndex;
								state.ProgressIndexStack[recursionLevel] = index;
								return;
							}
						}
						var context = contexts[contextIndex];
						context.CompressedSize = chunk.CompressedSize;
						context.UncompressedSize = chunk.UncompressedSize;
						context.Checksum = chunk.Checksum;
						context.FileOffset = acquisitionChunk.Offset;
						context.FilePath = chunkBufferFilePath!;
						context.RequestUri = new(string.Concat(baseRequestUrl, chunk.Gid.ToString()), UriKind.Relative);
						context.FileHandle = chunkBufferFileHandle!;
						tasks[contextIndex] = Task.Factory.StartNew(AcquireChunk, context, TaskCreationOptions.DenyChildAttach).Result;
					}
				}
				state.ProgressIndexStack.RemoveAt(chunkRecLevel);
			}
			index -= dir.Files.Count;
			for (; index < dir.Subdirectories.Count; index++)
			{
				var subdir = dir.Subdirectories[index];
				downloadDir(in subdir, Path.Join(path, manifest.DirectoryBuffer[subdir.Index].Name), recursionLevel + 1);
				if (linkedCts.IsCancellationRequested)
				{
					state.ProgressIndexStack[recursionLevel] = dir.Files.Count + index;
					return;
				}
			}
			state.ProgressIndexStack.RemoveAt(recursionLevel);
		}
		if (state.Status is not ItemState.ItemStatus.Downloading)
		{
			state.Status = ItemState.ItemStatus.Downloading;
			state.ProgressIndexStack.Clear();
			state.DisplayProgress = 0;
		}
		StatusUpdated?.Invoke(Status.Downloading);
		if (!DepotDecryptionKeys.TryGetValue(state.Id.DepotId, out var decryptionKey))
			throw new SteamException(SteamException.ErrorType.DepotDecryptionKeyMissing);
		CheckServerList();
		var threadSafeProgress = new ThreadSafeProgress(ProgressUpdated, state);
		var httpClients = new HttpClient[NumDownloadServers];
		for (int i = 0; i < httpClients.Length; i++)
			httpClients[i] = new()
			{
				BaseAddress = _servers[i],
				DefaultRequestVersion = HttpVersion.Version20,
				Timeout = TimeSpan.FromSeconds(10)
			};
		for (int i = 0; i < contexts.Length; i++)
		{
			contexts[i] = new(threadSafeProgress, httpClients[i % httpClients.Length], linkedCts.Token);
			contexts[i].Aes.Key = decryptionKey;
		}
		string dwContextsFilePath = Path.Join(DownloadsDirectory!, $"{state.Id}.scdwcontexts");
		ProgressInitiated?.Invoke(ProgressType.Binary, delta.DownloadSize, state.DisplayProgress);
		if (delta.ChunkBufferFileSize > 0)
		{
			chunkBufferFilePath = Path.Join(DownloadsDirectory!, $"{state.Id}.scchunkbuffer");
			chunkBufferFileHandle = new(File.OpenHandle(chunkBufferFilePath, FileMode.OpenOrCreate, FileAccess.Write, options: FileOptions.RandomAccess | FileOptions.Asynchronous), int.MaxValue);
		}
		if (File.Exists(dwContextsFilePath))
		{
			Span<byte> buffer;
			using (var fileHandle = File.OpenHandle(dwContextsFilePath))
			{
				buffer = GC.AllocateUninitializedArray<byte>((int)RandomAccess.GetLength(fileHandle));
				RandomAccess.Read(fileHandle, buffer, 0);
			}
			numResumedContexts = Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(buffer));
			nint offset = 8;
			int stringOffset = 8 + numResumedContexts * 32;
			for (int i = 0; i < numResumedContexts; i++)
			{
				int numChunks = contexts[i].LoadFromBuffer(buffer, ref offset, ref stringOffset);
				bool fileCreated = false;
				if (chunkBufferFilePath is not null && contexts[i].FilePath == chunkBufferFilePath)
				{
					contexts[i].FileHandle = chunkBufferFileHandle!;
					fileCreated = true;
				}
				else
					for (int j = 0; j < i; j++)
						if (contexts[j].FilePath == contexts[i].FilePath)
						{
							contexts[i].FileHandle = contexts[j].FileHandle;
							fileCreated = true;
							break;
						}
				if (!fileCreated)
					contexts[i].FileHandle = new(File.OpenHandle(contexts[i].FilePath, FileMode.OpenOrCreate, FileAccess.Write, options: FileOptions.RandomAccess | FileOptions.Asynchronous), numChunks);
			}
			for (int i = 0; i < numResumedContexts; i++)
				tasks[i] = Task.Factory.StartNew(AcquireChunk, contexts[i], TaskCreationOptions.DenyChildAttach);
		}
		downloadDir(in delta.AcquisitionTree, Path.Join(DownloadsDirectory!, state.Id.ToString()), 0);
		foreach (var task in tasks)
		{
			if (task is null)
				continue;
			if (!task.IsCompleted)
				Task.WaitAny([ task ], CancellationToken.None);
			if (task.IsFaulted)
				exception = task.Exception;
		}
		chunkBufferFileHandle?.Handle?.Dispose();
		foreach (var context in contexts)
			context.Dispose();
		if (linkedCts.IsCancellationRequested)
		{
			state.SaveToFile();
			int numContextsToSave = 0;
			int contextsFileSize = 0;
			for (int i = 0; i < contexts.Length; i++)
				if (!(tasks[i]?.IsCompletedSuccessfully ?? true))
				{
					var context = contexts[i];
					numContextsToSave++;
					contextsFileSize += 32 + Encoding.UTF8.GetByteCount(context.FilePath) + Encoding.UTF8.GetByteCount(context.FilePath);
				}
			if (numContextsToSave > 0)
			{
				Span<byte> buffer = new byte[contextsFileSize + 8];
				Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(buffer)) = numContextsToSave;
				nint offset = 8;
				int stringOffset = 8 + numContextsToSave * 32;
				for (int i = 0; i < contexts.Length; i++)
					if (!(tasks[i]?.IsCompletedSuccessfully ?? true))
						contexts[i].WriteToBuffer(buffer, ref offset, ref stringOffset);
				using var fileHandle = File.OpenHandle(dwContextsFilePath, FileMode.Create, FileAccess.Write, preallocationSize: buffer.Length);
				RandomAccess.Write(fileHandle, buffer, 0);
			}
			throw exception is null ? new OperationCanceledException(linkedCts.Token) : exception is SteamException ? exception : new SteamException(SteamException.ErrorType.DownloadFailed, exception);
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
				var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{server}depot/{item.DepotId}/manifest/{manifestId}/5/{requestCode}")) { Version = HttpVersion.Version20 };
				using var response = s_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).Result.EnsureSuccessStatusCode();
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
				var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{server}depot/{item.DepotId}/patch/{sourceManifest.Id}/{targetManifest.Id}")) { Version = HttpVersion.Version20 };
				using var response = s_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).Result.EnsureSuccessStatusCode();
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
	/// <summary>Persistent context for chunk acquisitions tasks.</summary>
	/// <param name="progress">Progress wrapper.</param>
	/// <param name="httpClient">HTTP client with base address set to server to download from.</param>
	/// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
	private class AcquisitionTaskContext(ThreadSafeProgress progress, HttpClient httpClient, CancellationToken cancellationToken) : IDisposable
	{
		/// <summary>Buffer for storing downloaded data and intermediate decrypted and decompressed data.</summary>
		public byte[] Buffer { get; } = GC.AllocateUninitializedArray<byte>(0x400000);
		/// <summary>Size of LZMA-compressed chunk data.</summary>
		public int CompressedSize { get; internal set; }
		/// <summary>Size of uncompressed chunk data. If -1, chunk won't be decompressed.</summary>
		public int UncompressedSize { get; internal set; }
		/// <summary>Adler checksum of chunk data.</summary>
		public uint Checksum { get; internal set; }
		/// <summary>Offset of chunk data from the beginning of containing file.</summary>
		public long FileOffset { get; internal set; }
		/// <summary>Path to the file to write chunk to.</summary>
		public string FilePath { get; internal set; } = string.Empty;
		/// <summary>AES decryptor.</summary>
		public Aes Aes { get; } = Aes.Create();
		/// <summary>Token to monitor for cancellation requests.</summary>
		public CancellationToken CancellationToken { get; } = cancellationToken;
		/// <summary>LZMA decoder.</summary>
		public Utils.LZMA.Decoder LzmaDecoder { get; } = new();
		/// <summary>HTTP client used to download chunk data.</summary>
		public HttpClient HttpClient { get; } = httpClient;
		/// <summary>Handle of the file to write chunk to.</summary>
		public LimitedUseFileHandle FileHandle { get; internal set; } = null!;
		/// <summary>Progress wrapper.</summary>
		public ThreadSafeProgress Progress { get; } = progress;
		/// <summary>Relative chunk URL.</summary>
		public Uri RequestUri { get; internal set; } = null!;
		public void Dispose()
		{
			Aes.Dispose();
			HttpClient.Dispose();
			FileHandle?.Handle?.Dispose();
		}
		/// <summary>Writes context data to a buffer.</summary>
		/// <param name="buffer">Buffer to write data to.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/> to write context data to.</param>
		/// <param name="stringOffset">Offset into <paramref name="buffer"/> to write UTF-8 encoded strings to.</param>
		public void WriteToBuffer(Span<byte> buffer, ref nint offset, ref int stringOffset)
		{
			ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
			nint entryOffset = offset;
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset)) = CompressedSize;
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 4)) = UncompressedSize;
			Unsafe.As<byte, uint>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 8)) = Checksum;
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 12)) = FileHandle.ChunksLeft;
			Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 16)) = FileOffset;
			int stringLength = Encoding.UTF8.GetBytes(FilePath, buffer[stringOffset..]);
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 24)) = stringLength;
			stringOffset += stringLength;
			stringLength = Encoding.UTF8.GetBytes(RequestUri.ToString(), buffer[stringOffset..]);
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 28)) = stringLength;
			stringOffset += stringLength;
			offset += 32;
		}
		/// <summary>Loads context data from a buffer.</summary>
		/// <param name="buffer">Buffer to read data from.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/> to read context data from.</param>
		/// <param name="stringOffset">Offset into <paramref name="buffer"/> to read UTF-8 encoded strings from.</param>
		public int LoadFromBuffer(ReadOnlySpan<byte> buffer, ref nint offset, ref int stringOffset)
		{
			ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
			nint entryOffset = offset;
			CompressedSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset));
			UncompressedSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 4));
			Checksum = Unsafe.As<byte, uint>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 8));
			int chunksLeft = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 12));
			FileOffset = Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 16));
			int stringLength = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 24));
			FilePath = Encoding.UTF8.GetString(buffer.Slice(stringOffset, stringLength));
			stringOffset += stringLength;
			stringLength = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, entryOffset + 28));
			RequestUri = new(Encoding.UTF8.GetString(buffer.Slice(stringOffset, stringLength)), UriKind.Relative);
			stringOffset += stringLength;
			offset += 32;
			return chunksLeft;
		}
	}
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
			_pathTree[0] = basePath;
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
				_chunkBufferFileHandle = new(File.OpenHandle(_chunkBufferFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, options: FileOptions.RandomAccess | FileOptions.Asynchronous), int.MaxValue);
			}
			if (state.ProgressIndexStack[^1] > 0)
			{
				if (_currentFile.Chunks.Count is 0)
				{
					_currentFilePath = Path.Join(_pathTree);
					_currentFileHandle = new(File.OpenHandle(_currentFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.RandomAccess | FileOptions.Asynchronous),
						(_currentFile.Chunks.Count is 0 ? manifest.FileBuffer[_currentFile.Index].Chunks.Count : _currentFile.Chunks.Count) - state.ProgressIndexStack[^1]);
				}
				else
				{
					_currentFilePath = _chunkBufferFilePath!;
					_currentFileHandle = _chunkBufferFileHandle!;
				}
			}
			_progressUpdatedHandler = handler;
		}
		/// <summary>Path to the currently selected file.</summary>
		private string? _currentFilePath;
		/// <summary>Entry for the currently selected file.</summary>
		private FileEntry.AcquisitionEntry _currentFile;
		/// <summary>Handle for the currently selected file.</summary>
		private LimitedUseFileHandle? _currentFileHandle;
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
		/// <summary>Handle for the chunk buffer file.</summary>
		private readonly LimitedUseFileHandle? _chunkBufferFileHandle;
		/// <summary>Called when progress value is updated.</summary>
		private readonly ProgressUpdatedHandler? _progressUpdatedHandler;
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
						_currentFileHandle = new(File.OpenHandle(_currentFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.RandomAccess | FileOptions.Asynchronous), _manifest.FileBuffer[_currentFile.Index].Chunks.Count);
					}
					else
					{
						_currentFilePath = _chunkBufferFilePath!;
						_currentFileHandle = _chunkBufferFileHandle!;
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
					FileHandle = _currentFileHandle!,
					Gid = chunk.Gid
				};
			}
		}
	}
	/// <summary>Thread-safe wrapper for updating progress value.</summary>
	/// <param name="handler">Event handler called when progress is updated.</param>
	/// <param name="state">Depot state object that holds progress value.</param>
	private class ThreadSafeProgress(ProgressUpdatedHandler? handler, ItemState state)
	{
		/// <summary>Updates progress value by adding chunk size to it.</summary>
		/// <param name="chunkSize">Size of LZMA-compressed chunk data.</param>
		public void SubmitChunk(int chunkSize)
		{
			lock (this)
			{
				state.DisplayProgress += chunkSize;
				handler?.Invoke(state.DisplayProgress);
			}
		}
	}
}