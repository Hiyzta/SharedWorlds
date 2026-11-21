using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// F9 opens the Play screen while sitting at the title menu. The primary entry is the
/// real "Play Shared Worlds" row in the multiplayer menu; this hotkey is the shortcut,
/// so no floating buttons are bolted onto the title screen.
/// </summary>
[Autoload(Side = ModSide.Client)]
public sealed class SharedWorldsTitleHotkey : ModSystem
{
	private bool _f9Down;

	public override void PostUpdateInput()
	{
		if (Main.dedServ)
			return;

		bool onTitleScreen = Main.gameMenu && Main.menuMode == 0;
		if (!onTitleScreen) {
			_f9Down = false;
			return;
		}

		bool f9 = Main.keyState.IsKeyDown(Keys.F9);
		if (f9 && !_f9Down) {
			SoundEngine.PlaySound(SoundID.MenuOpen);
			SharedWorldsUI.OpenPlay(0);
			_f9Down = true;
			return;
		}

		_f9Down = f9;
	}
}
