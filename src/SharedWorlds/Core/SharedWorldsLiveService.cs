namespace SharedWorlds.Core;

public sealed class WorldLiveState
{
	public LiveWorldSession? Live { get; set; }
	public SyncReport? Report { get; set; }
	public DateTime CheckedUtc { get; set; }
	public bool Busy { get; set; }
	public string Error { get; set; } = "";
}

/// <summary>
/// Runtime view of the worlds shown on the Play screen. Live state is deliberately not
/// persisted: Rich Presence describes a Steam lobby that may disappear at any moment.
/// </summary>
public static class SharedWorldsLiveService
{
	private static readonly object Lock = new();
	private static readonly Dictionary<string, WorldLiveState> States = new(StringComparer.OrdinalIgnoreCase);

	public static WorldLiveState Get(string worldId)
	{
		lock (Lock) {
			if (!States.TryGetValue(worldId, out WorldLiveState? state)) {
				state = new WorldLiveState();
				States[worldId] = state;
			}

			return state;
		}
	}

	public static void Set(string worldId, Action<WorldLiveState> update)
	{
		lock (Lock) {
			if (!States.TryGetValue(worldId, out WorldLiveState? state)) {
				state = new WorldLiveState();
				States[worldId] = state;
			}

			update(state);
		}
	}

	public static LiveWorldSession? FindLive(SharedWorldRecord record)
	{
		if (record == null || string.IsNullOrWhiteSpace(record.WorldId) || !WorkshopService.IsAvailable)
			return null;

		var ids = new HashSet<ulong>();
		if (record.LastHostSteamId != 0)
			ids.Add(record.LastHostSteamId);

		foreach (string text in record.Participants.Keys) {
			if (ulong.TryParse(text, out ulong id) && id != 0)
				ids.Add(id);
		}

		SharedWorldsLiveSession? session = SharedWorldsSessionState.Current;
		if (session != null && string.Equals(session.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase)) {
			if (session.HostSteamId != 0)
				ids.Add(session.HostSteamId);
			foreach (ulong id in session.Roster)
				ids.Add(id);
		}

		LiveWorldSession? best = null;
		foreach (ulong id in ids) {
			LiveWorldSession? live = SharedWorldsPresence.Read(id, record.WorldId);
			if (live == null || !live.IsUsable)
				continue;

			if (best == null || live.SnapshotUtc > best.SnapshotUtc)
				best = live;
		}

		Set(record.WorldId, state => state.Live = best);
		return best;
	}

	public static async Task<WorldLiveState> CheckAsync(SharedWorldRecord record, bool downloadIfNewer, IProgress<string>? progress = null, CancellationToken token = default)
	{
		Set(record.WorldId, state => {
			state.Busy = true;
			state.Error = "";
		});

		try {
			SharedWorldRecord? fresh = SharedWorldsService.Instance.Registry.FindByWorldId(record.WorldId) ?? record;
			LiveWorldSession? live = FindLive(fresh);
			SyncReport report = await SharedWorldsService.Instance.SyncAsync(fresh, downloadIfNewer, progress, token).ConfigureAwait(false);

			Set(record.WorldId, state => {
				state.Live = live;
				state.Report = report;
				state.CheckedUtc = DateTime.UtcNow;
				state.Busy = false;
				state.Error = report.Status == SyncStatus.Error ? report.Message : "";
			});

			return Get(record.WorldId);
		}
		catch (Exception e) {
			Log.Warn($"Could not check live state for '{record.WorldName}'.", e);
			Set(record.WorldId, state => {
				state.Busy = false;
				state.CheckedUtc = DateTime.UtcNow;
				state.Error = e.Message;
			});
			return Get(record.WorldId);
		}
	}

	/// <summary>Fast-forwards to the newest published snapshot before starting Host &amp; Play.</summary>
	public static async Task<(bool Success, string Message)> PrepareHostAsync(SharedWorldRecord record, IProgress<string>? progress = null, CancellationToken token = default)
	{
		WorldLiveState state = await CheckAsync(record, downloadIfNewer: true, progress, token).ConfigureAwait(false);
		SyncReport? report = state.Report;
		if (report == null)
			return (false, state.Error.Length > 0 ? state.Error : "Could not check the latest snapshot.");

		if (report.Status is SyncStatus.Fork or SyncStatus.Error)
			return (false, report.Message);

		SharedWorldRecord? fresh = SharedWorldsService.Instance.Registry.FindByWorldId(record.WorldId) ?? record;
		if (fresh.LocalVersion == 0 && string.IsNullOrEmpty(fresh.LocalHash) && !File.Exists(SharedWorldPaths.WorldPath(fresh)))
			return (false, "There is no local world file yet. Sync this world from the Workshop first.");

		return (true, report.Message);
	}

	public static bool CanJoin(SharedWorldRecord record, LiveWorldSession? live)
		=> live != null && live.IsUsable && live.LobbyId != 0;

	public static string StatusLine(SharedWorldRecord record, WorldLiveState state)
	{
		if (state.Busy)
			return "Checking the latest snapshot...";

		if (state.Error.Length > 0)
			return "Sync error: " + state.Error;

		if (state.Live != null)
			return FormatUtil.PlayerCount(state.Live.Players) + " · host " + WorkshopService.PersonaName(state.Live.HostSteamId);

		if (record.PendingNeedsEula)
			return "Steam Workshop EULA needs to be accepted before the next upload can be visible.";

		if (record.PendingUpload)
			return "A newer local snapshot is waiting to upload.";

		if (record.LocalVersion == 0 && string.IsNullOrEmpty(record.LocalHash))
			return "No local snapshot yet — sync before hosting.";

		return "Offline · " + (record.LastUpdatedUtc == default ? "never synced" : "last saved " + FormatUtil.Relative(record.LastUpdatedUtc));
	}

	public static string SyncLine(SharedWorldRecord record, WorldLiveState state)
	{
		if (state.CheckedUtc != default)
			return "Checked " + FormatUtil.Relative(state.CheckedUtc) + " · " + FormatUtil.Timestamp(record.LastUpdatedUtc);

		return record.LastUpdatedUtc == default
			? "No snapshot has been saved yet"
			: "Last saved " + FormatUtil.Relative(record.LastUpdatedUtc) + " · " + FormatUtil.Timestamp(record.LastUpdatedUtc);
	}
}
