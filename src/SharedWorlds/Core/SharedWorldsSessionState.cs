namespace SharedWorlds.Core;

/// <summary>
/// The client's view of the multiplayer session it is connected to. This is deliberately
/// memory-only: it describes a live Steam lobby, not a saved world.
/// </summary>
public sealed class SharedWorldsLiveSession
{
	public string WorldId { get; set; } = "";
	public string WorldName { get; set; } = "";
	public ulong HostSteamId { get; set; }
	public ulong LobbyId { get; set; }
	public int Version { get; set; }
	public string ContentHash { get; set; } = "";
	public DateTime SnapshotUtc { get; set; }
	public List<ulong> Roster { get; set; } = new();
	public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;

	public SharedWorldsLiveSession Clone() => new() {
		WorldId = WorldId,
		WorldName = WorldName,
		HostSteamId = HostSteamId,
		LobbyId = LobbyId,
		Version = Version,
		ContentHash = ContentHash,
		SnapshotUtc = SnapshotUtc,
		Roster = new List<ulong>(Roster),
		ReceivedUtc = ReceivedUtc
	};
}

public static class SharedWorldsSessionState
{
	private static readonly object Lock = new();
	private static SharedWorldsLiveSession? _current;

	public static SharedWorldsLiveSession? Current
	{
		get {
			lock (Lock)
				return _current?.Clone();
		}
	}

	public static void Set(SharedWorldsLiveSession session)
	{
		lock (Lock)
			_current = session.Clone();
	}

	public static void Clear()
	{
		lock (Lock)
			_current = null;
	}
}
