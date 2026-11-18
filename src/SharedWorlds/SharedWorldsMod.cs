using System.IO;
using Terraria.ModLoader;
using SharedWorlds.Core;
using SharedWorlds.Net;

namespace SharedWorlds;

public sealed class SharedWorldsMod : Mod
{
	public static SharedWorldsMod Instance { get; private set; } = null!;

	public override void Load()
	{
		Instance = this;
		Log.ModInstance = this;
		Log.Info("Shared Worlds loading.");
	}

	public override void Unload()
	{
		SharedWorldsService.Instance.Unload();
		Log.ModInstance = null;
		Instance = null!;
	}

	public override void HandlePacket(BinaryReader reader, int whoAmI)
	{
		SharedWorldsNet.HandlePacket(this, reader, whoAmI);
	}
}
