using System.Reflection;
using Terraria.IO;
using Terraria.ModLoader;
using SharedWorlds.Core;

namespace SharedWorlds;

/// <summary>
/// Observes authoritative world saves (Terraria's 10-minute autosave, manual saves and
/// Host &amp; Play saves) and turns them into a live Shared Worlds snapshot.
/// </summary>
public sealed class SharedWorldsSaveHook : ModSystem
{
	public override void Load()
	{
		try {
			MethodInfo? save = typeof(WorldFile).GetMethod("SaveWorld", BindingFlags.Public | BindingFlags.Static, binder: null, types: Type.EmptyTypes, modifiers: null);
			if (save == null) {
				Log.Warn("Could not find WorldFile.SaveWorld; live autosave uploads are unavailable.");
				return;
			}

			MonoModHooks.Add(save, new Action<Action>(AfterSaveWorld));
		}
		catch (Exception e) {
			Log.Error("Could not hook WorldFile.SaveWorld; live autosave uploads are unavailable.", e);
		}
	}

	private static void AfterSaveWorld(Action orig)
	{
		orig();
		try {
			SharedWorldsService.Instance.NotifyWorldSaved();
		}
		catch (Exception e) {
			Log.Warn("Could not process a shared-world autosave.", e);
		}
	}
}
