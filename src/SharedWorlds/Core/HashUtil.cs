using System.Security.Cryptography;

namespace SharedWorlds.Core;

public static class HashUtil
{
	/// <summary>
	/// Content identity for a snapshot:
	/// SHA256( SHA256(world.wld) || SHA256(world.twld) ), lowercase hex.
	/// </summary>
	public static string ComputeContentHash(string wldPath, string twldPath)
	{
		byte[] wldHash = HashFile(wldPath);
		byte[] twldHash = HashFile(twldPath);

		Span<byte> combined = stackalloc byte[64];
		wldHash.CopyTo(combined);
		twldHash.CopyTo(combined[32..]);

		return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
	}

	public static byte[] HashFile(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return SHA256.HashData(stream);
	}

	public static string HashFileHex(string path) => Convert.ToHexString(HashFile(path)).ToLowerInvariant();

	public static string HashBytesHex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

	public static bool VerifyContentHash(string wldPath, string twldPath, string expected)
	{
		if (string.IsNullOrWhiteSpace(expected))
			return false;

		string actual = ComputeContentHash(wldPath, twldPath);
		return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
	}

	public static string Short(string hash, int length = 12)
		=> string.IsNullOrEmpty(hash) ? "" : (hash.Length <= length ? hash : hash[..length]);
}
