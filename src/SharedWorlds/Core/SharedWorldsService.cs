using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;

namespace SharedWorlds.Core;

public sealed class SharedWorldsService
{
	public static SharedWorldsService Instance { get; } = new();

	public SharedWorldRegistryStore Registry { get; } = new();

	private static readonly SemaphoreSlim UploadGate = new(1, 1);

	private string _currentWorldId = "";
	private string _sessionStartHash = "";
	private int _sessionStartVersion;
	private bool _loadedAsMultiplayerClient;
	private DateTime _pendingOpenToFriendsSince;

	public string CurrentWorldId => _currentWorldId;
	public string LastStatus { get; private set; } = "";
	public SharedWorldRecord? CurrentRecord => Registry.FindByWorldId(_currentWorldId);

	public event Action? Changed;

	public void Initialize()
	{
		SharedWorldPaths.EnsureCreated();
		Registry.Load();
		Registry.Changed += () => Changed?.Invoke();
		Log.Info($"Registry loaded: {Registry.Snapshot().Count} shared world(s).");
	}

	public void Unload()
	{
		_currentWorldId = "";
		_sessionStartHash = "";
		HostAndPlay.ClearPending();
	}

	private static SharedWorldsConfig Config => SharedWorldsConfig.Current;

	public SharedWorldRecord? FindByFileName(string? fileName)
		=> Registry.FindByFileName(fileName);

	public SharedWorldRecord RegisterWorld(WorldFileData data, out string error)
	{
		error = "";

		if (data == null) {
			error = "No world selected.";
			return null!;
		}

		if (!data.IsValid) {
			error = "That world file is not valid and cannot be registered.";
			return null!;
		}

		if (data.IsCloudSave) {
			error = "Steam Cloud worlds are not supported for sharing yet. Move the world to local storage first.";
			return null!;
		}

		string path = Path.GetFullPath(data.Path);
		if (!SharedWorldPaths.IsInsideSavePath(path) ||
			!path.StartsWith(Path.GetFullPath(Main.WorldPath), StringComparison.OrdinalIgnoreCase)) {
			error = "Only worlds inside the tModLoader worlds folder can be shared.";
			return null!;
		}

		string fileName = Path.GetFileName(path);
		SharedWorldRecord? existing = Registry.FindByFileName(fileName);
		if (existing != null) {
			error = "This world is already registered as a shared world.";
			return existing;
		}

		string twld = Path.ChangeExtension(path, ".twld");
		string hash = File.Exists(twld) ? HashUtil.ComputeContentHash(path, twld) : "";

		ulong ownId = WorkshopService.CurrentSteamId;
		var record = new SharedWorldRecord {
			WorldId = Guid.NewGuid().ToString("D"),
			WorldName = string.IsNullOrWhiteSpace(data.Name) ? Path.GetFileNameWithoutExtension(fileName) : data.Name,
			WorldFileName = fileName,
			LocalVersion = 0,
			LocalHash = hash,
			LastHostSteamId = ownId,
			LastUpdatedUtc = DateTime.UtcNow,
			WorkshopStorageEnabled = true,
			Mods = GetCurrentMods()
		};

		record = Registry.Add(record);
		Registry.Save();

		LastStatus = $"Registered '{record.WorldName}'.";
		Log.Info(LastStatus);
		Changed?.Invoke();
		return record;
	}

	public void UnregisterWorld(string worldId)
	{
		if (Registry.Remove(worldId)) {
			Registry.Save();
			LastStatus = "Shared world registration removed. The world file and Workshop items were not deleted.";
			Changed?.Invoke();
		}
	}

	public void SetCurrentWorldIdFromLoad(string? worldId)
	{
		if (string.IsNullOrWhiteSpace(worldId))
			return;

		_currentWorldId = worldId;

		if (Main.netMode == NetmodeID.MultiplayerClient)
			_loadedAsMultiplayerClient = true;

		SharedWorldRecord? existing = Registry.FindByWorldId(worldId);
		if (existing != null) {
			string? loadedName = Main.worldPathName == null ? null : Path.GetFileName(Main.worldPathName);
			if (Main.netMode != NetmodeID.MultiplayerClient &&
				!string.IsNullOrWhiteSpace(loadedName) &&
				!string.Equals(existing.WorldFileName, loadedName, StringComparison.OrdinalIgnoreCase)) {
				existing.WorldFileName = loadedName;
				Registry.Update(existing);
				Registry.Save();
			}

			return;
		}

		// Registry was lost or this world arrived from a snapshot/peer transfer. Recover a
		// registration from the world file itself, but never do this on a multiplayer client
		// (the client does not own a local authoritative file).
		if (Main.netMode == NetmodeID.MultiplayerClient)
			return;

		string? path = Main.worldPathName;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return;

		string twld = Path.ChangeExtension(path, ".twld");
		string hash = File.Exists(twld) ? HashUtil.ComputeContentHash(path, twld) : "";

		var record = new SharedWorldRecord {
			WorldId = worldId,
			WorldName = string.IsNullOrWhiteSpace(Main.worldName) ? Path.GetFileNameWithoutExtension(path) : Main.worldName,
			WorldFileName = Path.GetFileName(path),
			LocalVersion = 0,
			LocalHash = hash,
			LastUpdatedUtc = DateTime.UtcNow,
			WorkshopStorageEnabled = true,
			Mods = GetCurrentMods()
		};

		record = Registry.Add(record);
		Registry.Save();
		Log.Warn($"Recovered a shared world registration from the world file: '{record.WorldName}'.");
	}

	public void OnWorldLoad()
	{
		_currentWorldId = "";
		_sessionStartHash = "";
		_sessionStartVersion = 0;
		_loadedAsMultiplayerClient = Main.netMode == NetmodeID.MultiplayerClient;

		string? path = Main.worldPathName;
		if (string.IsNullOrWhiteSpace(path))
			return;

		SharedWorldRecord? record = Registry.FindByFileName(Path.GetFileName(path));
		if (record == null)
			return;

		_currentWorldId = record.WorldId;
		_sessionStartVersion = record.LocalVersion;

		string twld = Path.ChangeExtension(path, ".twld");
		if (File.Exists(path) && File.Exists(twld)) {
			try {
				_sessionStartHash = HashUtil.ComputeContentHash(path, twld);
				RecordLocalHead(record, _sessionStartHash);
				_sessionStartVersion = record.LocalVersion;
			}
			catch (Exception e) {
				Log.Warn("Could not hash the loaded world.", e);
				_sessionStartHash = record.LocalHash;
			}
		}
		else {
			_sessionStartHash = record.LocalHash;
		}

		LastStatus = $"Loaded shared world '{record.WorldName}' (local v{record.LocalVersion}).";

		if (Main.netMode == NetmodeID.SinglePlayer && Config.EnableWorkshopStorage && record.WorkshopStorageEnabled) {
			_ = Task.Run(async () => {
				try {
					SharedWorldRecord? fresh = Registry.FindByWorldId(record.WorldId);
					if (fresh == null)
						return;

					SyncReport report = await SyncAsync(fresh, downloadIfNewer: false).ConfigureAwait(false);
					if (report.Status is SyncStatus.RemoteNewer or SyncStatus.Fork or SyncStatus.Error) {
						string message = report.Status == SyncStatus.Fork
							? $"Shared Worlds: '{fresh.WorldName}' has diverged from a published version. Save & exit and use the Shared Worlds screen before making more progress."
							: $"Shared Worlds: a newer snapshot of '{fresh.WorldName}' is available. Save & exit and sync before making more progress.";
						Main.QueueMainThreadAction(() => Main.NewText(message, 255, 210, 140));
					}
					else if (fresh.PendingUpload) {
						Main.QueueMainThreadAction(() => Main.NewText($"Shared Worlds: a previous snapshot of '{fresh.WorldName}' is still waiting to upload.", 255, 210, 140));
					}
				}
				catch (Exception e) {
					Log.Warn("Could not check for a newer shared-world snapshot on load.", e);
				}
			});
		}
	}

	/// <summary>Called after the authoritative save. Builds and (when possible) uploads the snapshot.</summary>
	public void OnWorldUnload()
	{
		bool loadedAsClient = _loadedAsMultiplayerClient;
		string? worldPath = Main.worldPathName;
		string? currentId = _currentWorldId;
		string parentHash = _sessionStartHash;
		int startVersion = _sessionStartVersion;

		_currentWorldId = "";
		_sessionStartHash = "";
		_loadedAsMultiplayerClient = false;

		if (string.IsNullOrWhiteSpace(currentId) || string.IsNullOrWhiteSpace(worldPath))
			return;

		SharedWorldRecord? record = Registry.FindByWorldId(currentId);
		if (record == null)
			return;

		if (string.Equals(HostAndPlay.PendingOpenToFriendsWorldId, record.WorldId, StringComparison.OrdinalIgnoreCase)) {
			HostAndPlay.PendingOpenToFriendsReady = true;
			_pendingOpenToFriendsSince = DateTime.UtcNow;
			return;
		}

		// A multiplayer client is not authoritative. Main.netMode may already be 0 by the
		// time OnWorldUnload runs (Terraria resets it during disconnect), so use the mode
		// captured at world load instead.
		if (loadedAsClient)
			return;

		string twldPath = Path.ChangeExtension(worldPath, ".twld");
		if (!File.Exists(worldPath) || !File.Exists(twldPath))
			return;

		PublishWorld(record, worldPath, twldPath, parentHash, startVersion);
	}

	private void PublishWorld(SharedWorldRecord record, string worldPath, string twldPath, string parentHash, int startVersion)
	{
		try {
			ulong hostId = WorkshopService.CurrentSteamId;
			if (hostId == 0)
				hostId = record.LastHostSteamId;

			if (string.IsNullOrEmpty(parentHash))
				parentHash = record.LocalHash;

			int nextVersion = Math.Max(record.HighestKnownVersion, startVersion) + 1;
			string snapshotParent = record.LocalVersion == 0 ? "" : parentHash;

			SnapshotBuildResult built = SnapshotService.Build(
				record,
				worldPath,
				twldPath,
				hostId,
				GetCurrentMods(),
				record.History,
				snapshotParent,
				nextVersion,
				Config.MaxHistoryEntries);

			if (!built.Success) {
				LastStatus = built.Message;
				Log.Warn(built.Message);
				return;
			}

			if (built.Unchanged) {
				LastStatus = built.Message;

				if (record.PendingUpload && record.WorkshopStorageEnabled && WorkshopService.IsAvailable) {
					UploadResult retry = RetryPendingAsync(record, null, CancellationToken.None).GetAwaiter().GetResult();
					LastStatus = retry.Success
						? $"Published pending snapshot v{record.LocalVersion} to Workshop."
						: "Snapshot unchanged; pending Workshop upload is still waiting: " + retry.Message;
				}

				return;
			}

			record.LocalVersion = built.Version;
			record.LocalHash = built.ContentHash;
			record.LastParentHash = snapshotParent;
			record.LastHostSteamId = hostId;
			record.LastUpdatedUtc = built.Manifest!.TimestampUtc;
			record.Mods = GetCurrentMods();
			record.PendingUpload = false;
			record.PendingSnapshotDir = null;
			record.PendingVersion = 0;
			record.PendingHash = null;
			record.PendingNeedsEula = false;

			Registry.RecordVersion(record.WorldId, built.Manifest.AsVersionRecord, Config.MaxHistoryEntries);
			record.History = Registry.FindByWorldId(record.WorldId)?.History ?? record.History;

			bool shouldUpload = record.WorkshopStorageEnabled && Config.AutoUploadOnWorldExit;
			if (shouldUpload && WorkshopService.IsAvailable) {
				// Mark the snapshot pending before starting the upload. If the process exits
				// before Steam finishes, the snapshot is retried on the next launch instead
				// of being silently lost.
				QueuePending(record, built.SnapshotDir, built.Version, built.ContentHash);
				Registry.Update(record);
				Registry.Save();

				if (Main.dedServ || Main.netMode == NetmodeID.Server) {
					// The server is about to exit: the main thread will not pump Steam
					// callbacks anymore, so a helper thread pumps them while we wait.
					UploadResult upload = UploadWithPump(record, built.SnapshotDir, built.Manifest);
					CompleteUpload(record, built.SnapshotDir, built.Version, built.ContentHash, upload);
				}
				else {
					SharedWorldRecord uploadRecord = record;
					string snapshotDir = built.SnapshotDir;
					SnapshotManifest manifest = built.Manifest;
					_ = Task.Run(() => {
						UploadResult upload;
						try {
							upload = UploadSnapshotAsync(uploadRecord, snapshotDir, manifest, null, CancellationToken.None)
								.GetAwaiter().GetResult();
						}
						catch (Exception e) {
							upload = new UploadResult {
								Success = false,
								Pending = true,
								ItemId = uploadRecord.WorkshopItemId,
								Message = "Workshop upload failed: " + e.Message,
								Exception = e
							};
						}

						Main.QueueMainThreadAction(() => CompleteUpload(uploadRecord, snapshotDir, manifest.Version, manifest.ContentHash, upload));
					});
				}
			}
			else {
				if (record.WorkshopStorageEnabled) {
					MarkPending(record, built.SnapshotDir, built.Version, built.ContentHash, new UploadResult {
						Success = false,
						Pending = true,
						Message = !Config.AutoUploadOnWorldExit
							? "Automatic Workshop upload is disabled."
							: "Steam Workshop is unavailable in this process."
					});
				}
				else {
					LastStatus = $"Saved '{record.WorldName}' v{record.LocalVersion} locally. Workshop storage is disabled for this world.";
					Log.Info(LastStatus);
				}
			}

			Registry.Update(record);
			Registry.Save();
			SnapshotService.PruneSnapshots(record.WorldId, Math.Max(3, Config.MaxLocalBackups / 2), built.SnapshotDir);
			Changed?.Invoke();
		}
		catch (Exception e) {
			Log.Error("Failed while publishing the world snapshot.", e);
			LastStatus = "Failed to publish snapshot: " + e.Message;
		}
	}

	private void QueuePending(SharedWorldRecord record, string snapshotDir, int version, string hash)
	{
		record.PendingUpload = true;
		record.PendingSnapshotDir = Path.GetRelativePath(SharedWorldPaths.Root, snapshotDir);
		record.PendingVersion = version;
		record.PendingHash = hash;
		record.PendingNeedsEula = false;
	}

	private UploadResult UploadWithPump(SharedWorldRecord record, string snapshotDir, SnapshotManifest manifest)
	{
		UploadResult? result = null;
		Exception? error = null;

		var thread = new Thread(() => {
			try {
				Task<UploadResult> task = UploadSnapshotAsync(record, snapshotDir, manifest, null, CancellationToken.None);
				while (!task.IsCompleted) {
					WorkshopService.PumpCallbacks();
					Thread.Sleep(10);
				}

				result = task.GetAwaiter().GetResult();
			}
			catch (Exception e) {
				error = e;
			}
		}) {
			IsBackground = false,
			Name = "SharedWorlds Workshop Upload"
		};

		thread.Start();
		int timeout = Math.Max(30, Config.UploadTimeoutSeconds) + 30;
		if (!thread.Join(TimeSpan.FromSeconds(timeout))) {
			return new UploadResult {
				Success = false,
				Pending = true,
				ItemId = record.WorkshopItemId,
				Message = "Workshop upload timed out during server shutdown. It will be retried next time."
			};
		}

		if (error != null) {
			return new UploadResult {
				Success = false,
				Pending = true,
				ItemId = record.WorkshopItemId,
				Message = "Workshop upload failed during server shutdown: " + error.Message,
				Exception = error
			};
		}

		return result ?? new UploadResult {
			Success = false,
			Pending = true,
			ItemId = record.WorkshopItemId,
			Message = "Workshop upload did not return a result."
		};
	}

	private void CompleteUpload(SharedWorldRecord record, string snapshotDir, int version, string hash, UploadResult upload)
	{
		SharedWorldRecord? latest = Registry.FindByWorldId(record.WorldId) ?? record;

		if (upload.Success) {
			if (latest.PendingVersion == version ||
				string.Equals(latest.PendingHash, hash, StringComparison.OrdinalIgnoreCase)) {
				latest.PendingUpload = false;
				latest.PendingSnapshotDir = null;
				latest.PendingVersion = 0;
				latest.PendingHash = null;
				latest.PendingNeedsEula = false;
			}

			LastStatus = $"Published '{latest.WorldName}' v{version} to Workshop.";
			Log.Info(LastStatus);
		}
		else if (latest.LocalVersion > version && latest.PendingVersion == version) {
			// A newer local snapshot superseded this upload; it is queued separately.
			latest.PendingUpload = false;
			latest.PendingSnapshotDir = null;
			latest.PendingVersion = 0;
			latest.PendingHash = null;
			latest.PendingNeedsEula = false;
			LastStatus = $"Skipped Workshop upload of older v{version}; local v{latest.LocalVersion} is newer.";
			Log.Info(LastStatus);
		}
		else {
			MarkPending(latest, snapshotDir, version, hash, upload);
		}

		Registry.Update(latest);
		Registry.Save();
		Changed?.Invoke();
	}

	private void MarkPending(SharedWorldRecord record, string snapshotDir, int version, string hash, UploadResult upload)
	{
		record.PendingUpload = true;
		record.PendingSnapshotDir = Path.GetRelativePath(SharedWorldPaths.Root, snapshotDir);
		record.PendingVersion = version;
		record.PendingHash = hash;
		record.PendingNeedsEula = upload.NeedsEula;
		LastStatus = "Snapshot saved locally; Workshop upload is pending. " + upload.Message;

		if (upload.NeedsEula)
			WorkshopService.ShowEula();

		Log.Warn(LastStatus);
	}

	public async Task<UploadResult> RetryPendingAsync(SharedWorldRecord record, IProgress<(float, string)>? progress, CancellationToken token)
	{
		if (!record.PendingUpload || string.IsNullOrWhiteSpace(record.PendingSnapshotDir))
			return new UploadResult { Success = false, Message = "No pending upload for this world." };

		string dir = Path.Combine(SharedWorldPaths.Root, record.PendingSnapshotDir);
		if (!SharedWorldPaths.IsInsideSavePath(dir))
			return new UploadResult { Success = false, Message = "Pending snapshot path is outside the tModLoader save directory." };
		if (!SnapshotService.VerifySnapshot(dir, out SnapshotManifest? manifest, out string error) || manifest == null)
			return new UploadResult { Success = false, Message = "Pending snapshot is unreadable: " + error };

		UploadResult result = await UploadSnapshotAsync(record, dir, manifest, progress, token).ConfigureAwait(false);
		if (result.Success) {
			record.PendingUpload = false;
			record.PendingSnapshotDir = null;
			record.PendingVersion = 0;
			record.PendingHash = null;
			record.PendingNeedsEula = false;
			Registry.Update(record);
			Registry.Save();
			Changed?.Invoke();
		}

		return result;
	}

	public async Task<UploadResult> UploadSnapshotAsync(
		SharedWorldRecord record,
		string snapshotDir,
		SnapshotManifest manifest,
		IProgress<(float, string)>? progress,
		CancellationToken token)
	{
		await UploadGate.WaitAsync(token).ConfigureAwait(false);
		try {
			// A background upload of an older snapshot must not overwrite a newer local snapshot.
			SharedWorldRecord? latest = Registry.FindByWorldId(record.WorldId);
			if (latest != null && latest.LocalVersion > manifest.Version) {
				return new UploadResult {
					Success = false,
					Pending = true,
					ItemId = record.WorkshopItemId,
					Message = $"Skipped uploading older v{manifest.Version}; local v{latest.LocalVersion} is newer."
				};
			}

			return await UploadSnapshotCoreAsync(record, snapshotDir, manifest, progress, token).ConfigureAwait(false);
		}
		finally {
			UploadGate.Release();
		}
	}

	private async Task<UploadResult> UploadSnapshotCoreAsync(
		SharedWorldRecord record,
		string snapshotDir,
		SnapshotManifest manifest,
		IProgress<(float, string)>? progress,
		CancellationToken token)
	{
		if (!WorkshopService.IsAvailable)
			return new UploadResult { Success = false, Pending = true, Message = "Steam client API is unavailable." };

		ulong ownId = WorkshopService.CurrentSteamId;
		if (ownId == 0)
			return new UploadResult { Success = false, Pending = true, Message = "Could not determine the current Steam account." };

		if (record.WorkshopItemId == 0) {
			progress?.Report((0f, "Creating Workshop item"));
			var create = await WorkshopService.CreateItemAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
			if (!create.Success) {
				if (create.NeedsEula)
					WorkshopService.ShowEula();
				return new UploadResult { Success = false, Pending = true, NeedsEula = create.NeedsEula, Message = create.Message };
			}

			record.WorkshopItemId = create.ItemId;
			record.Participants[ownId.ToString()] = create.ItemId;
			Registry.Update(record);
			Registry.Save();

			if (Main.netMode == NetmodeID.MultiplayerClient)
				SharedWorlds.Net.SharedWorldsNet.ClientSendOwnItem(record.WorldId, create.ItemId);

			// Continue into the upload even when the EULA is pending: the same update sets
			// FriendsOnly visibility, so a newly created item is never left public.
			if (create.NeedsEula)
				WorkshopService.ShowEula();
		}

		manifest.Participants[ownId.ToString()] = record.WorkshopItemId;
		SnapshotService.WriteManifest(snapshotDir, manifest);

		var metadata = new WorkshopMetadata {
			WorldId = manifest.WorldId,
			WorldName = manifest.WorldName,
			Version = manifest.Version,
			ContentHash = manifest.ContentHash,
			ParentHash = manifest.ParentHash,
			LastHostSteamId = manifest.LastHostSteamId,
			TimestampUnix = new DateTimeOffset(manifest.TimestampUtc).ToUnixTimeSeconds(),
			Participants = manifest.Participants.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value)
		};

		string title = $"Shared World: {manifest.WorldName}";
		string description =
			"Storage item for the Shared Worlds tModLoader mod.\n\n" +
			"This is NOT a mod. Do not subscribe to or install it as a mod.\n" +
			$"World ID: {manifest.WorldId}\n" +
			$"Latest version: {manifest.Version}\n" +
			$"Content hash: {manifest.ContentHash}\n" +
			$"Last host: {WorkshopService.PersonaName(manifest.LastHostSteamId)}\n\n" +
			"Only the owner of this Steam account can update this item. " +
			"The mod manages it automatically; you only need to accept the Steam Workshop Legal Agreement once.";

		UploadResult result = await WorkshopService.UploadAsync(
			record.WorkshopItemId,
			snapshotDir,
			metadata,
			title,
			description,
			$"v{manifest.Version} {HashUtil.Short(manifest.ContentHash)}",
			TimeSpan.FromSeconds(Math.Max(30, Config.UploadTimeoutSeconds)),
			progress).ConfigureAwait(false);

		if (result.NeedsEula) {
			WorkshopService.ShowEula();
			return new UploadResult {
				Success = false,
				Pending = true,
				NeedsEula = true,
				ItemId = result.ItemId,
				Message = "The snapshot uploaded, but Steam requires you to accept the Workshop Legal Agreement before friends can see the item. Accept it, then publish again."
			};
		}

		return result;
	}

	/// <summary>
	/// If the local file was changed without going through our snapshot path, treat it as a new
	/// local head instead of silently comparing against a stale hash. It will be published on exit.
	/// </summary>
	private void RefreshLocalHead(SharedWorldRecord record)
	{
		try {
			string wld = SharedWorldPaths.WorldPath(record);
			string twld = SharedWorldPaths.TwldPath(record);
			if (!File.Exists(wld) || !File.Exists(twld))
				return;

			string hash = HashUtil.ComputeContentHash(wld, twld);
			RecordLocalHead(record, hash);
		}
		catch (Exception e) {
			Log.Warn("Could not refresh the local snapshot head.", e);
		}
	}

	private void RecordLocalHead(SharedWorldRecord record, string hash)
	{
		if (string.IsNullOrWhiteSpace(hash) ||
			string.Equals(hash, record.LocalHash, StringComparison.OrdinalIgnoreCase))
			return;

		VersionRecord? known = record.History.FirstOrDefault(v =>
			string.Equals(v.Hash, hash, StringComparison.OrdinalIgnoreCase));

		if (known != null) {
			record.LocalHash = known.Hash;
			record.LocalVersion = known.Version;
			record.LastParentHash = known.ParentHash;
		}
		else if (string.IsNullOrEmpty(record.LocalHash)) {
			record.LocalHash = hash;
		}
		else {
			int next = record.HighestKnownVersion + 1;
			string parent = record.LocalHash;
			record.LastParentHash = parent;
			record.LocalVersion = next;
			record.LocalHash = hash;
			record.LastUpdatedUtc = DateTime.UtcNow;

			Registry.RecordVersion(record.WorldId, new VersionRecord {
				Version = next,
				Hash = hash,
				ParentHash = parent,
				HostSteamId = WorkshopService.CurrentSteamId,
				TimestampUtc = DateTime.UtcNow,
				WorldName = record.WorldName,
				SourceItemId = "local-unsnapshotted"
			}, Config.MaxHistoryEntries);

			record.History = Registry.FindByWorldId(record.WorldId)?.History ?? record.History;
			Log.Warn($"Local world '{record.WorldName}' changed outside the snapshot path; recorded it as local v{next}.");
		}

		Registry.Update(record);
		Registry.Save();
		Changed?.Invoke();
	}

	/// <summary>Queries all known participants, detects forks, and optionally installs the newest snapshot.</summary>
	public async Task<SyncReport> SyncAsync(
		SharedWorldRecord record,
		bool downloadIfNewer,
		IProgress<string>? progress = null,
		CancellationToken token = default)
	{
		var report = new SyncReport { Record = record };
		RefreshLocalHead(record);

		if (!WorkshopService.IsAvailable) {
			report.Status = SyncStatus.WorkshopUnavailable;
			report.Message = "Steam Workshop is unavailable. Local world and peer transfer still work.";
			return report;
		}

		var ids = new HashSet<ulong>(record.Participants.Values.Where(id => id != 0));
		if (record.WorkshopItemId != 0)
			ids.Add(record.WorkshopItemId);

		if (ids.Count == 0 && record.Participants.Count == 0) {
			report.Status = SyncStatus.LocalOnly;
			report.Message = "This world has no Workshop item yet. It will be created when you publish the world.";
			return report;
		}

		progress?.Report("Querying Steam Workshop item metadata...");
		List<WorkshopItemInfo> items = await WorkshopService.QueryItemsAsync(ids, TimeSpan.FromSeconds(Math.Max(10, Config.QueryTimeoutSeconds))).ConfigureAwait(false);

		var warnings = new List<string>();
		bool participantsChanged = false;

		// Discovery: a participant may have created their item after the last exchange.
		// Query their published items and match on the world ID in metadata.
		int discoveryBudget = 20;
		foreach (string steamIdText in record.Participants.Keys.ToList()) {
			if (discoveryBudget-- <= 0)
				break;

			if (!ulong.TryParse(steamIdText, out ulong participantSteamId) || participantSteamId == 0)
				continue;

			bool haveKnownItem = record.Participants.TryGetValue(steamIdText, out ulong knownItem) && knownItem != 0;
			bool knownItemWasReturned = haveKnownItem && items.Any(i => i.ItemId == knownItem);
			if (knownItemWasReturned)
				continue;

			if (participantSteamId != WorkshopService.CurrentSteamId && !WorkshopService.IsFriend(participantSteamId))
				continue;

			List<WorkshopItemInfo> published = await WorkshopService.QueryUserItemsAsync(
				participantSteamId,
				TimeSpan.FromSeconds(Math.Max(10, Config.QueryTimeoutSeconds))).ConfigureAwait(false);

			foreach (WorkshopItemInfo item in published) {
				if (item.Metadata == null ||
					!string.Equals(item.Metadata.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase))
					continue;

				if (items.All(i => i.ItemId != item.ItemId))
					items.Add(item);

				if (!record.Participants.TryGetValue(steamIdText, out ulong existing) || existing != item.ItemId) {
					record.Participants[steamIdText] = item.ItemId;
					participantsChanged = true;
				}

				if (participantSteamId == WorkshopService.CurrentSteamId && record.WorkshopItemId != item.ItemId) {
					record.WorkshopItemId = item.ItemId;
					participantsChanged = true;
				}
			}
		}

		if (participantsChanged) {
			Registry.Update(record);
			Registry.Save();
		}

		report.Items = items;

		var graph = new VersionGraph();
		graph.AddRange(record.History);

		var localRecord = new VersionRecord {
			Version = record.LocalVersion,
			Hash = record.LocalHash,
			ParentHash = record.LastParentHash,
			HostSteamId = record.LastHostSteamId,
			TimestampUtc = record.LastUpdatedUtc,
			WorldName = record.WorldName
		};

		if (!string.IsNullOrWhiteSpace(record.LocalHash))
			graph.Add(localRecord);

		var remote = new List<(VersionRecord Record, ulong ItemId)>();
		var unknownItems = new List<WorkshopItemInfo>();

		foreach (WorkshopItemInfo item in items) {
			if (item.Banned) {
				warnings.Add($"Workshop item {item.ItemId} is banned.");
				continue;
			}

			if (item.Metadata == null) {
				unknownItems.Add(item);
				continue;
			}

			if (!string.Equals(item.Metadata.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase)) {
				warnings.Add($"Workshop item {item.ItemId} belongs to a different world ID.");
				continue;
			}

			var version = new VersionRecord {
				Version = item.Metadata.Version,
				Hash = item.Metadata.ContentHash,
				ParentHash = item.Metadata.ParentHash,
				HostSteamId = item.Metadata.LastHostSteamId,
				TimestampUtc = item.Metadata.TimestampUnix == 0
					? item.UpdatedUtc
					: DateTimeOffset.FromUnixTimeSeconds(item.Metadata.TimestampUnix).UtcDateTime,
				WorldName = item.Metadata.WorldName,
				SourceItemId = item.ItemId.ToString()
			};

			graph.Add(version);
			remote.Add((version, item.ItemId));

			if (item.Metadata.Participants != null) {
				foreach (KeyValuePair<string, ulong> participant in item.Metadata.Participants) {
					if (participant.Value != 0 && !record.Participants.ContainsKey(participant.Key)) {
						record.Participants[participant.Key] = participant.Value;
						participantsChanged = true;
					}
				}
			}

			if (item.OwnerSteamId != 0 &&
				item.OwnerSteamId != WorkshopService.CurrentSteamId &&
				!WorkshopService.IsFriend(item.OwnerSteamId)) {
				warnings.Add($"You are not Steam friends with {WorkshopService.PersonaName(item.OwnerSteamId)}; their friends-only item may be inaccessible.");
			}
		}

		if (participantsChanged) {
			Registry.Update(record);
			Registry.Save();
		}

		// Unknown metadata (usually a very recent upload or an older item) is surfaced, not guessed.
		if (unknownItems.Count > 0) {
			warnings.Add($"{unknownItems.Count} Workshop item(s) had no readable version metadata; they were skipped.");
		}

		var candidates = remote.Select(r => r.Record).ToList();
		if (!string.IsNullOrWhiteSpace(record.LocalHash))
			candidates.Add(localRecord);

		List<VersionRecord> heads = graph.FindHeads(candidates);
		report.RemoteHeads = graph.FindHeads(remote.Select(r => r.Record))
			.Where(h => !string.Equals(h.Hash, record.LocalHash, StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (warnings.Count > 0)
			report.Message = string.Join(" ", warnings);
		else
			report.Message = "";

		VersionRecord? bestRemote = VersionGraph.PickBest(report.RemoteHeads);
		if (bestRemote == null) {
			if (string.IsNullOrWhiteSpace(record.LocalHash)) {
				report.Status = SyncStatus.LocalOnly;
				report.Message = "No published version was found. " + report.Message;
			}
			else {
				report.Status = SyncStatus.LocalNewer;
				report.Message = $"No newer published version was found; local v{record.LocalVersion} is the newest known. " + report.Message;
			}

			return report;
		}

		VersionRelation relation = graph.Compare(record.LocalHash, bestRemote.Hash);

		if (relation == VersionRelation.Same) {
			report.Status = SyncStatus.UpToDate;
			report.BestRemote = bestRemote;
			report.Message = $"Local snapshot is the newest known version (v{record.LocalVersion}). " + report.Message;
			return report;
		}

		if (relation == VersionRelation.LeftIsNewer) {
			if (heads.Count > 1) {
				report.Status = SyncStatus.Fork;
				report.ForkHeads = heads;
				report.BestRemote = bestRemote;
				report.Message = "The local version depends on a published version, but another published branch also exists. Choose a version manually. " + report.Message;
				return report;
			}

			report.Status = SyncStatus.LocalNewer;
			report.BestRemote = bestRemote;
			report.Message = $"Local snapshot v{record.LocalVersion} is newer than the published version. " + report.Message;
			return report;
		}

		if (relation == VersionRelation.Unrelated &&
			!string.IsNullOrWhiteSpace(record.LocalHash) &&
			bestRemote.Version < record.LocalVersion) {
			report.Status = SyncStatus.Fork;
			report.ForkHeads = new List<VersionRecord> { localRecord, bestRemote };
			report.BestRemote = bestRemote;
			report.Message = "An older published branch has diverged from the local world. Choose a version manually. " + report.Message;
			return report;
		}

		if (!downloadIfNewer) {
			report.Status = heads.Count > 1 ? SyncStatus.Fork : SyncStatus.RemoteNewer;
			report.BestRemote = bestRemote;
			report.ForkHeads = heads.Count > 1 ? heads : new List<VersionRecord>();
			report.Message = (heads.Count > 1
				? "More than one published version exists. Open this world and sync to inspect and resolve it. "
				: $"A newer snapshot is available on the Workshop (v{bestRemote.Version}). ") + report.Message;
			return report;
		}

		var source = remote.FirstOrDefault(r => string.Equals(r.Record.Hash, bestRemote.Hash, StringComparison.OrdinalIgnoreCase));
		if (source.ItemId == 0) {
			report.Status = SyncStatus.Error;
			report.Message = "The newest version was found, but no Workshop item can provide it.";
			return report;
		}

		progress?.Report($"Downloading v{bestRemote.Version} from Workshop item {source.ItemId}...");
		DownloadResult download = await WorkshopService.DownloadAsync(
			source.ItemId,
			TimeSpan.FromSeconds(Math.Max(30, Config.DownloadTimeoutSeconds)),
			null).ConfigureAwait(false);

		if (!download.Success || string.IsNullOrWhiteSpace(download.InstallFolder)) {
			report.Status = SyncStatus.Error;
			report.Message = download.Message;
			return report;
		}

		progress?.Report("Verifying downloaded snapshot...");
		if (!SnapshotService.VerifySnapshot(download.InstallFolder, out SnapshotManifest? manifest, out string verifyError) || manifest == null) {
			report.Status = SyncStatus.Error;
			report.Message = "Downloaded snapshot failed verification: " + verifyError;
			return report;
		}

		if (!string.Equals(manifest.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase)) {
			report.Status = SyncStatus.Error;
			report.Message = "Downloaded snapshot belongs to a different shared world.";
			return report;
		}

		if (manifest.Version != bestRemote.Version ||
			!string.Equals(manifest.ContentHash, bestRemote.Hash, StringComparison.OrdinalIgnoreCase)) {
			report.Status = SyncStatus.Error;
			report.Message = "Downloaded snapshot does not match the queried version metadata. Try again after Steam finishes propagating.";
			return report;
		}

		// The downloaded manifest contains the uploader's full history. This is what lets a
		// fast-forward work even when the metadata only exposes the newest hash.
		graph.AddRange(manifest.History);
		VersionRecord downloadedRecord = manifest.AsVersionRecord;
		downloadedRecord.SourceItemId = source.ItemId.ToString();
		graph.Add(downloadedRecord);

		var resolvedCandidates = remote.Select(r => r.Record).ToList();
		resolvedCandidates.Add(downloadedRecord);
		if (!string.IsNullOrWhiteSpace(record.LocalHash))
			resolvedCandidates.Add(localRecord);

		List<VersionRecord> resolvedHeads = graph.FindHeads(resolvedCandidates);
		if (resolvedHeads.Count > 1) {
			MergeRemoteKnowledge(record, manifest, bestRemote);
			report.Status = SyncStatus.Fork;
			report.ForkHeads = resolvedHeads;
			report.BestRemote = bestRemote;
			report.Message = "The published versions do not form a single chain. Choose which version to keep; neither was installed. " + report.Message;
			return report;
		}

		if (resolvedHeads.Count == 1 &&
			string.Equals(resolvedHeads[0].Hash, record.LocalHash, StringComparison.OrdinalIgnoreCase)) {
			MergeRemoteKnowledge(record, manifest, bestRemote);
			report.Status = SyncStatus.LocalNewer;
			report.BestRemote = bestRemote;
			report.Message = $"Local snapshot v{record.LocalVersion} is newer than the published version. " + report.Message;
			return report;
		}

		if (resolvedHeads.Count != 1 ||
			!string.Equals(resolvedHeads[0].Hash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase)) {
			MergeRemoteKnowledge(record, manifest, bestRemote);
			report.Status = SyncStatus.Error;
			report.BestRemote = resolvedHeads.FirstOrDefault() ?? bestRemote;
			report.Message = "The newest version is provided by a different Workshop item than the one just downloaded. Run sync again; nothing was overwritten.";
			return report;
		}

		if (!string.IsNullOrWhiteSpace(record.LocalHash) &&
			!graph.IsAncestor(record.LocalHash, manifest.ContentHash)) {
			MergeRemoteKnowledge(record, manifest, bestRemote);
			report.Status = SyncStatus.Fork;
			report.ForkHeads = new List<VersionRecord> { localRecord, bestRemote };
			report.BestRemote = bestRemote;
			report.Message = "The downloaded version is not a descendant of the local version. Choose a version manually; nothing was overwritten. " + report.Message;
			return report;
		}

		EnsureTargetFileName(record);

		if (!SnapshotService.Install(record, download.InstallFolder, keepBackup: true, Config.MaxLocalBackups, out string installMessage)) {
			report.Status = SyncStatus.Error;
			report.Message = installMessage;
			return report;
		}

		foreach (VersionRecord history in manifest.History)
			Registry.RecordVersion(record.WorldId, history, Config.MaxHistoryEntries);

		Registry.RecordVersion(record.WorldId, downloadedRecord, Config.MaxHistoryEntries);

		foreach (KeyValuePair<string, ulong> participant in manifest.Participants) {
			if (!string.IsNullOrWhiteSpace(participant.Key))
				record.Participants[participant.Key] = participant.Value;
		}

		record.History = Registry.FindByWorldId(record.WorldId)?.History ?? record.History;
		Registry.Update(record);
		Registry.Save();
		Changed?.Invoke();

		report.Status = SyncStatus.UpToDate;
		report.BestRemote = downloadedRecord;
		report.Message = $"Updated to v{manifest.Version} ({HashUtil.Short(manifest.ContentHash)}). " + installMessage;
		return report;
	}

	private void MergeRemoteKnowledge(SharedWorldRecord record, SnapshotManifest manifest, VersionRecord head)
	{
		foreach (VersionRecord history in manifest.History)
			Registry.RecordVersion(record.WorldId, history, Config.MaxHistoryEntries);

		Registry.RecordVersion(record.WorldId, manifest.AsVersionRecord, Config.MaxHistoryEntries);

		foreach (KeyValuePair<string, ulong> participant in manifest.Participants) {
			if (!string.IsNullOrWhiteSpace(participant.Key))
				record.Participants[participant.Key] = participant.Value;
		}

		record.History = Registry.FindByWorldId(record.WorldId)?.History ?? record.History;
		Registry.Update(record);
		Registry.Save();
		Changed?.Invoke();
	}

	/// <summary>Explicit fork resolution: install a chosen remote item even if it is not a descendant.</summary>
	public async Task<(bool Success, string Message)> InstallRemoteSnapshotAsync(
		SharedWorldRecord record,
		ulong itemId,
		IProgress<string>? progress,
		CancellationToken token)
	{
		if (!WorkshopService.IsAvailable)
			return (false, "Steam Workshop is unavailable.");

		progress?.Report("Downloading the selected Workshop snapshot...");
		DownloadResult download = await WorkshopService.DownloadAsync(
			itemId,
			TimeSpan.FromSeconds(Math.Max(30, Config.DownloadTimeoutSeconds)),
			null).ConfigureAwait(false);

		if (!download.Success || string.IsNullOrWhiteSpace(download.InstallFolder))
			return (false, download.Message);

		if (!SnapshotService.VerifySnapshot(download.InstallFolder, out SnapshotManifest? manifest, out string error) || manifest == null)
			return (false, "Downloaded snapshot failed verification: " + error);

		if (!string.Equals(manifest.WorldId, record.WorldId, StringComparison.OrdinalIgnoreCase))
			return (false, "The selected Workshop item belongs to a different shared world.");

		EnsureTargetFileName(record);

		if (!SnapshotService.Install(record, download.InstallFolder, keepBackup: true, Config.MaxLocalBackups, out string message, allowDowngrade: true))
			return (false, message);

		foreach (VersionRecord history in manifest.History)
			Registry.RecordVersion(record.WorldId, history, Config.MaxHistoryEntries);

		foreach (KeyValuePair<string, ulong> participant in manifest.Participants) {
			if (!string.IsNullOrWhiteSpace(participant.Key))
				record.Participants[participant.Key] = participant.Value;
		}

		record.History = Registry.FindByWorldId(record.WorldId)?.History ?? record.History;
		Registry.Update(record);
		Registry.Save();
		Changed?.Invoke();

		return (true, $"Installed v{manifest.Version} ({HashUtil.Short(manifest.ContentHash)}). The previous local branch was backed up. {message}");
	}

	public async Task<UploadResult> PublishNowAsync(SharedWorldRecord record, IProgress<(float, string)>? progress, CancellationToken token)
	{
		if (!record.PendingUpload || string.IsNullOrWhiteSpace(record.PendingSnapshotDir))
			return new UploadResult { Success = false, Message = "There is no pending snapshot. A snapshot is built when you save and exit a world." };

		return await RetryPendingAsync(record, progress, token).ConfigureAwait(false);
	}

	public string CreateInvite(SharedWorldRecord record)
	{
		ulong owner = WorkshopService.CurrentSteamId;
		if (owner == 0 || record.WorkshopItemId == 0)
			return "";

		return InviteCode.Create(record.WorldId, owner, record.WorkshopItemId, record.WorldName);
	}

	public SharedWorldRecord? ImportInvite(string code, out string error)
	{
		if (!InviteCode.TryParse(code, out InviteData? invite, out error) || invite == null)
			return null;

		SharedWorldRecord? existing = Registry.FindByWorldId(invite.WorldId);
		if (existing != null) {
			existing.Participants[invite.OwnerSteamId.ToString()] = invite.ItemId;
			Registry.Update(existing);
			Registry.Save();
			return existing;
		}

		string fileName = SnapshotService.EnsureFreeWorldFileName(string.IsNullOrWhiteSpace(invite.WorldName) ? "Shared World" : invite.WorldName);

		var record = new SharedWorldRecord {
			WorldId = invite.WorldId,
			WorldName = string.IsNullOrWhiteSpace(invite.WorldName) ? "Shared World" : invite.WorldName,
			WorldFileName = fileName,
			LocalVersion = 0,
			LocalHash = "",
			LastHostSteamId = invite.OwnerSteamId,
			LastUpdatedUtc = DateTime.UtcNow,
			Participants = new Dictionary<string, ulong> { [invite.OwnerSteamId.ToString()] = invite.ItemId },
			Mods = GetCurrentMods()
		};

		record = Registry.Add(record);
		Registry.Save();
		Changed?.Invoke();
		LastStatus = $"Imported shared world '{record.WorldName}'. Download a snapshot before hosting.";
		return record;
	}

	public void NoteParticipant(string worldId, ulong steamId, ulong itemId)
	{
		if (steamId == 0)
			return;

		SharedWorldRecord? record = Registry.FindByWorldId(worldId);
		if (record == null)
			return;

		if (record.Participants.TryGetValue(steamId.ToString(), out ulong existing) && existing == itemId)
			return;

		record.Participants[steamId.ToString()] = itemId;
		Registry.Update(record);
		Registry.Save();
		Changed?.Invoke();
	}

	public SharedWorldRecord EnsureRemoteWorld(string worldId, string worldName, ulong lastHost, IReadOnlyDictionary<string, ulong>? participants)
	{
		SharedWorldRecord? existing = Registry.FindByWorldId(worldId);
		if (existing != null) {
			bool changed = false;
			if (string.IsNullOrWhiteSpace(existing.WorldName) && !string.IsNullOrWhiteSpace(worldName)) {
				existing.WorldName = worldName;
				changed = true;
			}

			if (existing.LastHostSteamId == 0 && lastHost != 0) {
				existing.LastHostSteamId = lastHost;
				changed = true;
			}

			if (participants != null) {
				foreach (KeyValuePair<string, ulong> p in participants) {
					if (!string.IsNullOrWhiteSpace(p.Key) && !existing.Participants.ContainsKey(p.Key)) {
						existing.Participants[p.Key] = p.Value;
						changed = true;
					}
				}
			}

			if (changed) {
				Registry.Update(existing);
				Registry.Save();
				Changed?.Invoke();
			}

			return existing;
		}

		var record = new SharedWorldRecord {
			WorldId = worldId,
			WorldName = string.IsNullOrWhiteSpace(worldName) ? "Shared World" : worldName,
			WorldFileName = SnapshotService.EnsureFreeWorldFileName(string.IsNullOrWhiteSpace(worldName) ? "Shared World" : worldName),
			LocalVersion = 0,
			LocalHash = "",
			LastHostSteamId = lastHost,
			LastUpdatedUtc = DateTime.UtcNow,
			Participants = participants == null
				? new Dictionary<string, ulong>()
				: new Dictionary<string, ulong>(participants)
		};

		record = Registry.Add(record);
		Registry.Save();
		Changed?.Invoke();
		Log.Info($"Learned shared world '{record.WorldName}' from a multiplayer session.");
		return record;
	}

	public void QueueOpenToFriends(string worldId, out string error)
	{
		HostAndPlay.QueueOpenToFriends(worldId, out error);
	}

	/// <summary>Called from ModSystem.PostUpdateEverything on the client. Starts a queued Open to friends once the title screen is ready.</summary>
	public void Tick()
	{
		WorkshopService.PumpCallbacks();
		HostAndPlay.Tick();

		if (!HostAndPlay.PendingOpenToFriendsReady || Main.dedServ)
			return;

		if (!Main.gameMenu || Main.menuMode != 0)
			return;

		if ((DateTime.UtcNow - _pendingOpenToFriendsSince).TotalSeconds > 90) {
			HostAndPlay.ClearPending();
			LastStatus = "Open to friends timed out before the title screen was ready.";
			return;
		}

		string worldId = HostAndPlay.PendingOpenToFriendsWorldId;
		HostAndPlay.ClearPending();

		SharedWorldRecord? record = Registry.FindByWorldId(worldId);
		if (record == null)
			return;

		if (!HostAndPlay.Start(record, out string error)) {
			LastStatus = error;
			Log.Warn(error);
		}
	}

	public static List<ModEntry> GetCurrentMods()
		=> ModLoader.Mods
			.Where(m => m != null)
			.Select(m => new ModEntry { Name = m.Name, Version = m.Version.ToString() })
			.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();

	public void EnsureLocalTarget(SharedWorldRecord record) => EnsureTargetFileName(record);

	private static void EnsureTargetFileName(SharedWorldRecord record)
	{
		if (string.IsNullOrWhiteSpace(record.WorldFileName)) {
			record.WorldFileName = SnapshotService.EnsureFreeWorldFileName(
				string.IsNullOrWhiteSpace(record.WorldName) ? "Shared World" : record.WorldName);
			return;
		}

		string path = Path.Combine(Main.WorldPath, record.WorldFileName);
		if (record.LocalVersion == 0 &&
			string.IsNullOrEmpty(record.LocalHash) &&
			File.Exists(path)) {
			// The imported world has never been materialised here, and an unrelated file
			// now occupies the planned name. Never overwrite it by accident.
			record.WorldFileName = SnapshotService.EnsureFreeWorldFileName(record.WorldName);
		}
	}

	public string DescribeRecord(SharedWorldRecord record)
	{
		var sb = new StringBuilder();
		sb.AppendLine(record.WorldName);
		sb.AppendLine($"World ID: {record.WorldId}");
		sb.AppendLine($"Local snapshot: v{record.LocalVersion} {HashUtil.Short(record.LocalHash)}");
		sb.AppendLine($"Last host: {WorkshopService.PersonaName(record.LastHostSteamId)}");
		sb.AppendLine($"This account's item: {(record.WorkshopItemId == 0 ? "not created" : record.WorkshopItemId.ToString())}");
		sb.AppendLine($"Pending upload: {(record.PendingUpload ? "yes" : "no")}{(record.PendingNeedsEula ? " (Steam Workshop EULA not accepted)" : "")}");
		sb.AppendLine($"Participants: {record.Participants.Count}");
		foreach (KeyValuePair<string, ulong> p in record.Participants.OrderBy(p => p.Key))
			sb.AppendLine($"  {WorkshopService.PersonaName(ulong.TryParse(p.Key, out ulong id) ? id : 0)}: {p.Value}");
		return sb.ToString();
	}
}
