using System.IO;
using Steamworks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.Net;
using Terraria.Social;
using SharedWorlds.Core;

namespace SharedWorlds.Net;

public static class SharedWorldsNet
{
	private const byte MsgHello = 1;
	private const byte MsgClientItem = 2;
	private const byte MsgRequestSnapshot = 3;
	private const byte MsgSession = 4;
	private const byte MsgSnapshotStart = 10;
	private const byte MsgSnapshotChunk = 11;
	private const byte MsgSnapshotEnd = 12;
	private const byte MsgSnapshotAck = 13;
	private const byte MsgNotice = 14;

	private static DateTime _lastSessionBroadcastUtc = DateTime.MinValue;

	public static void HandlePacket(Mod mod, BinaryReader reader, int whoAmI)
	{
		try {
			byte type = reader.ReadByte();
			switch (type) {
				case MsgHello:
					ClientHandleHello(reader);
					break;
				case MsgSession:
					ClientHandleSession(reader);
					break;
				case MsgClientItem:
					ServerHandleClientItem(reader, whoAmI);
					break;
				case MsgRequestSnapshot:
					ServerHandleRequestSnapshot(reader, whoAmI);
					break;
				case MsgSnapshotStart:
					PeerTransferService.HandleSnapshotStart(reader, whoAmI);
					break;
				case MsgSnapshotChunk:
					PeerTransferService.HandleSnapshotChunk(reader, whoAmI);
					break;
				case MsgSnapshotEnd:
					PeerTransferService.HandleSnapshotEnd(reader, whoAmI);
					break;
				case MsgSnapshotAck:
					ClientHandleAck(reader);
					break;
				case MsgNotice:
					HandleNotice(reader);
					break;
				default:
					Log.Warn($"Unknown Shared Worlds packet type {type}.");
					break;
			}
		}
		catch (Exception e) {
			Log.Error("Failed to handle a Shared Worlds packet.", e);
		}
	}

	public static void Tick()
	{
		if (Main.netMode == NetmodeID.Server) {
			PeerTransferService.TickServer();

			if ((DateTime.UtcNow - _lastSessionBroadcastUtc).TotalSeconds >= 5) {
				_lastSessionBroadcastUtc = DateTime.UtcNow;
				BroadcastSession();
			}
		}
	}

	private static ModPacket NewPacket(byte type, int capacity = 1024)
	{
		ModPacket packet = SharedWorldsMod.Instance.GetPacket(capacity);
		packet.Write(type);
		return packet;
	}

	public static void ServerSendHello(int whoAmI)
	{
		try {
			SharedWorldRecord? record = SharedWorldsService.Instance.CurrentRecord;
			if (record == null)
				return;

			List<ulong> roster = GetConnectedSteamIds();
			ulong hostSteamId = WorkshopService.CurrentSteamId;
			if (hostSteamId == 0)
				hostSteamId = record.LastHostSteamId;

			ModPacket packet = NewPacket(MsgHello, 8192);
			packet.Write(record.WorldId);
			packet.Write(record.WorldName);
			packet.Write(record.LocalVersion);
			packet.Write(record.LocalHash ?? "");
			packet.Write(record.LastHostSteamId);

			int count = Math.Min(record.Participants.Count, 200);
			packet.Write(count);
			foreach (KeyValuePair<string, ulong> participant in record.Participants.Take(count)) {
				packet.Write(participant.Key);
				packet.Write(participant.Value);
			}

			packet.Write(hostSteamId);
			packet.Write(GetLobbyId());
			packet.Write(record.LastUpdatedUtc.Ticks);
			WriteRoster(packet, roster);
			packet.Send(toClient: whoAmI);
		}
		catch (Exception e) {
			Log.Warn("Could not send Shared Worlds hello packet.", e);
		}
	}

	private static void ClientHandleHello(BinaryReader reader)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient)
			return;

		string worldId = reader.ReadString();
		string worldName = reader.ReadString();
		int version = reader.ReadInt32();
		string hash = reader.ReadString();
		ulong lastHost = reader.ReadUInt64();
		int count = reader.ReadInt32();
		if (count < 0 || count > 200)
			return;

		var participants = new Dictionary<string, ulong>();
		for (int i = 0; i < count; i++) {
			string steamId = reader.ReadString();
			ulong itemId = reader.ReadUInt64();
			participants[steamId] = itemId;
		}

		ulong hostSteamId = reader.ReadUInt64();
		ulong lobbyId = reader.ReadUInt64();
		long snapshotTicks = reader.ReadInt64();
		List<ulong> roster = ReadRoster(reader);

		SharedWorldRecord record = SharedWorldsService.Instance.EnsureRemoteWorld(worldId, worldName, lastHost, participants);
		SharedWorldsSessionState.Set(new SharedWorldsLiveSession {
			WorldId = worldId,
			WorldName = record.WorldName,
			HostSteamId = hostSteamId,
			LobbyId = lobbyId,
			Version = version,
			ContentHash = hash,
			SnapshotUtc = snapshotTicks <= 0 ? default : new DateTime(snapshotTicks, DateTimeKind.Utc),
			Roster = roster
		});

		Log.Info($"Joined shared world '{record.WorldName}' (host {WorkshopService.PersonaName(hostSteamId)}, live v{version}, {HashUtil.Short(hash)}).");

		if (WorkshopService.CurrentSteamId != 0) {
			ModPacket packet = NewPacket(MsgClientItem, 128);
			packet.Write(record.WorldId);
			packet.Write(WorkshopService.CurrentSteamId);
			packet.Write(record.WorkshopItemId);
			packet.Send();
		}

		// Keep a local copy ready so this client can take over immediately if the host
		// disappears. The server throttles snapshot requests.
		if (!string.Equals(record.LocalHash, hash, StringComparison.OrdinalIgnoreCase))
			ClientRequestSnapshot(worldId, publish: false);
	}

	private static void ClientHandleSession(BinaryReader reader)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient)
			return;

		string worldId = reader.ReadString();
		ulong hostSteamId = reader.ReadUInt64();
		ulong lobbyId = reader.ReadUInt64();
		int version = reader.ReadInt32();
		string hash = reader.ReadString();
		long snapshotTicks = reader.ReadInt64();
		List<ulong> roster = ReadRoster(reader);

		if (string.IsNullOrWhiteSpace(worldId))
			return;

		SharedWorldsSessionState.Set(new SharedWorldsLiveSession {
			WorldId = worldId,
			WorldName = SharedWorldsService.Instance.Registry.FindByWorldId(worldId)?.WorldName ?? "",
			HostSteamId = hostSteamId,
			LobbyId = lobbyId,
			Version = version,
			ContentHash = hash,
			SnapshotUtc = snapshotTicks <= 0 ? default : new DateTime(snapshotTicks, DateTimeKind.Utc),
			Roster = roster
		});
	}

	private static void BroadcastSession()
	{
		try {
			SharedWorldRecord? record = SharedWorldsService.Instance.CurrentRecord;
			if (record == null || record.LastUpdatedUtc == default)
				return;

			List<ulong> roster = GetConnectedSteamIds();
			ulong hostSteamId = WorkshopService.CurrentSteamId;
			if (hostSteamId == 0)
				hostSteamId = record.LastHostSteamId;

			foreach (RemoteClient client in Netplay.Clients) {
				if (!client.IsConnected())
					continue;

				ModPacket packet = NewPacket(MsgSession, 512 + roster.Count * 8);
				packet.Write(record.WorldId);
				packet.Write(hostSteamId);
				packet.Write(GetLobbyId());
				packet.Write(record.LocalVersion);
				packet.Write(record.LocalHash ?? "");
				packet.Write(record.LastUpdatedUtc.Ticks);
				WriteRoster(packet, roster);
				packet.Send(toClient: client.Id);
			}
		}
		catch (Exception e) {
			Log.Warn("Could not broadcast the Shared Worlds session roster.", e);
		}
	}

	private static void WriteRoster(ModPacket packet, List<ulong> roster)
	{
		int count = Math.Min(roster.Count, 200);
		packet.Write(count);
		for (int i = 0; i < count; i++)
			packet.Write(roster[i]);
	}

	private static List<ulong> ReadRoster(BinaryReader reader)
	{
		int count = reader.ReadInt32();
		if (count < 0 || count > 200)
			return new List<ulong>();

		var roster = new List<ulong>(count);
		for (int i = 0; i < count; i++) {
			ulong steamId = reader.ReadUInt64();
			if (steamId != 0 && !roster.Contains(steamId))
				roster.Add(steamId);
		}

		return roster;
	}

	public static List<ulong> GetConnectedSteamIds()
	{
		var roster = new List<ulong>();
		try {
			for (int i = 0; i < 256; i++) {
				if (!Netplay.Clients[i].IsConnected())
					continue;

				if (Netplay.Clients[i].Socket?.GetRemoteAddress() is SteamAddress address &&
					address.SteamId.m_SteamID != 0 &&
					!roster.Contains(address.SteamId.m_SteamID)) {
					roster.Add(address.SteamId.m_SteamID);
				}
			}

			ulong own = WorkshopService.CurrentSteamId;
			if (own != 0 && !roster.Contains(own))
				roster.Add(own);
		}
		catch (Exception e) {
			Log.Warn("Could not build the Shared Worlds player roster.", e);
		}

		roster.Sort();
		return roster;
	}

	private static ulong GetLobbyId()
	{
		try {
			return SocialAPI.Network?.GetLobbyId() ?? 0;
		}
		catch {
			return 0;
		}
	}

	public static void ClientSendOwnItem(string worldId, ulong itemId)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient || WorkshopService.CurrentSteamId == 0)
			return;

		ModPacket packet = NewPacket(MsgClientItem, 128);
		packet.Write(worldId);
		packet.Write(WorkshopService.CurrentSteamId);
		packet.Write(itemId);
		packet.Send();
	}

	private static void ServerHandleClientItem(BinaryReader reader, int whoAmI)
	{
		if (Main.netMode != NetmodeID.Server)
			return;

		string worldId = reader.ReadString();
		ulong steamId = reader.ReadUInt64();
		ulong itemId = reader.ReadUInt64();

		if (steamId == 0)
			return;

		SharedWorldsService.Instance.NoteParticipant(worldId, steamId, itemId);
		Log.Info($"Player {whoAmI} linked Workshop item {itemId} for world {worldId}.");
	}

	public static void ClientRequestSnapshot(string worldId, bool publish)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient)
			return;

		ModPacket packet = NewPacket(MsgRequestSnapshot, 256);
		packet.Write(worldId);
		packet.Write(publish);
		packet.Send();
	}

	private static void ServerHandleRequestSnapshot(BinaryReader reader, int whoAmI)
	{
		if (Main.netMode != NetmodeID.Server)
			return;

		string worldId = reader.ReadString();
		bool publish = reader.ReadBoolean();
		PeerTransferService.HandleSnapshotRequest(whoAmI, worldId, publish);
	}

	public static void ServerSendSnapshotStart(int whoAmI, string transferId, string worldId, string worldName,
		int version, string contentHash, string dataHash, int totalBytes, int chunkSize, int totalChunks)
	{
		ModPacket packet = NewPacket(MsgSnapshotStart, 512);
		packet.Write(transferId);
		packet.Write(worldId);
		packet.Write(worldName);
		packet.Write(version);
		packet.Write(contentHash);
		packet.Write(dataHash);
		packet.Write(totalBytes);
		packet.Write(chunkSize);
		packet.Write(totalChunks);
		packet.Send(toClient: whoAmI);
	}

	public static void ServerSendSnapshotChunk(int whoAmI, string transferId, int index, byte[] data)
	{
		ModPacket packet = NewPacket(MsgSnapshotChunk, PeerTransferService.ChunkSize + 128);
		packet.Write(transferId);
		packet.Write(index);
		packet.Write(data.Length);
		packet.Write(data);
		packet.Send(toClient: whoAmI);
	}

	public static void ServerSendSnapshotEnd(int whoAmI, string transferId, string dataHash)
	{
		ModPacket packet = NewPacket(MsgSnapshotEnd, 128);
		packet.Write(transferId);
		packet.Write(dataHash);
		packet.Send(toClient: whoAmI);
	}

	public static void ClientSendAck(string transferId, bool success, string message)
	{
		if (Main.netMode != NetmodeID.MultiplayerClient)
			return;

		ModPacket packet = NewPacket(MsgSnapshotAck, 256);
		packet.Write(transferId);
		packet.Write(success);
		packet.Write(message ?? "");
		packet.Send();
	}

	private static void ClientHandleAck(BinaryReader reader)
	{
		if (Main.netMode != NetmodeID.Server)
			return;

		string transferId = reader.ReadString();
		bool success = reader.ReadBoolean();
		string message = reader.ReadString();
		// The server does not know the sender's player index from this packet alone;
		// PeerTransferService matches by transfer id.
		PeerTransferService.HandleAckByTransferId(transferId, success, message);
	}

	private static void HandleNotice(BinaryReader reader)
	{
		string message = reader.ReadString();
		if (Main.netMode == NetmodeID.MultiplayerClient)
			Main.NewText(message, 180, 220, 255);
		else
			Log.Info(message);
	}

	public static void ServerSendNotice(int whoAmI, string message)
	{
		ModPacket packet = NewPacket(MsgNotice, 512);
		packet.Write(message);
		packet.Send(toClient: whoAmI);
	}
}
