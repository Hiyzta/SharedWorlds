using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.Social;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// Adds a real "Play Shared Worlds" row to Terraria's multiplayer menu (menuMode 12).
/// tModLoader has no public menu-row API, so this is a deliberately tiny IL edit around
/// the existing row: the Back row moves down one slot, our row takes its place, and the
/// menu's normal mouse/gamepad selection path is left intact.
/// </summary>
[Autoload(Side = ModSide.Client)]
public sealed class MultiplayerMenuSystem : ModSystem
{
	private static readonly FieldInfo? SelectedMenuField =
		typeof(Main).GetField("selectedMenu", BindingFlags.Instance | BindingFlags.NonPublic);

	private static readonly FieldInfo? MenuModeField =
		typeof(Main).GetField("menuMode", BindingFlags.Static | BindingFlags.Public);

	private static readonly FieldInfo? LangMenuField =
		typeof(Lang).GetField("menu", BindingFlags.Static | BindingFlags.Public);

	private static bool _opening;

	public override void Load()
	{
		if (Main.dedServ || SelectedMenuField == null || MenuModeField == null || LangMenuField == null)
			return;

		try {
			MethodInfo? drawMenu = typeof(Main).GetMethod("DrawMenu", BindingFlags.Instance | BindingFlags.NonPublic);
			if (drawMenu == null) {
				Log.Warn("Could not find Main.DrawMenu; the Play Shared Worlds menu row will use the fallback button.");
				return;
			}

			MonoModHooks.Modify(drawMenu, InjectMenuRow);
			Log.Info("Installed the Play Shared Worlds multiplayer-menu row.");
		}
		catch (Exception e) {
			Log.Error("Could not install the Play Shared Worlds multiplayer-menu row; using the fallback button.", e);
		}
	}

	private static void InjectMenuRow(ILContext il)
	{
		var cursor = new ILCursor(il);

		// Find `if (Main.menuMode == 12)` in the vanilla menu chain.
		if (!cursor.TryGotoNext(MoveType.After,
			i => i.MatchLdsfld(MenuModeField!),
			i => i.MatchLdcI4(12),
			i => i.OpCode == OpCodes.Bne_Un || i.OpCode == OpCodes.Bne_Un_S)) {
			throw new InvalidOperationException("Could not locate the multiplayer menu branch in Main.DrawMenu.");
		}

		// Map the extra visual rows back to the vanilla handlers before any branch checks.
		cursor.Emit(OpCodes.Ldarg_0);
		cursor.Emit(OpCodes.Ldarg_0);
		cursor.Emit(OpCodes.Ldfld, SelectedMenuField!);
		cursor.EmitDelegate<Func<int, int>>(MapSelectedMenu);
		cursor.Emit(OpCodes.Stfld, SelectedMenuField!);

		// Capture the local that holds the menu label array.
		int labelArrayLocal = -1;
		if (!cursor.TryGotoNext(MoveType.Before,
			i => i.MatchLdloc(out labelArrayLocal),
			i => i.MatchLdcI4(0),
			i => i.MatchLdsfld(out Mono.Cecil.FieldReference? field) && field?.Name == "menu")) {
			throw new InvalidOperationException("Could not locate the multiplayer menu label array.");
		}

		// Find the vanilla `numButtons = 3 + steamOffset` assignment at the end of the branch.
		int numButtonsLocal = -1;
		if (!cursor.TryGotoNext(MoveType.Before,
			i => i.MatchLdcI4(3),
			i => i.MatchLdloc(out _),
			i => i.MatchAdd(),
			i => i.MatchStloc(out numButtonsLocal))) {
			throw new InvalidOperationException("Could not locate the multiplayer menu button count.");
		}

		Instruction three = cursor.Next!;
		Instruction store = three.Next?.Next?.Next
			?? throw new InvalidOperationException("Could not step over the multiplayer menu button count.");
		if (!store.MatchStloc(out _))
			throw new InvalidOperationException("Could not identify the multiplayer menu button-count store.");

		// The matched sequence is exactly four instructions: ldc.i4.3, ldloc, add, stloc.
		cursor.Index += 4;

		// 3 + offset becomes 4 + offset because the Back row moves down one slot.
		if (three.OpCode == OpCodes.Ldc_I4_3) {
			three.OpCode = OpCodes.Ldc_I4_4;
		}
		else if (three.OpCode == OpCodes.Ldc_I4_S || three.OpCode == OpCodes.Ldc_I4) {
			three.Operand = 4;
		}
		else {
			throw new InvalidOperationException("Unexpected multiplayer menu button-count instruction.");
		}

		cursor.Emit(OpCodes.Ldloc, il.Body.Variables[labelArrayLocal]);
		cursor.EmitDelegate<Action<string[]>>(AddMenuRow);
	}

	/// <summary>Called inside the menuMode 12 branch before vanilla checks selectedMenu.</summary>
	private static int MapSelectedMenu(int selected)
	{
		int offset = SocialAPI.Network != null ? 1 : 0;

		// Visual layout after injection:
		//   offset 0: Host & Play
		//   offset 1: Join via Steam
		//   offset 2: Join via IP
		//   2 + offset: Play Shared Worlds
		//   3 + offset: Back
		if (selected == 2 + offset) {
			RequestPlayScreen();
			return -1;
		}

		if (selected == 3 + offset)
			return 2 + offset;

		return selected;
	}

	private static void AddMenuRow(string[] labels)
	{
		int offset = SocialAPI.Network != null ? 1 : 0;
		if (labels.Length <= 3 + offset)
			return;

		// The vanilla branch has already written Back at 2 + offset. Move it down and
		// place the new row in the slot directly above Back.
		labels[3 + offset] = labels[2 + offset];
		labels[2 + offset] = "Play Shared Worlds";
	}

	private static void RequestPlayScreen()
	{
		if (_opening)
			return;

		_opening = true;
		Main.QueueMainThreadAction(() => {
			_opening = false;
			if (Main.menuMode != 12)
				return;

			SharedWorldsEulaGate.ShowIfNeeded(() => SharedWorldsUI.OpenPlay(12));
		});
	}
}
