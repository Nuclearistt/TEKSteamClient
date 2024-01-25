namespace TEKSteamClient.Manifest;

/// <summary>Steam depot manifest directory record.</summary>
public readonly struct DirectoryEntry
{
	/// <summary>Name of the directory.</summary>
	public required string Name { get; init; }
	/// <summary>Files contained in the directory.</summary>
	public required ArraySegment<FileEntry> Files { get; init; }
	/// <summary>Subdirectories contained in the directory.</summary>
	public required ArraySegment<DirectoryEntry> Subdirectories { get; init; }
	/// <summary>Structure used in <see cref="DepotDelta"/> to store indexes of items that must be acquired.</summary>
	internal readonly struct AcquisitionEntry
	{
		/// <summary>Indicates whether directory has been added or modifed.</summary>
		public required bool IsNew { get; init; }
		/// <summary>Index of the directory entry in <see cref="DepotManifest.DirectoryBuffer"/>.</summary>
		public required int Index { get; init; }
		/// <summary>Directory's file entries.</summary>
		public required ArraySegment<FileEntry.AcquisitionEntry> Files { get; init; }
		/// <summary>Subdirectory entries.</summary>
		public required ArraySegment<AcquisitionEntry> Subdirectories { get; init; }
	}
	/// <summary>Structure used in <see cref="DepotDelta"/> to store auxiliary data like patched chunks and relocations for files or removed items.</summary>
	internal readonly struct AuxiliaryEntry
	{
		/// <summary>Index of the directory entry in <see cref="DepotManifest.DirectoryBuffer"/>. If <see cref="FilesToRemove"/> is empty, the index is for source manifest.</summary>
		public required int Index { get; init; }
		/// <summary>Indexes of the files that must be removed. If empty, the directory with all its contents is removed instead.</summary>
		public ArraySegment<int>? FilesToRemove { get; init; }
		/// <summary>Directory's file entries.</summary>
		public ArraySegment<FileEntry.AuxiliaryEntry> Files { get; init; }
		/// <summary>Subdirectory entries.</summary>
		public ArraySegment<AuxiliaryEntry> Subdirectories { get; init; }
	}
	/// <summary>Mutable and reference-type variant of <see cref="AcquisitionEntry"/> used during validation and update delta computation.</summary>
	internal class AcquisitionStaging
	{
		/// <summary>Creates an empty staging directory entry with specified index.</summary>
		/// <param name="index">Index of the directory entry in <see cref="DepotManifest.DirectoryBuffer"/>.</param>
		/// <param name="isNew">Value indicating whether directory has been added or modifed.</param>
		public AcquisitionStaging(int index, bool isNew)
		{
			IsNew = isNew;
			Index = index;
			Files = [];
			Subdirectories = [];
		}
		/// <summary>Creates a staging directory entry by reading its data from validation cache buffer.</summary>
		/// <param name="buffer">Validation cache buffer.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/>, pointing to the beginning of directory entry.</param>
		public AcquisitionStaging(ReadOnlySpan<int> buffer, ref int offset)
		{
			IsNew = buffer[offset++] is not 0;
			Index = buffer[offset++];
			int numFiles = buffer[offset++];
			int numSubdirs = buffer[offset++];
			Files = new(numFiles);
			for (int i = 0; i < numFiles; i++)
				Files.Add(new(buffer, ref offset));
			Subdirectories = new(numSubdirs);
			for (int i = 0; i < numSubdirs; i++)
				Subdirectories.Add(new(buffer, ref offset));
		}
		/// <summary>Indicates whether directory has been added or modifed.</summary>
		public bool IsNew { get; internal set; }
		/// <summary>Index of the directory entry in <see cref="DepotManifest.DirectoryBuffer"/>.</summary>
		public int Index { get; }
		/// <summary>Directory's file entries.</summary>
		public List<FileEntry.AcquisitionStaging> Files { get; }
		/// <summary>Subdirectory entries.</summary>
		public List<AcquisitionStaging> Subdirectories { get; }
		/// <summary>Writes entry data to the validation cache buffer.</summary>
		/// <param name="buffer">Validation cache buffer.</param>
		/// <param name="offset">Offset into <paramref name="buffer"/> to write to.</param>
		public void WriteToBuffer(Span<int> buffer, ref int offset)
		{
			buffer[offset++] = IsNew ? 1 : 0;
			buffer[offset++] = Index;
			buffer[offset++] = Files.Count;
			buffer[offset++] = Subdirectories.Count;
			foreach (var file in Files)
				file.WriteToBuffer(buffer, ref offset);
			foreach (var subdir in Subdirectories)
				subdir.WriteToBuffer(buffer, ref offset);
		}
	}

	/// <summary>Mutable and reference-type variant of <see cref="AuxiliaryEntry"/> used during update delta computation.</summary>
	/// <param name="index">Index of the directory entry in its parent directory.</param>
	internal class AuxiliaryStaging(int index)
	{
		/// <summary>Index of the directory entry in <see cref="DepotManifest.DirectoryBuffer"/>. If <see cref="FilesToRemove"/> is empty, the index is for source manifest.</summary>
		public int Index { get; } = index;
		/// <summary>Indexes of the files that must be removed. If empty, the directory with all its contents is removed instead.</summary>
		public List<int>? FilesToRemove { get; set; }
		/// <summary>Directory's file entries.</summary>
		public List<FileEntry.AuxiliaryStaging> Files { get; } = [];
		/// <summary>Subdirectory entries.</summary>
		public List<AuxiliaryStaging> Subdirectories { get; } = [];
	}
}