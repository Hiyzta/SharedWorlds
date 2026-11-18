using System.IO.Compression;
using System.Text.Json;
using Terraria;

namespace SharedWorlds.Core;

public sealed class SnapshotBuildResult
{
	public bool Success { get; set; }
	public bool Unchanged { get; set; }
	public string Message { get; set; } = "";
	public string SnapshotDir { get; set; } = "";
	public string ContentHash { get; set; } = "";
	public int Version { get; set; }
	public SnapshotManifest? Manifest { get; set; }
	public Exception? Exception { get; set; }
}

public static class SnapshotService
{
	public const long MaxSnapshotBytes = 64L * 1024 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static SnapshotBuildResult Build(
		SharedWorldRecord record,
		string wldPath,
		string twldPath,
		ulong hostSteamId,
		IReadOnlyList<ModEntry> mods,
		IReadOnlyList<VersionRecord> knownHistory,
		string? parentHash = null,
		int? explicitVersion = null,
		int historyLimit = 250)
	{
		try {
			parentHash ??= record.LocalHash ?? "";
			if (!File.Exists(wldPath))
				return Fail($"World file not found: {wldPath}");
			if (!File.Exists(twldPath))
				return Fail($"Mod world data not found: {twldPath}");

			long wldSize = new FileInfo(wldPath).Length;
			long twldSize = new FileInfo(twldPath).Length;
			if (wldSize <= 0)
				return Fail("World file is empty.");
			if (wldSize + twldSize > MaxSnapshotBytes)
				return Fail($"World snapshot is too large ({(wldSize + twldSize) / 1024 / 1024} MB; limit {MaxSnapshotBytes / 1024 / 1024} MB).");

			string hash = HashUtil.ComputeContentHash(wldPath, twldPath);

			bool sameAsKnown =
				(!string.IsNullOrEmpty(record.LocalHash) && string.Equals(hash, record.LocalHash, StringComparison.OrdinalIgnoreCase)) ||
				(!string.IsNullOrEmpty(parentHash) && string.Equals(hash, parentHash, StringComparison.OrdinalIgnoreCase));

			if (sameAsKnown && record.LocalVersion > 0) {
				return new SnapshotBuildResult {
					Success = true,
					Unchanged = true,
					Message = "World content has not changed since the last snapshot.",
					ContentHash = hash,
					Version = record.LocalVersion
				};
			}

			int version = explicitVersion ?? (Math.Max(record.HighestKnownVersion, 0) + 1);
			if (explicitVersion == null && record.PendingUpload && record.PendingVersion > 0)
				version = record.PendingVersion;

			string dir = SharedWorldPaths.SnapshotDir(record.WorldId, version, hash);
			Directory.CreateDirectory(dir);

			string snapshotWld = Path.Combine(dir, "world.wld");
			string snapshotTwld = Path.Combine(dir, "world.twld");
			File.Copy(wldPath, snapshotWld, overwrite: true);
			File.Copy(twldPath, snapshotTwld, overwrite: true);

			if (!HashUtil.VerifyContentHash(snapshotWld, snapshotTwld, hash))
				return Fail("Snapshot verification failed immediately after copying the world files.");

			var history = new List<VersionRecord>();
			foreach (VersionRecord v in knownHistory)
				history.Add(Clone(v));

			foreach (VersionRecord v in record.History)
				if (history.All(h => !string.Equals(h.Hash, v.Hash, StringComparison.OrdinalIgnoreCase)))
					history.Add(Clone(v));

			var thisVersion = new VersionRecord {
				Version = version,
				Hash = hash,
				ParentHash = parentHash,
				HostSteamId = hostSteamId,
				TimestampUtc = DateTime.UtcNow,
				WorldName = record.WorldName
			};

			history.RemoveAll(v => string.Equals(v.Hash, hash, StringComparison.OrdinalIgnoreCase));

			if (!string.IsNullOrWhiteSpace(parentHash) &&
				history.All(v => !string.Equals(v.Hash, parentHash, StringComparison.OrdinalIgnoreCase))) {
				// Preserve the parent link even if the local registry lost the parent's record.
				history.Add(new VersionRecord {
					Version = Math.Max(0, version - 1),
					Hash = parentHash,
					ParentHash = "",
					HostSteamId = record.LastHostSteamId,
					TimestampUtc = record.LastUpdatedUtc,
					WorldName = record.WorldName
				});
			}

			history.Add(thisVersion);
			history = history
				.OrderBy(v => v.Version)
				.ThenBy(v => v.TimestampUtc)
				.ToList();

			if (history.Count > historyLimit)
				history.RemoveRange(0, history.Count - historyLimit);

			var manifest = new SnapshotManifest {
				WorldId = record.WorldId,
				WorldName = record.WorldName,
				Version = version,
				ContentHash = hash,
				ParentHash = parentHash,
				LastHostSteamId = hostSteamId,
				TimestampUtc = thisVersion.TimestampUtc,
				Mods = mods.Select(m => new ModEntry { Name = m.Name, Version = m.Version }).ToList(),
				Participants = new Dictionary<string, ulong>(record.Participants),
				History = history
			};

			if (record.WorkshopItemId != 0)
				manifest.Participants[hostSteamId.ToString()] = record.WorkshopItemId;

			WriteManifest(dir, manifest);

			Log.Info($"Built snapshot {record.WorldName} v{version} ({HashUtil.Short(hash)}) at {dir}");

			return new SnapshotBuildResult {
				Success = true,
				SnapshotDir = dir,
				ContentHash = hash,
				Version = version,
				Manifest = manifest,
				Message = $"Snapshot v{version} created."
			};
		}
		catch (Exception e) {
			Log.Error("Failed to build world snapshot.", e);
			return Fail("Failed to build snapshot: " + e.Message, e);
		}
	}

	public static void WriteManifest(string dir, SnapshotManifest manifest)
	{
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, "manifest.json");
		string temp = path + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
		File.Move(temp, path, overwrite: true);
	}

	public static SnapshotManifest? ReadManifest(string dir)
	{
		try {
			string path = Path.Combine(dir, "manifest.json");
			if (!File.Exists(path))
				return null;

			return JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(path), JsonOptions);
		}
		catch (Exception e) {
			Log.Warn($"Could not read snapshot manifest from {dir}.", e);
			return null;
		}
	}

	public static bool VerifySnapshot(string dir, out SnapshotManifest? manifest, out string error)
	{
		manifest = ReadManifest(dir);
		error = "";

		if (manifest == null) {
			error = "Snapshot manifest is missing or unreadable.";
			return false;
		}

		string wld = Path.Combine(dir, "world.wld");
		string twld = Path.Combine(dir, "world.twld");
		if (!File.Exists(wld) || !File.Exists(twld)) {
			error = "Snapshot is missing world.wld or world.twld.";
			return false;
		}

		if (!HashUtil.VerifyContentHash(wld, twld, manifest.ContentHash)) {
			error = "Snapshot hash mismatch: the files do not match the manifest.";
			return false;
		}

		return true;
	}

	public static bool Install(
		SharedWorldRecord record,
		string snapshotDir,
		bool keepBackup,
		int maxBackups,
		out string message,
		bool allowDowngrade = false)
	{
		message = "";

		if (!VerifySnapshot(snapshotDir, out SnapshotManifest? manifest, out string error) || manifest == null) {
			message = error;
			return false;
		}

		if (!string.Equals(manifest.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase)) {
			message = "Snapshot belongs to a different shared world.";
			return false;
		}

		if (!allowDowngrade && manifest.Version < record.LocalVersion) {
			message = $"Refusing to install older snapshot v{manifest.Version} over local v{record.LocalVersion}.";
			return false;
		}

		string worldPath = SharedWorldPaths.WorldPath(record);
		string twldPath = SharedWorldPaths.TwldPath(record);

		if (!SharedWorldPaths.IsInsideSavePath(worldPath)) {
			message = "Refusing to install a world outside the tModLoader save directory.";
			return false;
		}

		try {
			if (keepBackup && (File.Exists(worldPath) || File.Exists(twldPath)))
				BackupCurrent(record, worldPath, twldPath, maxBackups);

			Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);

			string tempWld = worldPath + ".sharedworld.tmp";
			string tempTwld = twldPath + ".sharedworld.tmp";

			File.Copy(Path.Combine(snapshotDir, "world.wld"), tempWld, overwrite: true);
			File.Copy(Path.Combine(snapshotDir, "world.twld"), tempTwld, overwrite: true);

			if (!HashUtil.VerifyContentHash(tempWld, tempTwld, manifest.ContentHash)) {
				TryDelete(tempWld);
				TryDelete(tempTwld);
				message = "Temporary copy failed hash verification; local world was not changed.";
				return false;
			}

			File.Move(tempWld, worldPath, overwrite: true);
			File.Move(tempTwld, twldPath, overwrite: true);

			record.LocalVersion = manifest.Version;
			record.LocalHash = manifest.ContentHash;
			record.LastParentHash = manifest.ParentHash;
			record.LastHostSteamId = manifest.LastHostSteamId;
			record.LastUpdatedUtc = manifest.TimestampUtc;
			record.WorldName = string.IsNullOrWhiteSpace(manifest.WorldName) ? record.WorldName : manifest.WorldName;
			record.Mods = manifest.Mods ?? new List<ModEntry>();

			foreach (KeyValuePair<string, ulong> participant in manifest.Participants ?? new()) {
				if (!string.IsNullOrWhiteSpace(participant.Key))
					record.Participants[participant.Key] = participant.Value;
			}

			message = $"Installed snapshot v{manifest.Version} ({HashUtil.Short(manifest.ContentHash)}).";
			Log.Info($"{message} World: {worldPath}");
			return true;
		}
		catch (Exception e) {
			Log.Error("Failed to install snapshot.", e);
			message = "Failed to install snapshot: " + e.Message;
			return false;
		}
	}

	public static void BackupCurrent(SharedWorldRecord record, string worldPath, string twldPath, int maxBackups)
	{
		try {
			if (!File.Exists(worldPath) && !File.Exists(twldPath))
				return;

			string dir = SharedWorldPaths.BackupDir(record.WorldId, record.LocalVersion);
			Directory.CreateDirectory(dir);

			if (File.Exists(worldPath))
				File.Copy(worldPath, Path.Combine(dir, "world.wld"), overwrite: true);
			if (File.Exists(twldPath))
				File.Copy(twldPath, Path.Combine(dir, "world.twld"), overwrite: true);

			if (!string.IsNullOrEmpty(record.LocalHash)) {
				var manifest = new SnapshotManifest {
					WorldId = record.WorldId,
					WorldName = record.WorldName,
					Version = record.LocalVersion,
					ContentHash = record.LocalHash,
					ParentHash = record.LastParentHash,
					LastHostSteamId = record.LastHostSteamId,
					TimestampUtc = record.LastUpdatedUtc,
					Mods = record.Mods,
					Participants = new Dictionary<string, ulong>(record.Participants),
					History = record.History.Select(Clone).ToList()
				};
				WriteManifest(dir, manifest);
			}

			PruneBackups(record.WorldId, maxBackups);
		}
		catch (Exception e) {
			Log.Warn("Could not create a local world backup.", e);
		}
	}

	public static void PruneBackups(string worldId, int keep)
	{
		try {
			string root = Path.Combine(SharedWorldPaths.BackupsRoot, worldId);
			if (!Directory.Exists(root) || keep < 1)
				return;

			foreach (string dir in Directory.GetDirectories(root)
				.OrderByDescending(Directory.GetLastWriteTimeUtc)
				.Skip(keep)) {
				try {
					Directory.Delete(dir, recursive: true);
				}
				catch { }
			}
		}
		catch (Exception e) {
			Log.Warn("Could not prune old backups.", e);
		}
	}

	public static byte[] ZipSnapshot(string snapshotDir)
	{
		using var memory = new MemoryStream();
		using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true)) {
			foreach (string name in new[] { "manifest.json", "world.wld", "world.twld" }) {
				string path = Path.Combine(snapshotDir, name);
				if (!File.Exists(path))
					throw new FileNotFoundException("Snapshot file missing: " + path, path);

				ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
				using Stream entryStream = entry.Open();
				using FileStream file = File.OpenRead(path);
				file.CopyTo(entryStream);
			}
		}

		return memory.ToArray();
	}

	public static bool ExtractZip(byte[] data, string destination, out string error)
	{
		error = "";
		try {
			string fullDest = Path.GetFullPath(destination);
			Directory.CreateDirectory(fullDest);

			using var memory = new MemoryStream(data, writable: false);
			using var archive = new ZipArchive(memory, ZipArchiveMode.Read);

			foreach (ZipArchiveEntry entry in archive.Entries) {
				string target = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
				if (!target.StartsWith(fullDest.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
					error = "Archive contains an invalid path.";
					return false;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				entry.ExtractToFile(target, overwrite: true);
			}

			return true;
		}
		catch (Exception e) {
			error = "Could not extract snapshot archive: " + e.Message;
			return false;
		}
	}

	public static void PruneSnapshots(string worldId, int keep, params string[] protectedDirs)
	{
		try {
			string root = Path.Combine(SharedWorldPaths.SnapshotsRoot, worldId);
			if (!Directory.Exists(root) || keep < 1)
				return;

			var protectedSet = new HashSet<string>(
				(protectedDirs ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath),
				StringComparer.OrdinalIgnoreCase);

			foreach (string dir in Directory.GetDirectories(root)
				.Where(d => !protectedSet.Contains(Path.GetFullPath(d)))
				.OrderByDescending(Directory.GetLastWriteTimeUtc)
				.Skip(keep)) {
				try {
					Directory.Delete(dir, recursive: true);
				}
				catch { }
			}
		}
		catch (Exception e) {
			Log.Warn("Could not prune old local snapshots.", e);
		}
	}

	public static string EnsureFreeWorldFileName(string desiredName)
	{
		string fileName = SharedWorldPaths.SafeWorldFileName(desiredName);
		string path = Path.Combine(Main.WorldPath, fileName);

		if (!File.Exists(path))
			return fileName;

		string baseName = Path.GetFileNameWithoutExtension(fileName);
		for (int i = 2; i < 1000; i++) {
			string candidate = $"{baseName} ({i}).wld";
			if (!File.Exists(Path.Combine(Main.WorldPath, candidate)))
				return candidate;
		}

		return $"{baseName} ({Guid.NewGuid():N}).wld";
	}

	public static VersionRecord Clone(VersionRecord v) => new() {
		Version = v.Version,
		Hash = v.Hash,
		ParentHash = v.ParentHash,
		HostSteamId = v.HostSteamId,
		TimestampUtc = v.TimestampUtc,
		WorldName = v.WorldName,
		SourceItemId = v.SourceItemId
	};

	private static SnapshotBuildResult Fail(string message, Exception? exception = null)
		=> new() { Success = false, Message = message, Exception = exception };

	private static void TryDelete(string path)
	{
		try {
			if (File.Exists(path))
				File.Delete(path);
		}
		catch { }
	}
}
