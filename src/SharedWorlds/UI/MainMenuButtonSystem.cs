using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// tModLoader has no public hook for adding a vanilla main-menu button. The active ModMenu's
/// UserInterface is drawn over the title screen, so we keep one small button attached there.
/// F9 also opens the screen, including when a menu theme or mod replaces that surface.
/// </summary>
[Autoload(Side = ModSide.Client)]
public sealed class MainMenuButtonSystem : ModSystem
{
	private UITextPanel<string>? _button;
	private UIState? _ownedState;
	private UIState? _attachedState;
	private ModMenu? _attachedMenu;
	private bool _f9Down;

	public override void Load()
	{
		if (Main.dedServ)
			return;

		_button = new UITextPanel<string>("Shared Worlds", 0.72f) {
			Width = StyleDimension.FromPixels(178),
			Height = StyleDimension.FromPixels(38),
			HAlign = 1f,
			VAlign = 0f,
			Left = StyleDimension.FromPixels(-24),
			Top = StyleDimension.FromPixels(112),
			BackgroundColor = SwUI.ButtonBg,
			BorderColor = SwUI.ButtonBorder
		};
		SwUI.WireHover(_button);

		_button.OnLeftClick += (_, _) => {
			SoundEngine.PlaySound(SoundID.MenuOpen);
			SharedWorldsUI.OpenPlay(0);
		};

		_ownedState = new UIState();
		_ownedState.Append(_button);
		_ownedState.Activate();
	}

	public override void Unload()
	{
		Detach();
		_button = null;
		_ownedState = null;
	}

	public override void PostUpdateInput()
	{
		if (Main.dedServ || _button == null || _ownedState == null)
			return;

		bool onTitleScreen = Main.gameMenu && Main.menuMode == 0;
		if (!onTitleScreen) {
			Detach();
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

		if (!SharedWorldsConfig.Current.ShowMainMenuButton) {
			Detach();
			return;
		}

		ModMenu? menu = MenuLoader.CurrentMenu;
		if (menu == null) {
			Detach();
			return;
		}

		if (!ReferenceEquals(menu, _attachedMenu))
			Detach();

		if (_attachedState != null)
			return;

		UserInterface ui = menu.UserInterface;
		if (ui.CurrentState == null) {
			ui.SetState(_ownedState);
			_attachedState = _ownedState;
		}
		else {
			ui.CurrentState.Append(_button);
			ui.CurrentState.Recalculate();
			_attachedState = ui.CurrentState;
		}

		_attachedMenu = menu;
	}

	private void Detach()
	{
		if (_attachedState == null) {
			_attachedMenu = null;
			return;
		}

		try {
			if (ReferenceEquals(_attachedState, _ownedState)) {
				_attachedMenu?.UserInterface.SetState(null);
			}
			else {
				_attachedState.RemoveChild(_button!);
				_attachedState.Recalculate();
			}
		}
		catch (Exception e) {
			Log.Warn("Could not detach the Shared Worlds main-menu button.", e);
		}

		_attachedState = null;
		_attachedMenu = null;
	}
}
