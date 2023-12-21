using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TEKSteamClient.Manifest;

/// <summary>Validation progress storage.</summary>
internal class ValidationCache
{
	/// <summary>Creates a new empty validation cache object.</summary>
	public ValidationCache() => AcquisitionTree = new(0, false);
	/// <summary>Creates a new validation cache object by reading an .scvcache file.</summary>
	/// <param name="filePath">Path to the validation cache file.</param>
	/// <exception cref="SteamException">The file is corrupted (hash mismatch).</exception>
	public ValidationCache(string filePath)
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
			throw new SteamException(SteamException.ErrorType.ValidationCacheCorrupted);
		}
		FilesMatching = buffer[1];
		FilesMismatching = buffer[2];
		FilesMissing = buffer[3];
		int dataOffset = 4;
		AcquisitionTree = new(buffer, ref dataOffset);
	}
	/// <summary>Matching files counter.</summary>
	public int FilesMatching { get; set; }
	/// <summary>Mismatching files counter.</summary>
	public int FilesMismatching { get; set; }
	/// <summary>Missing files counter.</summary>
	public int FilesMissing { get; set; }
	/// <summary>Tree of items that must be acquired.</summary>
	public DirectoryEntry.AcquisitionStaging AcquisitionTree { get; }
	/// <summary>Writes current validation progress to a file.</summary>
	/// <param name="filePath">Path to the validation cache file.</param>
	public void WriteToFile(string filePath)
	{
		static int calculateSize(DirectoryEntry.AcquisitionStaging directory)
		{
			int result = 4 + directory.Files.Count * 2;
			foreach (var file in directory.Files)
				result += file.Chunks.Count;
			foreach (var subdir in directory.Subdirectories)
				result += calculateSize(subdir);
			return result;
		}
		int totalSize = calculateSize(AcquisitionTree);
		Span<int> buffer = GC.AllocateUninitializedArray<int>(4 + totalSize);
		buffer[1] = FilesMatching;
		buffer[2] = FilesMismatching;
		buffer[3] = FilesMissing;
		int dataOffset = 4;
		AcquisitionTree.WriteToBuffer(buffer, ref dataOffset);
		Span<byte> byteSpan = MemoryMarshal.AsBytes(buffer);
		XxHash32.Hash(byteSpan[4..], byteSpan);
		using var fileHandle = File.OpenHandle(filePath, FileMode.Create, FileAccess.Write, preallocationSize: byteSpan.Length);
		RandomAccess.Write(fileHandle, byteSpan, 0);
	}
}