using System.Runtime.InteropServices;

namespace TEKSteamClient;

/// <summary>A recursive entry for reading and writing VDF (Valve Data Format), represents an object or a key-value pair within object.</summary>
/// <param name="key">Key of the entry.</param>
public class VDFEntry(string key)
{
	/// <summary>Creates a new entry and fills it with data read by <paramref name="reader"/>.</summary>
	/// <param name="reader">Reader of the stream that contains the text to parse.</param>
	public VDFEntry(StreamReader reader) : this(reader.ReadLine()![1..^1]) => Parse(reader);
	/// <summary>Key of the key-value pair, or name of the object.</summary>
	public string Key { get; set; } = key;
	/// <summary>Value of the key-value pair, <see langword="null"/> if the entry is an object.</summary>
	public string? Value { get; set; }
	/// <summary>Child nodes if <see langword="this"/> is an object.</summary>
	public List<VDFEntry>? Children { get; private set; }
	/// <summary>Retrieves object's child with specified <paramref name="key"/>.</summary>
	/// <param name="key">Key of the entry to retrieve.</param>
	public VDFEntry? this[string key] => Children?.Find(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
	/// <summary>Parses inner text of the object into its children.</summary>
	/// <param name="reader">Reader of the stream that contains the text to parse.</param>
	private void Parse(StreamReader reader)
	{
		Children = [];
		string? line;
		while ((line = reader.ReadLine()) is not null && !line.EndsWith('}'))
		{
			int qmc = line.AsSpan().Count('"') - line.AsSpan().Count("\\\"");
			if (qmc == 0) //Most likely empty line => nothing to read
				continue;
			string[] substrings = line.Split('"');
			if (qmc == 2) //1 string => object name
			{
				var child = new VDFEntry(substrings[1]);
				child.Parse(reader);
				Children.Add(child);
			}
			else if (qmc == 4) //2 strings => key-value pair
				Children.Add(new(substrings[1]) { Value = substrings[3] });
		}
	}
	/// <summary>Writes the contents of the entry to a stream.</summary>
	/// <param name="writer">Writer of the stream that the entry will be written to.</param>
	/// <param name="identLevel">Level of identation, used for child nodes.</param>
	public void Write(StreamWriter writer, [Optional]int identLevel)
	{
		Span<char> tabs = stackalloc char[identLevel];
		tabs.Fill('\t');
		writer.Write(tabs);
		writer.Write($"\"{Key}\"");
		if (Children is null)
			writer.WriteLine($"\t\t\"{Value}\"");
		else
		{
			writer.Write('\n');
			writer.Write(tabs);
			writer.Write("{\n");
			foreach (var child in Children)
				child.Write(writer, identLevel + 1);
			writer.Write(tabs);
			writer.Write("}\n");
		}
	}
}