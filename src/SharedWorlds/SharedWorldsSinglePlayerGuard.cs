using System.IO;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader;
using SharedWorlds.Core;

namespace SharedWorlds;

/// <summary>
/// A registered shared world only makes progress through a hosted session, where every
/// save is captured, published and broadcast to the group. Opening one from the vanilla
/// single-player world list is the classic way to fork the world silently, so it is
/// rejected here with the same rejection menu vanilla uses for Journey-mode mismatches:
/// the world shows dimmed in the list, sorts to the bottom, and clicking Play explains
/// where to go instead. Host &amp; Play — the mod's own hosting path and the vanilla
/// button, both of which set the multiplayer menu flag before the world select — and
/// dedicated servers stay allowed.
/// </summary>
public sealed class SharedWorldsSinglePlayerGuard : ModSystem
{
	public override bool CanWorldBePlayed(PlayerFileData player, WorldFileData world)
	{
		// Dedicated servers never go through a world-select screen.
		if (Main.dedServ)
			return true;

		// Host & Play (vanilla button and the mod's own launcher) runs with the
		// multiplayer menu flag set, so hosted sessions keep working.
		if (Main.menuMultiplayer)
			return true;

		if (string.IsNullOrEmpty(world?.Path))
			return true;

		return SharedWorldsService.Instance.FindByFileName(Path.GetFileName(world.Path)) == null;
	}

	public override string WorldCanBePlayedRejectionMessage(PlayerFileData player, WorldFileData world)
	{
		string name = world?.Name ?? "This world";
		if (name.Length > 24)
			name = name[..21] + "…";

		Log.Info($"Blocked a single-player load of shared world '{world?.Name}'; pointed at the Play Shared Worlds screen.");
		return $"\"{name}\" is a shared world.\nUse Play Shared Worlds (multiplayer menu) so every save reaches the group.";
	}
}
