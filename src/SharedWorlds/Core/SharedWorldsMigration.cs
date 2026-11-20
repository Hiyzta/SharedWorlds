using Terraria;
using SharedWorlds.UI;

namespace SharedWorlds.Core;

public sealed class SharedWorldsMigrationContext
{
	public SharedWorldRecord Record { get; init; } = null!;
	public SharedWorldsLiveSession Session { get; init; } = null!;
	public List<ulong> Candidates { get; init; } = new();
	public int SelfIndex { get; init; } = -1;
	public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

	public const double StartDelaySeconds = 3;
	public const double SlotSeconds = 15;

	public double ElapsedSeconds => (DateTime.UtcNow - StartedUtc).TotalSeconds;

	public int CurrentSlot => ElapsedSeconds < StartDelaySeconds
		? -1
		: (int)((ElapsedSeconds - StartDelaySeconds) / SlotSeconds);

	public ulong? CurrentCandidate => CurrentSlot >= 0 && CurrentSlot < Candidates.Count
		? Candidates[CurrentSlot]
		: null;

	public bool IsSelfTurn => CurrentSlot >= 0 && CurrentSlot == SelfIndex;
	public bool IsExhausted => CurrentSlot >= Candidates.Count;
}

/// <summary>
/// Client-side host recovery. Terraria cannot migrate a server in place, so this is a
/// deterministic offer order: the lowest connected SteamID gets the first rehost prompt;
/// if nobody takes it within the slot, the next account is offered. A Steam Rich Presence
/// handoff marker prevents two players starting hosts at the same time.
/// </summary>
public static class SharedWorldsMigration
{
	private static readonly object Lock = new();
	private static SharedWorldsMigrationContext? _context;

	public static SharedWorldsMigrationContext? Current
	{
		get {
			lock (Lock)
				return _context;
		}
	}

	public static void Begin(string? worldId, string? worldPath)
	{
		if (Main.dedServ || !SharedWorldsConfig.Current.EnableHostMigration)
			return;

		if (string.IsNullOrWhiteSpace(worldId))
			return;

		SharedWorldsLiveSession? session = SharedWorldsSessionState.Current;
		if (session == null || !string.Equals(session.WorldId, worldId, StringComparison.OrdinalIgnoreCase))
			return;

		ulong ownSteamId = WorkshopService.CurrentSteamId;
		if (ownSteamId == 0 || session.HostSteamId == ownSteamId)
			return;

		SharedWorldRecord? record = SharedWorldsService.Instance.Registry.FindByWorldId(worldId);
		if (record == null)
			return;

		lock (Lock) {
			if (_context != null)
				return;
		}

		var candidates = new List<ulong>();
		foreach (ulong id in session.Roster) {
			if (id != 0 && id != session.HostSteamId && !candidates.Contains(id))
				candidates.Add(id);
		}

		// The roster is the connected-player list. Only fall back to the historical
		// participant list when an older server did not send a roster.
		if (candidates.Count == 0) {
			foreach (string text in record.Participants.Keys) {
				if (ulong.TryParse(text, out ulong id) &&
					id != 0 && id != session.HostSteamId && !candidates.Contains(id)) {
					candidates.Add(id);
				}
			}
		}

		if (ownSteamId != session.HostSteamId && !candidates.Contains(ownSteamId))
			candidates.Add(ownSteamId);

		candidates.Sort();

		var context = new SharedWorldsMigrationContext {
			Record = record,
			Session = session,
			Candidates = candidates,
			SelfIndex = candidates.IndexOf(ownSteamId)
		};

		lock (Lock)
			_context = context;

		Log.Info($"Host left '{record.WorldName}'. Rehost candidates: {string.Join(", ", candidates.Select(WorkshopService.PersonaName))}.");

		Main.QueueMainThreadAction(() => {
			if (!Main.gameMenu) {
				// The disconnect path sets gameMenu after OnWorldUnload; try once more next tick.
				Main.QueueMainThreadAction(OpenUi);
				return;
			}

			OpenUi();
		});
	}

	private static void OpenUi()
	{
		if (Current == null || Main.dedServ)
			return;

		Main.MenuUI.SetState(new SharedWorldsMigrationUIState());
		Main.menuMode = 888;
	}

	public static void Clear()
	{
		lock (Lock)
			_context = null;
	}

	public static void GiveUp(string message)
	{
		SharedWorldsMigrationContext? context;
		lock (Lock) {
			context = _context;
			_context = null;
		}

		if (context == null)
			return;

		SharedWorldsSessionState.Clear();
		Main.QueueMainThreadAction(() => {
			var play = new SharedWorldsPlayUIState(0, message, context.Record.WorldId, context.Record.WorldId);
			Main.MenuUI.SetState(play);
			Main.menuMode = 888;
		});
	}
}
