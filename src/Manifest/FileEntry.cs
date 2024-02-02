using System.Runtime.CompilerServices;

namespace TEKSteamClient.Manifest;

/// <summary>Steam depot manifest file record.</summary>
public readonly struct FileEntry
{
	/// <summary>Name of the file.</summary>
	public required string Name { get; init; }
	/// <summary>Total size of the file OR'ed with its flags. Used only during initialization.</summary>
	public required long SizeAndFlags { get; init; }
	/// <summary>Total size of the file.</summary>
	public long Size => SizeAndFlags & 0x00FFFFFFFFFFFFFF;
	/// <summary>Extra flags of the file.</summary>
	public Flag Flags => (Flag)((SizeAndFlags & 0x7F00000000000000) >> 56);
	/// <summary>Chunks that compose the file.</summary>
	public required ArraySegment<ChunkEntry> Chunks { get; init; }
	/// <summary>Extra flags describing file's attributes or permissions.</summary>
	[Flags]
	public enum Flag
	{
		/// <summary>The file has <see cref="FileAttributes.ReadOnly"/> attribute.</summary>
		ReadOnly = 1 << 0,
		/// <summary>The file has <see cref="FileAttributes.Hidden"/> attribute.</summary>
		Hidden = 1 << 1,
		/// <summary>The file has <see cref="UnixFileMode.UserExecute"/>, <see cref="UnixFileMode.GroupExecute"/> and <see cref="UnixFileMode.OtherExecute"/> permissions.</summary>
		Executable = 1 << 2
	}
	/// <summary>Structure used in <see cref="DepotDelta"/> to store indexes of files and chunks that must be acquired.</summary>
	internal readonly struct AcquisitionEntry
	{
		/// <summary>Index of the file entry in <see cref="DepotManifest.FileBuffer"/>.</summary>
		public required int Index { get; init; }
		/// <summary>File's chunks that must be acquired. If empty, the whole file is acquired.</summary>
		public required ArraySegment<ChunkEntry> Chunks { get; init; }
		/// <summary>Structure storing chunk's index along with its optional offset in chunk buffer file.</summary>
		/// <param name="index">Index of the chunk in its file.</param>
		public readonly struct ChunkEntry(int index)
		{
			/// <summary>Index of the chunk entry in <see cref="DepotManifest.ChunkBuffer"/>.</summary>
			public int Index { get; } = index;
			/// <summary>Offset of the chunk data from the beginning of chunk buffer file.</summary>
			public long Offset { get; init; }
		}
	}
	/// <summary>Structure used in <see cref="DepotDelta"/> to store auxiliary data like patched chunks and relocations.</summary>
	internal readonly struct AuxiliaryEntry
	{
		/// <summary>Index of the file entry in <see cref="DepotManifest.FileBuffer"/>.</summary>
		public required int Index { get; init; }
		/// <summary>File's chunk patch and relocation entries.</summary>
		public ArraySegment<ITransferOperation> TransferOperations { get; init; }
		/// <summary>Represents an operation that takes data from certain region of a file and writes data to another region of a file.</summary>
		public interface ITransferOperation { }
		/// <summary>Contains data that is needed to patch a chunk.</summary>
		public class ChunkPatchEntry : ITransferOperation
		{
			/// <summary>Index of target chunk entry in <see cref="DepotManifest.ChunkBuffer"/>.</summary>
			public required int ChunkIndex { get; init; }
			/// <summary>Index of the corresponding patch chunk.</summary>
			public required int PatchChunkIndex { get; init; }
			/// <summary>Offset of chunk data within the intermediate file, if negative the intermediate file is not used.</summary>
			public required long IntermediateFileOffset { get; init; }
		}
		/// <summary>Describes data that needs to be moved within the file.</summary>
		public class RelocationEntry : ITransferOperation
		{
			/// <summary>Source offset of data bulk within the file.</summary>
			public required long SourceOffset { get; init; }
			/// <summary>Target offset of data bulk within the file.</summary>
			public required long TargetOffset { get; init; }
			/// <summary>Offset of data bulk within the intermediate file, if negative the intermediate file is not used.</summary>
			public required long IntermediateFileOffset { get; init; }
			/// <summary>Size of data bulk.</summary>
			public required int Size { get; init; }
		}
	}
	/// <summary>Mutable and reference-type variant of <see cref="AcquisitionEntry"/> used during validation and update delta computation.</summary>
	internal class AcquisitionStaging
	{
		/// <summary>Creates an empty staging file entry with specified index.</summary>
		/// <param name="index">Index of the file entry in its parent directory.</param>
		public AcquisitionStaging(int index)
		{
			Index = index;
			Chunks = [];
		}
		/// <summary>Creates a staging file entry by reading its data from validation cache buffer.</summary>
		/// <param name="buffer">Validation cache buffer.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/>, pointing to the beginning of file entry.</param>
		public AcquisitionStaging(ReadOnlySpan<int> buffer, ref int offset)
		{
			Index = buffer[offset++];
			int numChunks = buffer[offset++];
			Chunks = new(numChunks);
			for (int i = 0; i < numChunks; i++)
				Chunks.Add(new(buffer[offset++]));
		}
		/// <summary>Index of the file entry in <see cref="DepotManifest.FileBuffer"/>.</summary>
		public int Index { get; }
		/// <summary>File's chunks that must be acquired. If empty, the whole file is acquired.</summary>
		public List<AcquisitionEntry.ChunkEntry> Chunks { get; }
		/// <summary>Writes entry data to the validation cache buffer.</summary>
		/// <param name="buffer">Validation cache buffer.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/> to write to.</param>
		public void WriteToBuffer(Span<int> buffer, ref int offset)
		{
			buffer[offset++] = Index;
			buffer[offset++] = Chunks.Count;
			foreach (var chunk in Chunks)
				buffer[offset++] = chunk.Index;
		}
	}
	/// <summary>Mutable and reference-type variant of <see cref="AuxiliaryEntry"/> used during update delta computation.</summary>
	/// <param name="index">Index of the file entry in its parent directory.</param>
	internal class AuxiliaryStaging(int index)
	{
		/// <summary>Index of the file entry in <see cref="DepotManifest.FileBuffer"/>.</summary>
		public int Index { get; } = index;
		/// <summary>File's chunk patch entries.</summary>
		public List<ChunkPatchEntry> ChunkPatches { get; } = [];
		/// <summary>File's relocation entries.</summary>
		public List<RelocationEntry> Relocations { get; } = [];
		/// <summary>File's transfer operation entries.</summary>
		public List<ITransferOperation> TransferOperations { get; } = [];
		/// <summary>Represents an operation that takes data from certain region of a file and writes data to another region of a file.</summary>
		public interface ITransferOperation { }
		/// <summary>Contains data that is needed to patch a chunk.</summary>
		public class ChunkPatchEntry : ITransferOperation
		{
			/// <summary>Indicates whether intermediate file needs to be used as a buffer to avoid overlapping further chunks.</summary>
			public bool UseIntermediateFile { get; set; }
			/// <summary>Index of target chunk entry in <see cref="DepotManifest.ChunkBuffer"/>.</summary>
			public required int ChunkIndex { get; init; }
			/// <summary>Index of the corresponding patch chunk.</summary>
			public required int PatchChunkIndex { get; init; }
			/// <summary>Size of either source chunk or patched chunk data, whichever is smaller.</summary>
			public required long Size { get; init; }
		}
		/// <summary>Describes data that needs to be moved within the file.</summary>
		public class RelocationEntry : ITransferOperation, IComparable<RelocationEntry>
		{
			/// <summary>Indicates whether intermediate file needs to be used as a buffer to avoid overlapping further relocations and chunk patches.</summary>
			public bool UseIntermediateFile { get; set; }
			/// <summary>Source offset of data bulk within the file.</summary>
			public required long SourceOffset { get; init; }
			/// <summary>Target offset of data bulk within the file.</summary>
			public required long TargetOffset { get; init; }
			/// <summary>Size of data bulk.</summary>
			public required long Size { get; set; }
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public int CompareTo(RelocationEntry? other) => other is null ? 1 : SourceOffset.CompareTo(other.SourceOffset);
		}
		/// <summary>Represents an operation that takes data from certain region of a file and writes data to another region of a file.</summary>
		public class TransferOperation : IComparable<TransferOperation>
		{
			/// <summary>Number of other operations' target regions intersections with this operation's source region. Used for ordering entries.</summary>
			public int Weight { get; set; }
			/// <summary>Offset of the beginning of source region.</summary>
			public required long SourceStart { get; init; }
			/// <summary>Offset of the end of source region.</summary>
			public required long SourceEnd { get; init; }
			/// <summary>Offset of the beginning of target region.</summary>
			public required long TargetStart { get; init; }
			/// <summary>Offset of the end of target region.</summary>
			public required long TargetEnd { get; init; }
			/// <summary>Object that implements the operation.</summary>
			public required ITransferOperation Object { get; init; }
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public int CompareTo(TransferOperation? other) => other is null ? 1 : other.Weight.CompareTo(Weight);
		}
	}
}