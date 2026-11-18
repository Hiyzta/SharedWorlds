using System.Text;

namespace SharedWorlds.Core;

public sealed record InviteData(string WorldId, ulong OwnerSteamId, ulong ItemId, string WorldName);

public static class InviteCode
{
	public const string Prefix = "SW1";

	public static string Create(string worldId, ulong ownerSteamId, ulong itemId, string worldName)
	{
		string name = Convert.ToBase64String(Encoding.UTF8.GetBytes(worldName ?? ""))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');

		return $"{Prefix}.{worldId}.{ownerSteamId}.{itemId}.{name}";
	}

	public static bool TryParse(string? code, out InviteData? data, out string error)
	{
		data = null;
		error = "";
		code = (code ?? "").Trim();

		string[] parts = code.Split('.');
		if (parts.Length != 5 || parts[0] != Prefix) {
			error = "That is not a Shared Worlds invite code.";
			return false;
		}

		if (!Guid.TryParse(parts[1], out _) ||
			!ulong.TryParse(parts[2], out ulong owner) ||
			!ulong.TryParse(parts[3], out ulong itemId) ||
			owner == 0 || itemId == 0) {
			error = "The invite code is malformed.";
			return false;
		}

		string name;
		try {
			string b64 = parts[4].Replace('-', '+').Replace('_', '/');
			switch (b64.Length % 4) {
				case 2: b64 += "=="; break;
				case 3: b64 += "="; break;
			}
			name = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
		}
		catch {
			error = "The invite code has an invalid world name.";
			return false;
		}

		data = new InviteData(parts[1], owner, itemId, name);
		return true;
	}
}
