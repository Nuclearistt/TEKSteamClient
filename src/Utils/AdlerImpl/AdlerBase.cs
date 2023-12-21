namespace TEKSteamClient.Utils.AdlerImpl;

/// <summary>Base Adler checksum implementation that doesn't use specific instruction sets.</summary>
internal class AdlerBase : IAdlerImpl
{
	public uint ComputeChecksum(ReadOnlySpan<byte> data)
	{
		uint a = 0, b = 0;
		int index = 0;
		int numChunks = data.Length / 5552, remainder = data.Length % 5552;
		for (int i = 0; i < numChunks; i++)
		{
			for (int j = 0; j < 5552; j++)
			{
				a += data[index++];
				b += a;
			}
			a %= 65521;
			b %= 65521;
		}
		if (remainder > 0)
		{
			for (int i = 0; i < remainder; i++)
			{
				a += data[index++];
				b += a;
			}
			a %= 65521;
			b %= 65521;
		}
		return a | (b << 16);
	}
}