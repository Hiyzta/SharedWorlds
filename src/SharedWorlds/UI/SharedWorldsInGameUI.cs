using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using SharedWorlds.Core;
using SharedWorlds.Net;

namespace SharedWorlds.UI;

[Autoload(Side = ModSide.Client)]
public sealed class SharedWorldsInGameUI : ModSystem
{
	private UserInterface _ui = null!;
	private SharedWorldsInGamePanel _panel = null!;
	private GameTime? _lastGameTime;
	private ModKeybind? _keybind;

	public override void Load()
	{
		if (Main.dedServ)
			return;

		_ui = new UserInterface();
		_panel = new SharedWorldsInGamePanel(this);
		_panel.Activate();
		_keybind = KeybindLoader.RegisterKeybind(Mod, "Shared Worlds", "F9");
	}

	public override void Unload()
	{
		_ui = null!;
		_panel = null!;
		_keybind = null;
	}

	public override void UpdateUI(GameTime gameTime)
	{
		if (Main.dedServ)
			return;

		_lastGameTime = gameTime;
		if (_ui?.CurrentState != null)
			_ui.Update(gameTime);
	}

	public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
	{
		if (Main.dedServ)
			return;

		int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
		if (mouseTextIndex == -1)
			return;

		layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
			"SharedWorlds: In-game panel",
			() => {
				if (_lastGameTime != null && _ui?.CurrentState != null)
					_ui.Draw(Main.spriteBatch, _lastGameTime);
				return true;
			},
			InterfaceScaleType.UI));
	}

	public override void PostUpdateInput()
	{
		if (Main.dedServ || Main.gameMenu || _keybind == null || Main.drawingPlayerChat)
			return;

		if (!_keybind.JustPressed)
			return;

		if (_ui.CurrentState == null) {
			_panel.Refresh();
			_ui.SetState(_panel);
			SoundEngine.PlaySound(SoundID.MenuOpen);
		}
		else {
			_ui.SetState(null);
			SoundEngine.PlaySound(SoundID.MenuClose);
		}
	}

	public void Close()
	{
		_ui.SetState(null);
	}
}

public sealed class SharedWorldsInGamePanel : UIState
{
	private readonly SharedWorldsInGameUI _owner;
	private UIText _body = null!;
	private UITextPanel<string> _openButton = null!;
	private UITextPanel<string> _syncButton = null!;
	private UITextPanel<string> _pendingButton = null!;

	public SharedWorldsInGamePanel(SharedWorldsInGameUI owner)
	{
		_owner = owner;
	}

	public override void OnInitialize()
	{
		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(600),
			Height = StyleDimension.FromPixels(400),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(13, 23, 36, 246),
			BorderColor = new Color(52, 92, 112, 255)
		};
		Append(panel);

		panel.Append(new UIPanel {
			Width = StyleDimension.FromPixelsAndPercent(0, 1f),
			Height = StyleDimension.FromPixels(64),
			BackgroundColor = new Color(22, 46, 64, 255),
			BorderColor = new Color(52, 92, 112, 255)
		});

		panel.Append(new UIText("Shared Worlds", 1f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(8)
		});

		panel.Append(new UIText("In-game snapshot controls", 0.62f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(38),
			TextColor = new Color(120, 200, 214)
		});

		_body = new UIText("", 0.68f) {
			Left = StyleDimension.FromPixels(22),
			Top = StyleDimension.FromPixels(76),
			Width = StyleDimension.FromPixels(556),
			Height = StyleDimension.FromPixels(118),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = new Color(185, 220, 232)
		};
		panel.Append(_body);

		_openButton = SharedWorldsUIState.MakeButton("Open to friends", 22, 212, 170, OpenToFriends);
		panel.Append(_openButton);

		_syncButton = SharedWorldsUIState.MakeButton("Sync & publish", 204, 212, 150, SyncAndPublish);
		panel.Append(_syncButton);

		_pendingButton = SharedWorldsUIState.MakeButton("Upload pending", 366, 212, 150, UploadPending);
		panel.Append(_pendingButton);

		panel.Append(new UIText("F9 toggles this panel. 'Open to friends' saves, exits to the title screen and starts Host & Play.", 0.58f) {
			Left = StyleDimension.FromPixels(22),
			Top = StyleDimension.FromPixels(262),
			Width = StyleDimension.FromPixels(556),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = new Color(132, 168, 182)
		});

		panel.Append(SharedWorldsUIState.MakeButton("Close", 22, 334, 100, _owner.Close));
	}

	public void Refresh()
	{
		SharedWorldRecord? record = SharedWorldsService.Instance.CurrentRecord;
		if (record == null) {
			_body.SetText("This world is not registered as a shared world. Exit to the title screen, open Shared Worlds and register it first.");
			_openButton.SetText("Unavailable");
			_syncButton.SetText("Unavailable");
			_pendingButton.SetText("Unavailable");
			return;
		}

		_body.SetText(
			$"{record.WorldName}\n" +
			$"Latest saved snapshot: {(record.LocalVersion == 0 ? "none yet" : FormatUtil.Timestamp(record.LastUpdatedUtc))}\n" +
			$"Last host: {WorkshopService.PersonaName(record.LastHostSteamId)}\n" +
			$"This account's item: {(record.WorkshopItemId == 0 ? "not created yet" : record.WorkshopItemId.ToString())}\n" +
			$"Workshop: {(record.PendingNeedsEula ? "EULA needed" : record.PendingUpload ? "upload pending" : "no pending upload")}");

		_openButton.SetText("Open to friends");
		_syncButton.SetText(Main.netMode == NetmodeID.MultiplayerClient ? "Sync & publish" : "Host only");
		_pendingButton.SetText(record.PendingUpload ? "Upload pending" : "No pending");
	}

	private void OpenToFriends()
	{
		SharedWorldRecord? record = SharedWorldsService.Instance.CurrentRecord;
		if (record == null)
			return;

		_owner.Close();
		SharedWorldsService.Instance.QueueOpenToFriends(record.WorldId, out string error);
		if (!string.IsNullOrEmpty(error)) {
			Main.NewText("Open to friends: " + error, 255, 180, 120);
			return;
		}

		Main.NewText("Saving and reopening this world as a Host & Play session...", 160, 255, 180);
	}

	private void SyncAndPublish()
	{
		SharedWorldRecord? record = SharedWorldsService.Instance.CurrentRecord;
		if (record == null)
			return;

		if (Main.netMode != NetmodeID.MultiplayerClient) {
			Main.NewText("Sync & publish is requested from a connected client; in single-player just exit normally.", 255, 210, 140);
			return;
		}

		_owner.Close();
		SharedWorldsNet.ClientRequestSnapshot(record.WorldId, publish: true);
		Main.NewText("Asked the host server to save, publish and send the current snapshot.", 160, 255, 180);
	}

	private void UploadPending()
	{
		SharedWorldRecord? record = SharedWorldsService.Instance.CurrentRecord;
		if (record == null || !record.PendingUpload) {
			Main.NewText("There is no pending snapshot to upload.", 255, 210, 140);
			return;
		}

		_owner.Close();
		Main.NewText("Uploading pending snapshot to your Workshop item...", 160, 220, 255);
		_ = Task.Run(async () => {
			UploadResult result = await SharedWorldsService.Instance.PublishNowAsync(record, null, CancellationToken.None);
			Main.QueueMainThreadAction(() => Main.NewText(result.Success ? "Pending snapshot published." : "Upload still pending: " + result.Message, 180, 220, 255));
		});
	}
}
