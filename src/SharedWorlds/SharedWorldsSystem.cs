using System.IO;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using SharedWorlds.Core;
using SharedWorlds.Net;

namespace SharedWorlds;

public sealed class SharedWorldsSystem : ModSystem
{
	public override void OnModLoad()
	{
		SharedWorldsService.Instance.Initialize();
	}

	public override void OnModUnload()
	{
		SharedWorldsService.Instance.Unload();
	}

	public override void OnWorldLoad()
	{
		SharedWorldsService.Instance.OnWorldLoad();
	}

	public override void OnWorldUnload()
	{
		SharedWorldsService.Instance.OnWorldUnload();
	}

	public override void PostUpdateEverything()
	{
		SharedWorldsService.Instance.Tick();
		SharedWorldsService.Instance.TickAutoSave();
		SharedWorldsNet.Tick();
	}

	public override void PostUpdateInput()
	{
		// Main.Update returns before PostUpdateEverything on the title screen, so menu-only
		// work (queued Open to friends and Host & Play continuation) needs this hook.
		if (Main.gameMenu)
			SharedWorldsService.Instance.Tick();
	}

	public override void SaveWorldData(TagCompound tag)
	{
		string worldId = SharedWorldsService.Instance.CurrentWorldId;
		if (!string.IsNullOrWhiteSpace(worldId))
			tag["SharedWorldId"] = worldId;
	}

	public override void LoadWorldData(TagCompound tag)
	{
		if (tag.TryGet("SharedWorldId", out string worldId))
			SharedWorldsService.Instance.SetCurrentWorldIdFromLoad(worldId);
	}

	public override void NetSend(BinaryWriter writer)
	{
		string worldId = SharedWorldsService.Instance.CurrentWorldId;
		writer.Write(worldId ?? "");
	}

	public override void NetReceive(BinaryReader reader)
	{
		string worldId = reader.ReadString();
		if (!string.IsNullOrWhiteSpace(worldId))
			SharedWorldsService.Instance.SetCurrentWorldIdFromLoad(worldId);
	}
}
