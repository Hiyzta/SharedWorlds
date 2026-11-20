using System.Reflection;
using Terraria;
using Terraria.IO;
using Terraria.Net;
using Terraria.Social;

namespace SharedWorlds.Core;

/// <summary>
/// Launches the same Host &amp; Play flow the vanilla button uses.
/// Host &amp; Play is a separate server process; this cannot be done "in place" in single-player.
/// </summary>
public static class HostAndPlay
{
	private static readonly MethodInfo? SubmitServerPasswordMethod =
		typeof(Main).GetMethod("OnSubmitServerPassword", BindingFlags.Instance | BindingFlags.NonPublic, binder: null, types: Type.EmptyTypes, modifiers: null);

	public static bool IsSupported => !Main.dedServ && SocialAPI.Network != null && SubmitServerPasswordMethod != null;

	public static string PendingOpenToFriendsWorldId { get; private set; } = "";
	public static bool PendingOpenToFriendsReady { get; set; }

	/// <summary>Set when the player must pick a character before Host &amp; Play can start.</summary>
	public static string PendingHostWorldId { get; private set; } = "";

	public static bool IsWaitingForCharacter(string worldId)
		=> !string.IsNullOrEmpty(worldId) && string.Equals(PendingHostWorldId, worldId, StringComparison.OrdinalIgnoreCase);

	public static bool QueueOpenToFriends(string worldId, out string error)
	{
		error = "";

		if (Main.dedServ) {
			error = "Host & Play is not available on a dedicated server.";
			return false;
		}

		if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer) {
			error = "Return to single-player first. Open to friends saves and relaunches the world as a Host & Play session.";
			return false;
		}

		if (Main.gameMenu) {
			error = "A world must be loaded before using Open to friends.";
			return false;
		}

		PendingOpenToFriendsWorldId = worldId;
		PendingOpenToFriendsReady = false;
		WorldGen.SaveAndQuit();
		return true;
	}

	public static void ClearPending()
	{
		PendingOpenToFriendsWorldId = "";
		PendingOpenToFriendsReady = false;
		PendingHostWorldId = "";
		SharedWorldsPresence.ClearHandoff();
	}

	/// <summary>Continues a Host request after character selection, or clears it if the player backs out.</summary>
	public static void Tick()
	{
		if (string.IsNullOrEmpty(PendingHostWorldId) || Main.dedServ || !Main.gameMenu)
			return;

		if (Main.menuMode == 0) {
			PendingHostWorldId = "";
			SharedWorldsPresence.ClearHandoff();
			return;
		}

		if (Main.menuMode != 6 || Main.ActivePlayerFileData?.Player == null)
			return;

		string worldId = PendingHostWorldId;
		PendingHostWorldId = "";
		SharedWorldRecord? record = SharedWorldsService.Instance.Registry.FindByWorldId(worldId);
		if (record == null)
			return;

		if (!Start(record, out string error)) {
			Log.Warn("Could not continue Host & Play after character selection: " + error);
		}
	}

	/// <summary>Starts Host &amp; Play for the given registered world. Must be called from the title screen.</summary>
	public static bool Start(SharedWorldRecord record, out string error)
	{
		error = "";

		if (Main.dedServ) {
			error = "Host & Play is not available on a dedicated server.";
			return false;
		}

		if (!Main.gameMenu) {
			error = "Return to the title screen before hosting.";
			return false;
		}

		if (Main.ActivePlayerFileData?.Player == null) {
			PendingHostWorldId = record.WorldId;
			Main.menuMultiplayer = true;
			Main.menuServer = true;
			Main.ClearPendingPlayerSelectCallbacks();
			Main.MenuUI.SetState(null);
			Main.menuMode = 1;
			error = "Choose a character; Host & Play will continue automatically.";
			return false;
		}

		if (SocialAPI.Network == null) {
			error = "Steam networking is not available. Start tModLoader through Steam.";
			return false;
		}

		string path = SharedWorldPaths.WorldPath(record);
		if (!File.Exists(path)) {
			error = "The local world file is missing: " + path;
			return false;
		}

		if (!SharedWorldPaths.IsInsideSavePath(path)) {
			error = "Refusing to host a world outside the tModLoader save directory.";
			return false;
		}

		WorldFileData? data = Main.WorldList.FirstOrDefault(w =>
			string.Equals(Path.GetFullPath(w.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

		if (data == null) {
			data = new WorldFileData(path, cloudSave: false) {
				Name = Path.GetFileNameWithoutExtension(path)
			};
		}

		try {
			data.SetAsActive();
			PendingHostWorldId = "";
			Main.menuMultiplayer = true;
			Main.menuServer = true;
			Main.MenuServerMode = ServerMode.Lobby | ServerMode.FriendsCanJoin;
			Netplay.ServerPassword = "";
			Main.showServerConsole = false;
			Main.autoPass = true;
			Main.MenuUI.SetState(null);

			if (SubmitServerPasswordMethod == null) {
				OpenHostAndPlaySettings();
				error = "Automatic Host & Play is not available in this tModLoader build; the normal Host & Play screen has been opened instead.";
				return false;
			}

			SubmitServerPasswordMethod.Invoke(Main.instance, null);
			Log.Info($"Started Host & Play for '{record.WorldName}' ({record.WorldId}).");
			return true;
		}
		catch (TargetInvocationException e) {
			Log.Error("Host & Play invocation failed.", e.InnerException ?? e);
			OpenHostAndPlaySettings();
			error = "Could not start Host & Play automatically. The normal Host & Play screen has been opened instead: " + (e.InnerException?.Message ?? e.Message);
			return false;
		}
		catch (Exception e) {
			Log.Error("Host & Play failed.", e);
			error = "Could not start Host & Play: " + e.Message;
			return false;
		}
	}

	private static void OpenHostAndPlaySettings()
	{
		try {
			Main.menuMultiplayer = true;
			Main.menuServer = true;
			Main.MenuServerMode = ServerMode.Lobby | ServerMode.FriendsCanJoin;
			Main.MenuUI.SetState(null);
			Main.menuMode = 889;
		}
		catch (Exception e) {
			Log.Error("Could not open the Host & Play settings screen.", e);
		}
	}
}
