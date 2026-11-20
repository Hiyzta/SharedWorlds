using System.Collections.Concurrent;
using System.IO.Compression;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.Net;
using SharedWorlds.Core;

namespace SharedWorlds.Net;

/// <summary>
/// Server-authoritative snapshot transfer. The host asks the server to save/publish a snapshot;
/// the server zips it, sends it in small verified chunks, and uploads the same snapshot to Workshop.
/// </summary>
public static class PeerTransferService
{
	public const int ChunkSize = 16 * 1024;
	private const int ChunksPerTick = 4;
	private const int MaxTransferBytes = 64 * 1024 * 1024;

	private sealed class Plan
	{
		public string Id = "";
		public string WorldId = "";
		public string WorldName = "";
		public int Version;
		public string ContentHash = "";
		public string DataHash = "";
		public byte[] Data = Array.Empty<byte>();
		public List<byte[]> Chunks = new();
		public DateTime CreatedUtc = DateTime.UtcNow;
	}

	private sealed class Outgoing
	{
		public Plan Plan = null!;
		public int WhoAmI;
		public int NextChunk;
		public bool EndSent;
		public DateTime StartedUtc = DateTime.UtcNow;
	}

	private sealed class Incoming
	{
		public string Id = "";
		public string WorldId = "";
		public string WorldName = "";
		public int Version;
		public string ContentHash = "";
		public string DataHash = "";
		public int TotalBytes;
		public int ChunkSize;
		public int TotalChunks;
		public int NextChunk;
		public MemoryStream Buffer = new();
		public DateTime StartedUtc = DateTime.UtcNow;
	}

	private static readonly Dictionary<int, Outgoing> OutgoingByPlayer = new();
	private static readonly Dictionary<string, Incoming> IncomingById = new();
	private static readonly HashSet<int> KnownActivePlayers = new();
	private static DateTime _lastPublishRequest = DateTime.MinValue;

	public static void Reset()
	{
		OutgoingByPlayer.Clear();
		IncomingById.Clear();
		KnownActivePlayers.Clear();
	}

	public static void TickServer()
	{
		if (Main.netMode != NetmodeID.Server)
			return;

		// Hello on the first tick where the player is fully in the world.
		for (int i = 0; i < 256; i++) {
			bool active = Netplay.Clients[i].IsConnected() && Main.player[i].active;
			if (active && KnownActivePlayers.Add(i))
				SharedWorldsNet.ServerSendHello(i);
			else if (!active && KnownActivePlayers.Remove(i))
				OutgoingByPlayer.Remove(i);
		}

		foreach (Outgoing outgoing in OutgoingByPlayer.Values.ToList()) {
			if (!Netplay.Clients[outgoing.WhoAmI].IsConnected()) {
				OutgoingByPlayer.Remove(outgoing.WhoAmI);
				continue;
			}

			if ((DateTime.UtcNow - outgoing.StartedUtc).TotalMinutes > 10) {
				OutgoingByPlayer.Remove(outgoing.WhoAmI);
				SharedWorldsNet.ServerSendNotice(outgoing.WhoAmI, "Snapshot transfer timed out.");
				continue;
			}

			if (!outgoing.EndSent) {
				for (int n = 0; n < ChunksPerTick && outgoing.NextChunk < outgoing.Plan.Chunks.Count; n++) {
					byte[] chunk = outgoing.Plan.Chunks[outgoing.NextChunk];
					SharedWorldsNet.ServerSendSnapshotChunk(outgoing.WhoAmI, outgoing.Plan.Id, outgoing.NextChunk, chunk);
					outgoing.NextChunk++;
				}

				if (outgoing.NextChunk >= outgoing.Plan.Chunks.Count) {
					SharedWorldsNet.ServerSendSnapshotEnd(outgoing.WhoAmI, outgoing.Plan.Id, outgoing.Plan.DataHash);
					outgoing.EndSent = true;
				}
			}
		}
	}

	public static void HandleAck(int whoAmI, string transferId, bool success, string message)
	{
		if (Main.netMode != NetmodeID.Server)
			return;

		if (OutgoingByPlayer.TryGetValue(whoAmI, out Outgoing? outgoing) &&
			string.Equals(outgoing.Plan.Id, transferId, StringComparison.OrdinalIgnoreCase)) {
			OutgoingByPlayer.Remove(whoAmI);
			Main.NewText($"Snapshot transfer to player {whoAmI} {(success ? "verified" : "failed")}: {message}", 180, 220, 255);
			Log.Info($"Snapshot transfer to player {whoAmI} {(success ? "succeeded" : "failed")}: {message}");
		}
	}

	public static void HandleAckByTransferId(string transferId, bool success, string message)
	{
		if (Main.netMode != NetmodeID.Server)
			return;

		KeyValuePair<int, Outgoing> match = OutgoingByPlayer.FirstOrDefault(pair =>
			string.Equals(pair.Value.Plan.Id, transferId, StringComparison.OrdinalIgnoreCase));

		if (match.Value != null)
			HandleAck(match.Key, transferId, success, message);
	}

	/// <summary>Handles a client request to save, publish and broadcast the current world snapshot.</summary>
	public static void HandleSnapshotRequest(int whoAmI, string worldId, bool publish)
	{
		if (Main.netMode != NetmodeID.Server)
			return;

		if ((DateTime.UtcNow - _lastPublishRequest).TotalSeconds < 10) {
			SharedWorldsNet.ServerSendNotice(whoAmI, "Please wait a few seconds before requesting another snapshot.");
			return;
		}

		_lastPublishRequest = DateTime.UtcNow;

		SharedWorldRecord? record = SharedWorldsService.Instance.Registry.FindByWorldId(worldId);
		if (record == null) {
			SharedWorldsNet.ServerSendNotice(whoAmI, "This world is not registered as a shared world.");
			return;
		}

		if (!string.Equals(record.WorldId, SharedWorldsService.Instance.CurrentWorldId, StringComparison.OrdinalIgnoreCase)) {
			SharedWorldsNet.ServerSendNotice(whoAmI, "The server is currently running a different shared world.");
			return;
		}

		Main.NewText("Saving the world and preparing a shared snapshot...", 180, 220, 255);
		PublishAndBroadcast(record, publish);
	}

	private static void PublishAndBroadcast(SharedWorldRecord record, bool publish)
	{
		try {
			WorldFile.SaveWorld();

			string worldPath = Main.worldPathName;
			string twldPath = Path.ChangeExtension(worldPath, ".twld");
			if (!File.Exists(worldPath) || !File.Exists(twldPath)) {
				Main.NewText("Snapshot failed: saved world files were not found.", 255, 160, 120);
				return;
			}

			ulong hostId = WorkshopService.CurrentSteamId;
			if (hostId == 0)
				hostId = record.LastHostSteamId;

			int version = Math.Max(record.HighestKnownVersion, record.LocalVersion) + 1;
			string parent = record.LocalVersion == 0 ? "" : record.LocalHash;
			SnapshotBuildResult built = SnapshotService.Build(
				record,
				worldPath,
				twldPath,
				hostId,
				SharedWorldsService.GetCurrentMods(),
				record.History,
				parent,
				version,
				SharedWorldsConfig.Current.MaxHistoryEntries);

			if (!built.Success) {
				Main.NewText("Snapshot failed: " + built.Message, 255, 160, 120);
				Log.Warn(built.Message);
				return;
			}

			if (built.Unchanged) {
				Main.NewText("No world changes since the last snapshot.", 180, 220, 255);
				return;
			}

			record.LocalVersion = built.Version;
			record.LocalHash = built.ContentHash;
			record.LastParentHash = built.Manifest!.ParentHash;
			record.LastHostSteamId = hostId;
			record.LastUpdatedUtc = built.Manifest.TimestampUtc;
			record.Mods = SharedWorldsService.GetCurrentMods();
			record.PendingUpload = publish;
			record.PendingSnapshotDir = publish ? Path.GetRelativePath(SharedWorldPaths.Root, built.SnapshotDir) : null;
			record.PendingVersion = publish ? built.Version : 0;
			record.PendingHash = publish ? built.ContentHash : null;
			record.PendingNeedsEula = false;
			record.PendingUpdatedUtc = DateTime.UtcNow;

			SharedWorldsService.Instance.Registry.RecordVersion(record.WorldId, built.Manifest.AsVersionRecord, SharedWorldsConfig.Current.MaxHistoryEntries);
			record.History = SharedWorldsService.Instance.Registry.FindByWorldId(record.WorldId)?.History ?? record.History;
			SharedWorldsService.Instance.Registry.Update(record);
			SharedWorldsService.Instance.Registry.Save();

			BeginBroadcast(record, built.SnapshotDir, built.Manifest, Enumerable.Range(0, 256).Where(i => Netplay.Clients[i].IsConnected() && Main.player[i].active));
			Main.NewText($"Snapshot v{built.Version} is being sent to connected players.", 180, 220, 255);

			if (publish) {
				string snapshotDir = built.SnapshotDir;
				SnapshotManifest manifest = built.Manifest;
				_ = Task.Run(async () => {
					try {
						UploadResult result = await SharedWorldsService.Instance.UploadSnapshotAsync(record, snapshotDir, manifest, null, CancellationToken.None).ConfigureAwait(false);
						if (result.Success) {
							if (string.Equals(record.LocalHash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase)) {
								record.PendingUpload = false;
								record.PendingSnapshotDir = null;
								record.PendingVersion = 0;
								record.PendingHash = null;
								record.PendingNeedsEula = false;
								record.PendingUpdatedUtc = DateTime.UtcNow;
								SharedWorldsService.Instance.Registry.Update(record);
								SharedWorldsService.Instance.Registry.Save();
							}

							Main.QueueMainThreadAction(() => Main.NewText($"Published shared world v{manifest.Version} to Steam Workshop.", 160, 255, 180));
						}
						else {
							record.PendingNeedsEula = result.NeedsEula;
							record.PendingUpdatedUtc = DateTime.UtcNow;
							SharedWorldsService.Instance.Registry.Update(record);
							SharedWorldsService.Instance.Registry.Save();
							Main.QueueMainThreadAction(() => Main.NewText("Workshop upload pending: " + result.Message, 255, 210, 140));
							Log.Warn("Workshop upload pending: " + result.Message);
						}
					}
					catch (Exception e) {
						Log.Error("Background Workshop upload failed.", e);
					}
				});
			}
		}
		catch (Exception e) {
			Log.Error("Snapshot publish/broadcast failed.", e);
			Main.NewText("Snapshot failed: " + e.Message, 255, 160, 120);
		}
	}

	public static void BeginBroadcast(SharedWorldRecord record, string snapshotDir, SnapshotManifest manifest, IEnumerable<int> players)
	{
		byte[] data;
		try {
			data = SnapshotService.ZipSnapshot(snapshotDir);
		}
		catch (Exception e) {
			Log.Error("Could not zip snapshot for peer transfer.", e);
			return;
		}

		if (data.Length == 0 || data.Length > MaxTransferBytes) {
			Log.Warn($"Snapshot zip size {data.Length} is outside the transfer limit.");
			return;
		}

		var plan = new Plan {
			Id = Guid.NewGuid().ToString("N"),
			WorldId = record.WorldId,
			WorldName = record.WorldName,
			Version = manifest.Version,
			ContentHash = manifest.ContentHash,
			DataHash = HashUtil.HashBytesHex(data),
			Data = data
		};

		for (int offset = 0; offset < data.Length; offset += ChunkSize) {
			int length = Math.Min(ChunkSize, data.Length - offset);
			var chunk = new byte[length];
			System.Buffer.BlockCopy(data, offset, chunk, 0, length);
			plan.Chunks.Add(chunk);
		}

		foreach (int player in players.Distinct()) {
			if (!Netplay.Clients[player].IsConnected())
				continue;

			OutgoingByPlayer[player] = new Outgoing {
				Plan = plan,
				WhoAmI = player,
				StartedUtc = DateTime.UtcNow
			};

			SharedWorldsNet.ServerSendSnapshotStart(
				player,
				plan.Id,
				plan.WorldId,
				plan.WorldName,
				plan.Version,
				plan.ContentHash,
				plan.DataHash,
				plan.Data.Length,
				ChunkSize,
				plan.Chunks.Count);
		}
	}

	public static void HandleSnapshotStart(BinaryReader reader, int whoAmI)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient)
			return;

		string id = reader.ReadString();
		string worldId = reader.ReadString();
		string worldName = reader.ReadString();
		int version = reader.ReadInt32();
		string contentHash = reader.ReadString();
		string dataHash = reader.ReadString();
		int totalBytes = reader.ReadInt32();
		int chunkSize = reader.ReadInt32();
		int totalChunks = reader.ReadInt32();

		if (string.IsNullOrWhiteSpace(id) || totalBytes <= 0 || totalBytes > MaxTransferBytes ||
			chunkSize <= 0 || chunkSize > ChunkSize || totalChunks <= 0) {
			return;
		}

		var incoming = new Incoming {
			Id = id,
			WorldId = worldId,
			WorldName = worldName,
			Version = version,
			ContentHash = contentHash,
			DataHash = dataHash,
			TotalBytes = totalBytes,
			ChunkSize = chunkSize,
			TotalChunks = totalChunks
		};

		IncomingById[id] = incoming;
	}

	public static void HandleSnapshotChunk(BinaryReader reader, int whoAmI)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient)
			return;

		string id = reader.ReadString();
		int index = reader.ReadInt32();
		int length = reader.ReadInt32();
		if (length < 0 || length > ChunkSize)
			return;

		byte[] data = reader.ReadBytes(length);
		if (data.Length != length)
			return;

		if (!IncomingById.TryGetValue(id, out Incoming? incoming))
			return;

		if (index != incoming.NextChunk) {
			IncomingById.Remove(id);
			SharedWorldsNet.ClientSendAck(id, false, "chunk order mismatch");
			return;
		}

		if (incoming.Buffer.Length + length > incoming.TotalBytes) {
			IncomingById.Remove(id);
			SharedWorldsNet.ClientSendAck(id, false, "transfer too large");
			return;
		}

		incoming.Buffer.Write(data, 0, length);
		incoming.NextChunk++;
	}

	public static void HandleSnapshotEnd(BinaryReader reader, int whoAmI)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient)
			return;

		string id = reader.ReadString();
		string dataHash = reader.ReadString();

		if (!IncomingById.TryGetValue(id, out Incoming? incoming)) {
			SharedWorldsNet.ClientSendAck(id, false, "unknown transfer");
			return;
		}

		IncomingById.Remove(id);

		try {
			byte[] data = incoming.Buffer.ToArray();
			if (data.Length != incoming.TotalBytes) {
				SharedWorldsNet.ClientSendAck(id, false, "incomplete transfer");
				return;
			}

			if (!string.Equals(HashUtil.HashBytesHex(data), dataHash, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(dataHash, incoming.DataHash, StringComparison.OrdinalIgnoreCase)) {
				SharedWorldsNet.ClientSendAck(id, false, "transport hash mismatch");
				return;
			}

			string tempDir = Path.Combine(SharedWorldPaths.TempRoot, "receive-" + id);
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, true);

			if (!SnapshotService.ExtractZip(data, tempDir, out string extractError)) {
				SharedWorldsNet.ClientSendAck(id, false, extractError);
				return;
			}

			if (!SnapshotService.VerifySnapshot(tempDir, out SnapshotManifest? manifest, out string verifyError) || manifest == null) {
				SharedWorldsNet.ClientSendAck(id, false, verifyError);
				return;
			}

			if (!string.Equals(manifest.WorldId, incoming.WorldId, StringComparison.OrdinalIgnoreCase) ||
				manifest.Version != incoming.Version ||
				!string.Equals(manifest.ContentHash, incoming.ContentHash, StringComparison.OrdinalIgnoreCase)) {
				SharedWorldsNet.ClientSendAck(id, false, "snapshot metadata mismatch");
				return;
			}

			SharedWorldRecord record = SharedWorldsService.Instance.EnsureRemoteWorld(
				manifest.WorldId,
				string.IsNullOrWhiteSpace(manifest.WorldName) ? incoming.WorldName : manifest.WorldName,
				manifest.LastHostSteamId,
				manifest.Participants);

			SharedWorldsService.Instance.EnsureLocalTarget(record);

			if (!SnapshotService.Install(record, tempDir, keepBackup: true, SharedWorldsConfig.Current.MaxLocalBackups, out string installMessage)) {
				SharedWorldsNet.ClientSendAck(id, false, installMessage);
				return;
			}

			foreach (VersionRecord history in manifest.History)
				SharedWorldsService.Instance.Registry.RecordVersion(record.WorldId, history, SharedWorldsConfig.Current.MaxHistoryEntries);

			foreach (KeyValuePair<string, ulong> participant in manifest.Participants) {
				if (!string.IsNullOrWhiteSpace(participant.Key))
					record.Participants[participant.Key] = participant.Value;
			}

			record.History = SharedWorldsService.Instance.Registry.FindByWorldId(record.WorldId)?.History ?? record.History;
			SharedWorldsService.Instance.Registry.Update(record);
			SharedWorldsService.Instance.Registry.Save();

			ClientSideNotify($"Received shared world '{record.WorldName}' v{manifest.Version}.");
			SharedWorldsNet.ClientSendAck(id, true, $"v{manifest.Version} installed");
		}
		catch (Exception e) {
			Log.Error("Could not install received snapshot.", e);
			SharedWorldsNet.ClientSendAck(id, false, e.Message);
		}
		finally {
			try {
				string tempDir = Path.Combine(SharedWorldPaths.TempRoot, "receive-" + id);
				if (Directory.Exists(tempDir))
					Directory.Delete(tempDir, true);
			}
			catch { }
		}
	}

	public static void ClientSideNotify(string message)
	{
		if (Main.netMode == NetmodeID.MultiplayerClient)
			Main.NewText(message, 160, 255, 180);
		else
			Log.Info(message);
	}
}
