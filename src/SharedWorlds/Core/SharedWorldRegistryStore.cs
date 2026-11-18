using System.Text.Json;

namespace SharedWorlds.Core;

/// <summary>Thread-safe local registry of shared worlds. Persisted as JSON inside the tML save folder.</summary>
public sealed class SharedWorldRegistryStore
{
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	private readonly object _lock = new();
	private SharedWorldRegistry _data = new();

	public event Action? Changed;

	public string RegistryPath => SharedWorldPaths.RegistryPath;

	public void Load()
	{
		lock (_lock) {
			SharedWorldPaths.EnsureCreated();

			if (!File.Exists(RegistryPath)) {
				_data = new SharedWorldRegistry();
				return;
			}

			try {
				string json = File.ReadAllText(RegistryPath);
				_data = JsonSerializer.Deserialize<SharedWorldRegistry>(json, JsonOptions) ?? new SharedWorldRegistry();
				_data.Worlds ??= new List<SharedWorldRecord>();

				foreach (SharedWorldRecord record in _data.Worlds) {
					record.Participants ??= new Dictionary<string, ulong>();
					record.History ??= new List<VersionRecord>();
					record.Mods ??= new List<ModEntry>();
				}
			}
			catch (Exception e) {
				string corrupt = RegistryPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
				try {
						File.Move(RegistryPath, corrupt, overwrite: true);
					}
					catch { }
	
					Log.Warn($"Could not read Shared Worlds registry; started a new one. Old file: {corrupt}", e);
					_data = new SharedWorldRegistry();
				}
			}
		}
	
public void Save()
{
	lock (_lock) {
		try {
			SharedWorldPaths.EnsureCreated();

			// The Host & Play server is a separate process sharing this save directory with the
			// client. A short cross-process lock plus a merge prevents one process from silently
			// dropping the other's participants/history.
			using FileStream? fileLock = TryAcquireFileLock();
			MergeFromDisk();

			string json = JsonSerializer.Serialize(_data, JsonOptions);
			string temp = RegistryPath + ".tmp";

			File.WriteAllText(temp, json);
			for (int attempt = 0; attempt < 5; attempt++) {
				try {
					File.Move(temp, RegistryPath, overwrite: true);
					break;
				}
				catch (IOException) when (attempt < 4) {
					Thread.Sleep(50);
				}
			}
		}
		catch (Exception e) {
			Log.Error("Could not save the Shared Worlds registry.", e);
		}
	}

	Changed?.Invoke();
}

private FileStream? TryAcquireFileLock()
{
	for (int i = 0; i < 40; i++) {
		try {
			return new FileStream(RegistryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}
		catch (IOException) {
			Thread.Sleep(50);
		}
		catch (UnauthorizedAccessException) {
			break;
		}
	}

	Log.Warn("Could not acquire the Shared Worlds registry lock; writing anyway.");
	return null;
}

private void MergeFromDisk()
{
	try {
		if (!File.Exists(RegistryPath))
			return;

		SharedWorldRegistry? disk = JsonSerializer.Deserialize<SharedWorldRegistry>(File.ReadAllText(RegistryPath), JsonOptions);
		if (disk?.Worlds == null)
			return;

		foreach (SharedWorldRecord diskRecord in disk.Worlds) {
			SharedWorldRecord? memoryRecord = _data.Worlds.FirstOrDefault(w =>
				string.Equals(w.WorldId, diskRecord.WorldId, StringComparison.OrdinalIgnoreCase));

			if (memoryRecord == null) {
				_data.Worlds.Add(diskRecord);
				continue;
			}

			MergeRecords(memoryRecord, diskRecord);
		}
	}
	catch (Exception e) {
		Log.Warn("Could not merge the on-disk registry before saving; keeping in-memory state.", e);
	}
}

private static void MergeRecords(SharedWorldRecord target, SharedWorldRecord other)
{
	foreach (KeyValuePair<string, ulong> participant in other.Participants ?? new()) {
		if (string.IsNullOrWhiteSpace(participant.Key))
			continue;

		if (!target.Participants.TryGetValue(participant.Key, out ulong current) ||
			(current == 0 && participant.Value != 0))
			target.Participants[participant.Key] = participant.Value;
	}

	foreach (VersionRecord version in other.History ?? new()) {
		if (target.History.All(v => !string.Equals(v.Hash, version.Hash, StringComparison.OrdinalIgnoreCase)))
			target.History.Add(version);
	}

	target.History = target.History
		.OrderBy(v => v.Version)
		.ThenBy(v => v.TimestampUtc)
		.GroupBy(v => v.Hash, StringComparer.OrdinalIgnoreCase)
		.Select(g => g.First())
		.ToList();

	if (other.LocalVersion > target.LocalVersion) {
		target.LocalVersion = other.LocalVersion;
		target.LocalHash = other.LocalHash;
		target.LastParentHash = other.LastParentHash;
		target.LastHostSteamId = other.LastHostSteamId;
		target.LastUpdatedUtc = other.LastUpdatedUtc;
		target.Mods = other.Mods ?? target.Mods;
		target.PendingUpload = other.PendingUpload;
		target.PendingSnapshotDir = other.PendingSnapshotDir;
		target.PendingVersion = other.PendingVersion;
		target.PendingHash = other.PendingHash;
	}

	if (string.IsNullOrWhiteSpace(target.WorldFileName) && !string.IsNullOrWhiteSpace(other.WorldFileName))
		target.WorldFileName = other.WorldFileName;

	if (string.IsNullOrWhiteSpace(target.WorldName) && !string.IsNullOrWhiteSpace(other.WorldName))
		target.WorldName = other.WorldName;

	if (target.WorkshopItemId == 0 && other.WorkshopItemId != 0)
		target.WorkshopItemId = other.WorkshopItemId;

	if (!target.PendingUpload && other.PendingUpload) {
		target.PendingUpload = true;
		target.PendingSnapshotDir = other.PendingSnapshotDir;
		target.PendingVersion = other.PendingVersion;
		target.PendingHash = other.PendingHash;
	}
}

public List<SharedWorldRecord> Snapshot()
{
	lock (_lock) {
		return _data.Worlds.Select(Clone).ToList();
	}
}

public SharedWorldRecord? FindByWorldId(string? worldId)
{
	if (string.IsNullOrWhiteSpace(worldId))
		return null;

	lock (_lock) {
		SharedWorldRecord? record = _data.Worlds.FirstOrDefault(w =>
			string.Equals(w.WorldId, worldId, StringComparison.OrdinalIgnoreCase));
		return record == null ? null : Clone(record);
	}
}

public SharedWorldRecord? FindByFileName(string? fileName)
{
	if (string.IsNullOrWhiteSpace(fileName))
		return null;

	lock (_lock) {
		SharedWorldRecord? record = _data.Worlds.FirstOrDefault(w =>
			string.Equals(w.WorldFileName, fileName, StringComparison.OrdinalIgnoreCase));
		return record == null ? null : Clone(record);
	}
}

public SharedWorldRecord Add(SharedWorldRecord record)
{
	lock (_lock) {
		SharedWorldRecord? existing = _data.Worlds.FirstOrDefault(w =>
			string.Equals(w.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase));

		if (existing != null) {
			CopyInto(existing, record);
			return Clone(existing);
		}

		_data.Worlds.Add(Clone(record));
		return Clone(record);
	}
}

public void Update(SharedWorldRecord record)
{
	lock (_lock) {
		SharedWorldRecord? existing = _data.Worlds.FirstOrDefault(w =>
			string.Equals(w.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase));

		if (existing == null)
			_data.Worlds.Add(Clone(record));
		else
			CopyInto(existing, record);
	}

	Changed?.Invoke();
}

public bool Mutate(string worldId, Action<SharedWorldRecord> mutate)
{
	lock (_lock) {
		SharedWorldRecord? record = _data.Worlds.FirstOrDefault(w =>
			string.Equals(w.WorldId, worldId, StringComparison.OrdinalIgnoreCase));

		if (record == null)
			return false;

		mutate(record);
		return true;
	}
}

public bool Remove(string worldId)
{
	bool removed;
	lock (_lock) {
		removed = _data.Worlds.RemoveAll(w =>
			string.Equals(w.WorldId, worldId, StringComparison.OrdinalIgnoreCase)) > 0;
	}

	if (removed)
		Changed?.Invoke();

	return removed;
}

/// <summary>Merges a version record into a world's history, keeping it deduplicated and bounded.</summary>
public void RecordVersion(string worldId, VersionRecord record, int historyLimit = 250)
{
	lock (_lock) {
		SharedWorldRecord? world = _data.Worlds.FirstOrDefault(w =>
			string.Equals(w.WorldId, worldId, StringComparison.OrdinalIgnoreCase));

		if (world == null)
			return;

		VersionRecord? existing = world.History.FirstOrDefault(v =>
			string.Equals(v.Hash, record.Hash, StringComparison.OrdinalIgnoreCase));

		if (existing == null) {
			world.History.Add(record);
		}
		else {
			if (string.IsNullOrEmpty(existing.ParentHash))
				existing.ParentHash = record.ParentHash;
			if (existing.Version == 0)
				existing.Version = record.Version;
			if (existing.HostSteamId == 0)
				existing.HostSteamId = record.HostSteamId;
			if (existing.TimestampUtc == default)
				existing.TimestampUtc = record.TimestampUtc;
		}

		world.History = world.History
			.OrderBy(v => v.Version)
			.ThenBy(v => v.TimestampUtc)
			.GroupBy(v => v.Hash, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.ToList();

		if (world.History.Count > historyLimit)
			world.History.RemoveRange(0, world.History.Count - historyLimit);
	}
}

public static SharedWorldRecord Clone(SharedWorldRecord record)
{
	var clone = new SharedWorldRecord();
	CopyInto(clone, record);
	return clone;
}

private static void CopyInto(SharedWorldRecord target, SharedWorldRecord source)
{
	target.WorldId = source.WorldId;
	target.WorldName = source.WorldName;
	target.WorldFileName = source.WorldFileName;
	target.WorkshopItemId = source.WorkshopItemId;
	target.LocalVersion = source.LocalVersion;
	target.LocalHash = source.LocalHash;
	target.LastParentHash = source.LastParentHash;
	target.LastHostSteamId = source.LastHostSteamId;
	target.LastUpdatedUtc = source.LastUpdatedUtc;
	target.Participants = new Dictionary<string, ulong>(source.Participants ?? new());
	target.History = (source.History ?? new()).Select(v => new VersionRecord {
		Version = v.Version,
		Hash = v.Hash,
		ParentHash = v.ParentHash,
		HostSteamId = v.HostSteamId,
		TimestampUtc = v.TimestampUtc,
		WorldName = v.WorldName,
		SourceItemId = v.SourceItemId
	}).ToList();
	target.Mods = (source.Mods ?? new()).Select(m => new ModEntry {
		Name = m.Name,
		Version = m.Version
	}).ToList();
	target.WorkshopStorageEnabled = source.WorkshopStorageEnabled;
	target.PendingUpload = source.PendingUpload;
	target.PendingSnapshotDir = source.PendingSnapshotDir;
	target.PendingVersion = source.PendingVersion;
	target.PendingHash = source.PendingHash;
}
}
