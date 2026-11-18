using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// tModLoader has no public hook for adding a vanilla main-menu button. This attaches one
/// small button to the active ModMenu's UserInterface (or to its current UIState) while the
/// title screen is visible. F9 is the fallback if a menu mod replaces this surface.
/// </summary>
[Autoload(Side = ModSide.Client)]
public sealed class MainMenuButtonSystem : ModSystem
{
	private UITextPanel<string>? _button;
	private UIState? _ownedState;
	private UIState? _attachedState;
	private ModMenu? _attachedMenu;

	public override void Load()
	{
		if (Main.dedServ)
			return;

		_button = new UITextPanel<string>("Shared Worlds", 0.68f) {
			Width = StyleDimension.FromPixels(170),
			Height = StyleDimension.FromPixels(38),
			HAlign = 1f,
			VAlign = 0f,
			Left = StyleDimension.FromPixels(-24),
			Top = StyleDimension.FromPixels(112),
			BackgroundColor = new Color(25, 55, 75, 220)
		};

		_button.OnLeftClick += (_, _) => {
			SoundEngine.PlaySound(SoundID.MenuOpen);
			SharedWorldsUI.Open();
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

	public override void PostUpdateEverything()
	{
		if (Main.dedServ || _button == null || _ownedState == null)
			return;

		if (!SharedWorldsConfig.Current.ShowMainMenuButton) {
			Detach();
			return;
		}

		if (!Main.gameMenu || Main.menuMode != 0 || Main.MenuUI.CurrentState != null) {
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
