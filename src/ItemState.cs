using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TEKSteamClient;

/// <summary>Describes current state of a Steam item.</summary>
public class ItemState
{
	/// <summary>Creates a new item state object with specified backing file. If file exists, it is read, otherwise properties are initialized with default values.</summary>
	/// <param name="id">ID of the item.</param>
	/// <param name="filePath">Path to the file storing item state.</param>
	public ItemState(ItemIdentifier id, string filePath)
	{
		_filePath = filePath;
		Id = id;
		if (!File.Exists(filePath))
		{
			ProgressIndexStack = [];
			return;
		}
		byte[] buffer;
		using (var fileHandle = File.OpenHandle(filePath))
		{
			buffer = new byte[(int)RandomAccess.GetLength(fileHandle)];
			RandomAccess.Read(fileHandle, buffer, 0);
		}
		ref byte bufferRef = ref MemoryMarshal.GetArrayDataReference(buffer);
		CurrentManifestId = Unsafe.As<byte, ulong>(ref bufferRef);
		Status = Unsafe.As<byte, ItemStatus>(ref Unsafe.AddByteOffset(ref bufferRef, 8));
		if (Status > ItemStatus.UpdateAvailable)
		{
			int numIndexes = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 12));
			ProgressIndexStack = new(numIndexes);
			DisplayProgress = Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, 16));
			ProgressIndexStack.AddRange(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 24)), numIndexes));
		}
		else
			ProgressIndexStack = [];
	}
	/// <summary>Path to the file backing this object.</summary>
	private readonly string _filePath;
	/// <summary>When <see cref="Status"/> is not <see cref="ItemStatus.Installed"/> or <see cref="ItemStatus.UpdateAvailable"/>, stores the progress value to be displayed to user.</summary>
	public long DisplayProgress { get; internal set; }
	/// <summary>ID of manifest describing currently installed version, 0 if unknown.</summary>
	public ulong CurrentManifestId { get; internal set; }
	/// <summary>Current status of the item.</summary>
	public ItemStatus Status { get; internal set; }
	/// <summary>ID of the item.</summary>
	public ItemIdentifier Id { get; }
	/// <summary>When <see cref="Status"/> is not <see cref="ItemStatus.Installed"/> or <see cref="ItemStatus.UpdateAvailable"/>, stores the progress of operation's tree walking.</summary>
	public List<int> ProgressIndexStack { get; }
	/// <summary>Saves current state to file.</summary>
	public void SaveToFile()
	{
		Span<byte> buffer = stackalloc byte[Status > ItemStatus.UpdateAvailable ? 24 + ProgressIndexStack.Count * 4 : 12];
		ref byte bufferRef = ref MemoryMarshal.GetReference(buffer);
		Unsafe.As<byte, ulong>(ref bufferRef) = CurrentManifestId;
		Unsafe.As<byte, ItemStatus>(ref Unsafe.AddByteOffset(ref bufferRef, 8)) = Status;
		if (Status > ItemStatus.UpdateAvailable)
		{
			Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 12)) = ProgressIndexStack.Count;
			Unsafe.As<byte, long>(ref Unsafe.AddByteOffset(ref bufferRef, 16)) = DisplayProgress;
			ProgressIndexStack.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 24)), ProgressIndexStack.Count));
		}
		using var fileHandle = File.OpenHandle(_filePath, FileMode.Create, FileAccess.Write, preallocationSize: buffer.Length);
		RandomAccess.Write(fileHandle, buffer, 0);
	}
	/// <summary>Describes status of an item.</summary>
	public enum ItemStatus
	{
		/// <summary>Item is corrupted and should be validated ASAP.</summary>
		Corrupted,
		/// <summary>Item is installed and up to date.</summary>
		Installed,
		/// <summary>Item update is available.</summary>
		UpdateAvailable,
		/// <summary>Item installation is being validated.</summary>
		Validating,
		/// <summary>Item's download cache is being preallocated.</summary>
		Preallocating,
		/// <summary>Item files are downloading.</summary>
		Downloading,
		/// <summary>Item is being patched.</summary>
		Patching,
		/// <summary>New data for item is being written to its installation.</summary>
		WritingNewData,
		/// <summary>Item's old files are being removed.</summary>
		RemovingOldFiles
	}
}