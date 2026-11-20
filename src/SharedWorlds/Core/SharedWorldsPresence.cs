using Steamworks;

namespace SharedWorlds.Core;

/// <summary>
/// A live Host &amp; Play session advertised through Steam Rich Presence. Rich Presence is
/// friends-only on Steam, which matches the FriendsOnly Workshop storage used by the mod.
/// </summary>
public sealed class LiveWorldSession
{
	public string WorldId { get; init; } = "";
	public string WorldName { get; init; } = "";
	public ulong HostSteamId { get; init; }
	public ulong LobbyId { get; init; }
	public int Version { get; init; }
	public string ContentHash { get; init; } = "";
	public int Players { get; init; }
	public DateTime SnapshotUtc { get; init; }

	public bool IsUsable => !string.IsNullOrWhiteSpace(WorldId) && LobbyId != 0;
}

/// <summary>
/// Reads and writes the small Rich Presence record used by the Play screen and by host
/// migration. No account data or world contents are placed in Rich Presence.
/// </summary>
public static class SharedWorldsPresence
{
	private const string KeyWorld = "sw_world";
	private const string KeyName = "sw_name";
	private const string KeyLobby = "sw_lobby";
	private const string KeyVersion = "sw_version";
	private const string KeyHash = "sw_hash";
	private const string KeyPlayers = "sw_players";
	private const string KeyTime = "sw_time";
	private const string KeySchema = "sw_schema";
	private const string KeyHandoff = "sw_handoff";

	private const int Schema = 1;

	private static readonly Dictionary<ulong, DateTime> LastPresenceRequest = new();
	private static readonly Dictionary<ulong, (DateTime ReadUtc, LiveWorldSession? Session)> Cache = new();
	private static readonly object CacheLock = new();

	private static string? _lastPublished;
	private static DateTime _lastPublishedUtc;

	public static void PublishHosting(SharedWorldRecord record, ulong lobbyId, int players, string? hash, int version, DateTime snapshotUtc)
	{
		if (!WorkshopService.IsAvailable || record == null || string.IsNullOrWhiteSpace(record.WorldId) || lobbyId == 0)
			return;

		ulong host = WorkshopService.CurrentSteamId;
		string signature = $"{record.WorldId}|{lobbyId}|{players}|{hash}|{version}|{snapshotUtc.Ticks}";
		if (signature == _lastPublished && (DateTime.UtcNow - _lastPublishedUtc).TotalSeconds < 15)
			return;

		try {
			SteamFriends.SetRichPresence(KeySchema, Schema.ToString());
			SteamFriends.SetRichPresence(KeyWorld, record.WorldId);
			SteamFriends.SetRichPresence(KeyName, Trim(record.WorldName, 120));
			SteamFriends.SetRichPresence(KeyLobby, lobbyId.ToString());
			SteamFriends.SetRichPresence(KeyVersion, Math.Max(0, version).ToString());
			SteamFriends.SetRichPresence(KeyHash, Trim(hash ?? record.LocalHash, 120));
			SteamFriends.SetRichPresence(KeyPlayers, Math.Max(0, players).ToString());
			SteamFriends.SetRichPresence(KeyTime, new DateTimeOffset(snapshotUtc == default ? DateTime.UtcNow : snapshotUtc).ToUnixTimeSeconds().ToString());
			// The new host process clears the handoff marker set by the client that accepted.
			SteamFriends.SetRichPresence(KeyHandoff, "");

			_lastPublished = signature;
			_lastPublishedUtc = DateTime.UtcNow;
			Log.Info($"Advertising live shared world '{record.WorldName}' (lobby {lobbyId}, {players} player(s)).");
		}
		catch (Exception e) {
			Log.Warn("Could not publish Shared Worlds Rich Presence.", e);
		}
	}

	public static void ClearHosting()
	{
		if (!WorkshopService.IsAvailable)
			return;

		try {
			SteamFriends.SetRichPresence(KeySchema, "");
			SteamFriends.SetRichPresence(KeyWorld, "");
			SteamFriends.SetRichPresence(KeyName, "");
			SteamFriends.SetRichPresence(KeyLobby, "");
			SteamFriends.SetRichPresence(KeyVersion, "");
			SteamFriends.SetRichPresence(KeyHash, "");
			SteamFriends.SetRichPresence(KeyPlayers, "");
			SteamFriends.SetRichPresence(KeyTime, "");
			SteamFriends.SetRichPresence(KeyHandoff, "");
			_lastPublished = null;
		}
		catch (Exception e) {
			Log.Warn("Could not clear Shared Worlds Rich Presence.", e);
		}
	}

	/// <summary>Reads a friend's live session for this world. Results are cached briefly.</summary>
	public static LiveWorldSession? Read(ulong steamId, string worldId, bool forceRefresh = false)
	{
		if (!WorkshopService.IsAvailable || steamId == 0 || string.IsNullOrWhiteSpace(worldId))
			return null;

		lock (CacheLock) {
			if (!forceRefresh && Cache.TryGetValue(steamId, out var cached) &&
				(DateTime.UtcNow - cached.ReadUtc).TotalSeconds < 2.5)
				return cached.Session != null && string.Equals(cached.Session.WorldId, worldId, StringComparison.OrdinalIgnoreCase)
					? cached.Session
					: null;
		}

		LiveWorldSession? session = ReadCore(steamId);
		lock (CacheLock)
			Cache[steamId] = (DateTime.UtcNow, session);

		return session != null && string.Equals(session.WorldId, worldId, StringComparison.OrdinalIgnoreCase)
			? session
			: null;
	}

	private static LiveWorldSession? ReadCore(ulong steamId)
	{
		try {
			var friend = new CSteamID(steamId);
			if (LastPresenceRequest.TryGetValue(steamId, out DateTime requested) &&
				(DateTime.UtcNow - requested).TotalSeconds < 5) {
				// Already requested recently; Steam will update the cached keys asynchronously.
			}
			else {
				SteamFriends.RequestFriendRichPresence(friend);
				LastPresenceRequest[steamId] = DateTime.UtcNow;
			}

			string worldId = SteamFriends.GetFriendRichPresence(friend, KeyWorld) ?? "";
			if (string.IsNullOrWhiteSpace(worldId))
				return null;

			if (!ulong.TryParse(SteamFriends.GetFriendRichPresence(friend, KeyLobby) ?? "", out ulong lobbyId) || lobbyId == 0)
				return null;

			int version = int.TryParse(SteamFriends.GetFriendRichPresence(friend, KeyVersion) ?? "", out int v) ? v : 0;
			int players = int.TryParse(SteamFriends.GetFriendRichPresence(friend, KeyPlayers) ?? "", out int p) ? p : 0;
			long unix = long.TryParse(SteamFriends.GetFriendRichPresence(friend, KeyTime) ?? "", out long t) ? t : 0;

			return new LiveWorldSession {
				WorldId = worldId,
				WorldName = SteamFriends.GetFriendRichPresence(friend, KeyName) ?? "",
				HostSteamId = steamId,
				LobbyId = lobbyId,
				Version = version,
				ContentHash = SteamFriends.GetFriendRichPresence(friend, KeyHash) ?? "",
				Players = Math.Max(0, players),
				SnapshotUtc = unix <= 0 ? default : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
			};
		}
		catch (Exception e) {
			Log.Warn($"Could not read Rich Presence for {WorkshopService.PersonaName(steamId)}.", e);
			return null;
		}
	}

	/// <summary>Marks this account as preparing to take over a world. Friends can see this
	/// while the new Host &amp; Play process is loading, so they do not elect a second host.</summary>
	public static void SetHandoff(string worldId)
	{
		if (!WorkshopService.IsAvailable || string.IsNullOrWhiteSpace(worldId))
			return;

		try {
			SteamFriends.SetRichPresence(KeyHandoff, Trim(worldId, 120));
		}
		catch (Exception e) {
			Log.Warn("Could not publish the host handoff marker.", e);
		}
	}

	public static void ClearHandoff()
	{
		if (!WorkshopService.IsAvailable)
			return;

		try {
			SteamFriends.SetRichPresence(KeyHandoff, "");
		}
		catch { }
	}

	public static bool HasHandoff(ulong steamId, string worldId)
	{
		if (!WorkshopService.IsAvailable || steamId == 0 || string.IsNullOrWhiteSpace(worldId))
			return false;

		try {
			string value = SteamFriends.GetFriendRichPresence(new CSteamID(steamId), KeyHandoff) ?? "";
			return string.Equals(value, worldId, StringComparison.OrdinalIgnoreCase);
		}
		catch {
			return false;
		}
	}

	public static void Invalidate()
	{
		lock (CacheLock)
			Cache.Clear();
	}

	private static string Trim(string value, int max)
		=> string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
