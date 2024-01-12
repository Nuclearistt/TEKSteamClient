using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TEKSteamClient;

/// <summary>Identifier for Steam item (depot or a workshop item).</summary>
public class ItemIdentifier
{
	/// <summary>Creates a new item identifier with specified depot ID and optionally a workshop item ID.</summary>
	/// <param name="depotId">ID of the depot.</param>
	/// <param name="itemId">ID of the workshop item.</param>
	public ItemIdentifier(uint depotId, [Optional]ulong itemId)
	{
		DepotId = depotId;
		WorkshopItemId = itemId;
	}
	/// <summary>Creates a new item identifier by parsing its string representation.</summary>
	/// <param name="str">String representation of item identifier.</param>
	public ItemIdentifier(string str)
	{
		if (str.Contains('.'))
		{
			string[] substrings = str.Split('.');
			DepotId = uint.Parse(substrings[0]);
			WorkshopItemId = ulong.Parse(substrings[1]);

		}
		else
		{
			DepotId = uint.Parse(str);
			WorkshopItemId = 0;
		}
	}
	/// <summary>Depot ID of the item.</summary>
	public uint DepotId { get; }
	/// <summary>Workshop item ID of the item.</summary>
	public ulong WorkshopItemId { get; }
	/// <inheritdoc/>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override string ToString() => WorkshopItemId is 0 ? DepotId.ToString() : string.Concat(DepotId.ToString(), ".", WorkshopItemId.ToString());
}