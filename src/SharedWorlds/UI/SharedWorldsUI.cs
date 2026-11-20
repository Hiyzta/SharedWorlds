using Microsoft.Xna.Framework;
using ReLogic.OS;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.ID;
using Terraria.IO;
using Terraria.UI;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

public static class SharedWorldsUI
{
	public static void Open()
	{
		if (Main.dedServ)
			return;

		SoundEngine.PlaySound(SoundID.MenuOpen);
		Main.MenuUI.SetState(new SharedWorldsUIState());
		Main.menuMode = 888;
	}

	/// <summary>Opens the clean Play screen used by the real multiplayer-menu row.</summary>
	public static void OpenPlay(int returnMenuMode = 12, string? status = null, string? focusWorldId = null)
	{
		if (Main.dedServ)
			return;

		SoundEngine.PlaySound(SoundID.MenuOpen);
		Main.MenuUI.SetState(new SharedWorldsPlayUIState(returnMenuMode, status, focusWorldId));
		Main.menuMode = 888;
	}

	public static void Close()
	{
		CloseTo(0);
	}

	public static void CloseTo(int menuMode)
	{
		Main.MenuUI.SetState(null);
		Main.menuMode = menuMode;
	}

	public static bool JoinLobby(LiveWorldSession live, out string error)
	{
		error = "";
		if (Main.dedServ) {
			error = "Multiplayer is not available on a dedicated server.";
			return false;
		}

		if (live == null || live.LobbyId == 0) {
			error = "That shared world is no longer online.";
			return false;
		}

		if (Terraria.Social.SocialAPI.Network is not Terraria.Social.Steam.NetClientSocialModule client) {
			error = "Steam multiplayer is not available. Start tModLoader through Steam.";
			return false;
		}

		try {
			Main.MenuUI.SetState(null);
			Main.menuMode = 0;
			Main.menuMultiplayer = true;
			Main.menuServer = false;
			client.ConnectToLobby(live.LobbyId);
			Log.Info($"Joining live shared world '{live.WorldName}' via lobby {live.LobbyId}.");
			return true;
		}
		catch (Exception e) {
			Log.Error("Could not join the shared-world lobby.", e);
			error = "Could not join the live world: " + e.Message;
			return false;
		}
	}
}

public sealed class SharedWorldsUIState : UIState
{
	private readonly List<SharedWorldRecord> _records = new();
	private UIList _list = null!;
	private UIText _status = null!;
	private bool _busy;
	private Task<SyncReport>? _task;
	private string _hostAfterSync = "";
	private readonly Action? _onBack;
	private readonly Action? _onPlay;
	public SyncReport? LastSyncReport { get; private set; }

	public SharedWorldsUIState(Action? onBack = null, Action? onPlay = null)
	{
		_onBack = onBack;
		_onPlay = onPlay;
	}

	public override void OnInitialize()
	{
		float panelWidth = Math.Clamp(Main.screenWidth - 40, 640, 900);
		float panelHeight = Math.Clamp(Main.screenHeight - 40, 520, 700);

		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(panelWidth),
			Height = StyleDimension.FromPixels(panelHeight),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(13, 23, 36, 246),
			BorderColor = new Color(52, 92, 112, 255)
		};
		Append(panel);

		var header = new UIPanel {
			Width = StyleDimension.FromPixelsAndPercent(0, 1f),
			Height = StyleDimension.FromPixels(72),
			BackgroundColor = new Color(22, 46, 64, 255),
			BorderColor = new Color(52, 92, 112, 255)
		};
		panel.Append(header);

		var title = new UIText("Shared Worlds", 1.15f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(8)
		};
		panel.Append(title);

		var subtitle = new UIText("One world · one latest snapshot · any participant can host", 0.68f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(44),
			TextColor = new Color(120, 200, 214)
		};
		panel.Append(subtitle);

		panel.Append(MakeButton("Play", panelWidth - 122, 20, 96, () => {
			if (_onPlay != null)
				_onPlay();
			else
				SharedWorldsUI.OpenPlay(0);
		}));

		var listPanel = new UIPanel {
			Left = StyleDimension.FromPixels(20),
			Top = StyleDimension.FromPixels(82),
			Width = StyleDimension.FromPixels(panelWidth - 40),
			Height = StyleDimension.FromPixels(panelHeight - 212),
			BackgroundColor = new Color(7, 14, 24, 220),
			BorderColor = new Color(38, 70, 86, 255)
		};
		panel.Append(listPanel);

		_list = new UIList {
			Left = StyleDimension.FromPixels(8),
			Top = StyleDimension.FromPixels(8),
			Width = StyleDimension.FromPixelsAndPercent(-34, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-16, 1f),
			ListPadding = 6f
		};
		listPanel.Append(_list);

		var scrollbar = new UIScrollbar {
			HAlign = 1f,
			Width = StyleDimension.FromPixels(20),
			Height = StyleDimension.FromPixelsAndPercent(-16, 1f),
			Top = StyleDimension.FromPixels(8)
		};
		scrollbar.SetView(100f, 1000f);
		listPanel.Append(scrollbar);
		_list.SetScrollbar(scrollbar);

		var statusPanel = new UIPanel {
			Left = StyleDimension.FromPixels(20),
			Top = StyleDimension.FromPixels(panelHeight - 124),
			Width = StyleDimension.FromPixels(panelWidth - 40),
			Height = StyleDimension.FromPixels(52),
			BackgroundColor = new Color(8, 17, 27, 190),
			BorderColor = new Color(38, 70, 86, 255)
		};
		panel.Append(statusPanel);

		_status = new UIText("", 0.7f) {
			Left = StyleDimension.FromPixels(10),
			Top = StyleDimension.FromPixels(7),
			Width = StyleDimension.FromPixels(panelWidth - 60),
			Height = StyleDimension.FromPixels(38),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = new Color(190, 224, 234)
		};
		statusPanel.Append(_status);

		float buttonTop = panelHeight - 58;
		panel.Append(MakeButton("Refresh", 24, buttonTop, 110, () => RefreshList()));
		panel.Append(MakeButton("Register world", 144, buttonTop, 150, OpenPicker));
		panel.Append(MakeButton("Import invite", 304, buttonTop, 140, OpenImport));
		panel.Append(MakeButton("Back", panelWidth - 104, buttonTop, 80, () => {
			if (_onBack != null)
				_onBack();
			else
				SharedWorldsUI.Close();
		}));
	}

	public override void OnActivate()
	{
		SharedWorldsService.Instance.Changed -= OnServiceChanged;
		SharedWorldsService.Instance.Changed += OnServiceChanged;
		RefreshList();

		SharedWorldRecord? pendingEula = _records.FirstOrDefault(r => r.PendingNeedsEula);
		if (pendingEula == null)
			return;

		_ = Task.Run(async () => {
			WorkshopService.EulaState state = await WorkshopService.CheckEulaAsync(TimeSpan.FromSeconds(10));
			if (state != WorkshopService.EulaState.Accepted)
				return;

			SharedWorldsService.Instance.ClearPendingEula(pendingEula.WorldId);
			Main.QueueMainThreadAction(() => {
				_status.SetText("Steam Workshop EULA is accepted. Use Publish pending when ready.");
				RefreshList();
			});
		});
	}

	public override void OnDeactivate()
	{
		SharedWorldsService.Instance.Changed -= OnServiceChanged;
	}

	private void OnServiceChanged()
	{
		Main.QueueMainThreadAction(RefreshList);
	}

	public override void Update(GameTime gameTime)
	{
		base.Update(gameTime);

		if (_task == null || !_task.IsCompleted)
			return;

		Task<SyncReport> task = _task;
		_task = null;
		_busy = false;

		string message;
		bool ok = false;
		try {
			SyncReport report = task.Result;
			LastSyncReport = report;
			message = FormatReport(report);
			ok = report.Status is not SyncStatus.Fork and not SyncStatus.Error;
		}
		catch (Exception e) {
			message = "Error: " + e.Message;
			Log.Error("Shared Worlds operation failed.", e);
		}

		_status.SetText(message);
		RefreshList();

		if (ok && !string.IsNullOrEmpty(_hostAfterSync)) {
			string worldId = _hostAfterSync;
			_hostAfterSync = "";
			SharedWorldRecord? record = SharedWorldsService.Instance.Registry.FindByWorldId(worldId);
			if (record != null && !HostAndPlay.Start(record, out string error))
				_status.SetText(error);
		}
		else {
			_hostAfterSync = "";
		}
	}

	private void RefreshList()
	{
		_records.Clear();
		_records.AddRange(SharedWorldsService.Instance.Registry.Snapshot()
			.OrderByDescending(r => r.LastUpdatedUtc));

		_list.Clear();
		if (_records.Count == 0) {
			_list.Add(new UIText("No shared worlds yet. Use Register world to add one, or import an invite code.", 0.8f) {
				TextColor = new Color(170, 190, 200),
				IsWrapped = true
			});
		}
		else {
			foreach (SharedWorldRecord record in _records)
				_list.Add(new SharedWorldEntryElement(record, this));
		}

		_list.Recalculate();
	}

	public void BeginSync(SharedWorldRecord record, bool hostAfter)
	{
		if (_busy)
			return;

		if (hostAfter) {
			LiveWorldSession? live = SharedWorldsLiveService.FindLive(record);
			if (live != null && live.HostSteamId != WorkshopService.CurrentSteamId) {
				_status.SetText($"{WorkshopService.PersonaName(live.HostSteamId)} is already hosting this world. Use Play Shared Worlds to join instead.");
				return;
			}
		}

		_busy = true;
		_hostAfterSync = hostAfter ? record.WorldId : "";
		_status.SetText($"Checking Steam Workshop for '{record.WorldName}'...");
		LastSyncReport = null;
		_task = Task.Run(async () => await SharedWorldsService.Instance.SyncAsync(record, downloadIfNewer: true, progress: null, CancellationToken.None));
	}

	private static string FormatReport(SyncReport report)
	{
		return report.Status switch {
			SyncStatus.Fork => "Fork detected. Open Info for both versions; no files were overwritten. " + report.Message,
			SyncStatus.Error => "Error: " + report.Message,
			SyncStatus.WorkshopUnavailable => "Workshop unavailable; local world is unchanged. " + report.Message,
			SyncStatus.RemoteNewer => "Updated. " + report.Message,
			SyncStatus.UpToDate => "Up to date. " + report.Message,
			SyncStatus.LocalNewer => "Local version is newest. " + report.Message,
			SyncStatus.LocalOnly => "Local only. " + report.Message,
			_ => report.Message
		};
	}

	public void OpenDetail(SharedWorldRecord record)
	{
		SoundEngine.PlaySound(SoundID.MenuTick);
		Main.MenuUI.SetState(new SharedWorldDetailUIState(record, this));
		Main.menuMode = 888;
	}

	public void OpenPicker()
	{
		SoundEngine.PlaySound(SoundID.MenuOpen);
		Main.LoadWorlds();
		Main.MenuUI.SetState(new SharedWorldPickerUIState(this));
		Main.menuMode = 888;
	}

	public void OpenImport()
	{
		SoundEngine.PlaySound(SoundID.MenuOpen);
		var keyboard = new UIVirtualKeyboard("Import Shared Worlds invite", "", OnImportSubmitted, () => {
			Main.MenuUI.SetState(this);
			Main.menuMode = 888;
		}, 0, allowEmpty: false);
		Main.MenuUI.SetState(keyboard);
		Main.menuMode = 888;
	}

	private void OnImportSubmitted(string text)
	{
		try {
			SharedWorldRecord? record = SharedWorldsService.Instance.ImportInvite(text, out string error);
			Main.MenuUI.SetState(this);
			Main.menuMode = 888;
			RefreshList();
			_status.SetText(record == null ? "Import failed: " + error : $"Imported '{record.WorldName}'. Use Sync to download the newest snapshot.");
		}
		catch (Exception e) {
			Log.Error("Could not import a shared-world invite.", e);
			Main.MenuUI.SetState(this);
			Main.menuMode = 888;
			_status.SetText("Import failed: " + e.Message);
		}
	}

	public void CopyInvite(SharedWorldRecord record)
	{
		string code = SharedWorldsService.Instance.CreateInvite(record);
		if (string.IsNullOrEmpty(code)) {
			_status.SetText("Invite unavailable: publish this world once so it has a Workshop item.");
			return;
		}

		try {
			Platform.Get<IClipboard>().Value = code;
			_status.SetText("Invite code copied to the clipboard. It contains the world ID and this account's Workshop item ID.");
		}
		catch (Exception e) {
			_status.SetText("Could not copy the invite code: " + e.Message);
		}
	}

	public void ShowStatus(string text) => _status.SetText(text);

	public static UITextPanel<string> MakeButton(string text, float left, float top, float width, Action onClick)
	{
		var button = new UITextPanel<string>(text, 0.7f) {
			Left = StyleDimension.FromPixels(left),
			Top = StyleDimension.FromPixels(top),
			Width = StyleDimension.FromPixels(width),
			Height = StyleDimension.FromPixels(32)
		};

		button.OnLeftClick += (_, _) => {
			SoundEngine.PlaySound(SoundID.MenuTick);
			onClick();
		};

		return button;
	}
}

public sealed class SharedWorldEntryElement : UIPanel
{
	private readonly SharedWorldRecord _record;
	private readonly SharedWorldsUIState _owner;

	public SharedWorldEntryElement(SharedWorldRecord record, SharedWorldsUIState owner)
	{
		_record = record;
		_owner = owner;
		float entryWidth = Math.Clamp(Main.screenWidth - 40, 640, 900) - 82;

		Width = StyleDimension.FromPixelsAndPercent(-8, 1f);
		Height = StyleDimension.FromPixels(104);
		BackgroundColor = record.PendingUpload
			? new Color(25, 40, 48, 238)
			: new Color(16, 33, 47, 238);
		BorderColor = record.PendingUpload
			? new Color(92, 78, 42, 255)
			: new Color(44, 82, 100, 255);

		var name = new UIText(record.WorldName, 0.92f) {
			Left = StyleDimension.FromPixels(14),
			Top = StyleDimension.FromPixels(8),
			Width = StyleDimension.FromPixelsAndPercent(-330, 1f),
			TextOriginX = 0f
		};
		Append(name);

		string host = WorkshopService.PersonaName(record.LastHostSteamId);
		string version = record.LocalVersion == 0
			? "no local snapshot yet"
			: $"{FormatUtil.Relative(record.LastUpdatedUtc)} · {FormatUtil.Timestamp(record.LastUpdatedUtc)}";
		var versionText = new UIText($"{version}  ·  last host: {host}", 0.66f) {
			Left = StyleDimension.FromPixels(14),
			Top = StyleDimension.FromPixels(36),
			Width = StyleDimension.FromPixelsAndPercent(-330, 1f),
			TextOriginX = 0f,
			TextColor = new Color(160, 202, 216)
		};
		Append(versionText);

		string item = record.WorkshopItemId == 0 ? "item not created yet" : "item " + record.WorkshopItemId;
		string status = record.PendingUpload
			? (record.PendingNeedsEula ? "EULA needed" : "upload pending")
			: "synced";
		var itemText = new UIText($"{item}  ·  {status}", 0.62f) {
			Left = StyleDimension.FromPixels(14),
			Top = StyleDimension.FromPixels(57),
			Width = StyleDimension.FromPixelsAndPercent(-330, 1f),
			TextOriginX = 0f,
			TextColor = record.PendingUpload ? new Color(240, 198, 118) : new Color(128, 170, 184)
		};
		Append(itemText);

		var hint = new UIText(
			record.Participants.Count == 0
				? "Only this account so far"
				: $"{record.Participants.Count} participant account(s)  ·  {record.History.Count} saved snapshot(s)",
			0.58f) {
			Left = StyleDimension.FromPixels(14),
			Top = StyleDimension.FromPixels(78),
			Width = StyleDimension.FromPixelsAndPercent(-330, 1f),
			TextOriginX = 0f,
			TextColor = new Color(110, 145, 160)
		};
		Append(hint);

		Append(MakeSmallButton("Host", entryWidth - 300, 12, () => _owner.BeginSync(_record, hostAfter: true)));
		Append(MakeSmallButton("Sync", entryWidth - 204, 12, () => _owner.BeginSync(_record, hostAfter: false)));
		Append(MakeSmallButton("Invite", entryWidth - 108, 12, () => _owner.CopyInvite(_record)));
		Append(MakeSmallButton("Info", entryWidth - 204, 56, () => _owner.OpenDetail(_record)));
	}

	private UITextPanel<string> MakeSmallButton(string text, float left, float top, Action onClick)
	{
		var button = new UITextPanel<string>(text, 0.62f) {
			Left = StyleDimension.FromPixels(left),
			Top = StyleDimension.FromPixels(top),
			Width = StyleDimension.FromPixels(88),
			Height = StyleDimension.FromPixels(30)
		};
		button.OnLeftClick += (_, _) => {
			SoundEngine.PlaySound(SoundID.MenuTick);
			onClick();
		};
		return button;
	}
}

public sealed class SharedWorldPickerUIState : UIState
{
	private readonly SharedWorldsUIState _parent;
	private readonly UIList _list = new() { ListPadding = 6f };

	public SharedWorldPickerUIState(SharedWorldsUIState parent)
	{
		_parent = parent;
	}

	public override void OnInitialize()
	{
		float panelWidth = Math.Clamp(Main.screenWidth - 40, 640, 760);
		float panelHeight = Math.Clamp(Main.screenHeight - 40, 420, 560);

		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(panelWidth),
			Height = StyleDimension.FromPixels(panelHeight),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(13, 23, 36, 246),
			BorderColor = new Color(52, 92, 112, 255)
		};
		Append(panel);

		panel.Append(new UIPanel {
			Width = StyleDimension.FromPixelsAndPercent(0, 1f),
			Height = StyleDimension.FromPixels(72),
			BackgroundColor = new Color(22, 46, 64, 255),
			BorderColor = new Color(52, 92, 112, 255)
		});

		panel.Append(new UIText("Register a local world", 1f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(10)
		});

		panel.Append(new UIText("Steam Cloud worlds are not supported yet; move the world to local storage first.", 0.62f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(44),
			TextColor = new Color(140, 190, 204)
		});

		_list.Left = StyleDimension.FromPixels(20);
		_list.Top = StyleDimension.FromPixels(86);
		_list.Width = StyleDimension.FromPixels(panelWidth - 62);
		_list.Height = StyleDimension.FromPixels(panelHeight - 150);
		panel.Append(_list);

		var scrollbar = new UIScrollbar {
			HAlign = 1f,
			Width = StyleDimension.FromPixels(20),
			Height = StyleDimension.FromPixels(panelHeight - 150),
			Top = StyleDimension.FromPixels(86)
		};
		scrollbar.SetView(100f, 1000f);
		panel.Append(scrollbar);
		_list.SetScrollbar(scrollbar);

		panel.Append(SharedWorldsUIState.MakeButton("Back", panelWidth - 120, panelHeight - 58, 100, GoBack));

		foreach (WorldFileData world in Main.WorldList.Where(w => w.IsValid).OrderBy(w => w.Name)) {
			bool registered = SharedWorldsService.Instance.FindByFileName(Path.GetFileName(world.Path)) != null;
			string suffix = world.IsCloudSave ? "  (Steam Cloud - unsupported)"
				: registered ? "  (already shared)"
				: "";

			var button = new UITextPanel<string>(world.Name + suffix, 0.72f) {
				Width = StyleDimension.FromPixelsAndPercent(-8, 1f),
				Height = StyleDimension.FromPixels(44),
				BackgroundColor = registered || world.IsCloudSave
					? new Color(28, 34, 44, 220)
					: new Color(24, 52, 70, 235)
			};

			if (!registered && !world.IsCloudSave) {
				button.OnLeftClick += (_, _) => {
					SoundEngine.PlaySound(SoundID.MenuTick);
					try {
						SharedWorldRecord record = SharedWorldsService.Instance.RegisterWorld(world, out string error);
						Main.MenuUI.SetState(_parent);
						Main.menuMode = 888;
						_parent.ShowStatus(record == null ? "Could not register: " + error : $"Registered '{record.WorldName}'. Host it once and save to publish the first snapshot.");
					}
					catch (Exception e) {
						Log.Error("Could not register a shared world.", e);
						_parent.ShowStatus("Could not register: " + e.Message);
					}
				};
			}

			_list.Add(button);
		}

		if (_list.Count == 0)
			_list.Add(new UIText("No local worlds found.", 0.8f) { TextColor = new Color(180, 190, 200) });
	}

	private void GoBack()
	{
		SoundEngine.PlaySound(SoundID.MenuClose);
		Main.MenuUI.SetState(_parent);
		Main.menuMode = 888;
	}
}

public sealed class SharedWorldDetailUIState : UIState
{
	private readonly SharedWorldRecord _record;
	private readonly SharedWorldsUIState _parent;
	private readonly UIText _text;

	public SharedWorldDetailUIState(SharedWorldRecord record, SharedWorldsUIState parent)
	{
		_record = record;
		_parent = parent;
		_text = new UIText("", 0.68f) { IsWrapped = true };
	}

	public override void OnInitialize()
	{
		float panelWidth = Math.Clamp(Main.screenWidth - 40, 700, 900);
		float panelHeight = Math.Clamp(Main.screenHeight - 40, 520, 700);

		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(panelWidth),
			Height = StyleDimension.FromPixels(panelHeight),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(13, 23, 36, 246),
			BorderColor = new Color(52, 92, 112, 255)
		};
		Append(panel);

		panel.Append(new UIPanel {
			Width = StyleDimension.FromPixelsAndPercent(0, 1f),
			Height = StyleDimension.FromPixels(76),
			BackgroundColor = new Color(22, 46, 64, 255),
			BorderColor = new Color(52, 92, 112, 255)
		});

		panel.Append(new UIText(_record.WorldName, 1f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(8)
		});

		panel.Append(new UIText("World ID " + HashUtil.Short(_record.WorldId, 18), 0.6f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(46),
			TextColor = new Color(132, 176, 190)
		});

		_text.Left = StyleDimension.FromPixels(24);
		_text.Top = StyleDimension.FromPixels(88);
		_text.Width = StyleDimension.FromPixels(panelWidth - 48);
		_text.Height = StyleDimension.FromPixels(panelHeight - 250);
		_text.TextOriginX = 0f;
		_text.TextColor = new Color(185, 215, 230);
		_text.SetText(SharedWorldsService.Instance.DescribeRecord(_record) + DescribeFork() + DescribeHistory());
		panel.Append(_text);

		float firstRow = panelHeight - 142;
		float secondRow = panelHeight - 84;

		panel.Append(SharedWorldsUIState.MakeButton("Copy invite", 24, firstRow, 130, () => {
			_parent.CopyInvite(_record);
			_parent.ShowStatus("Invite copied (if this world has a Workshop item).");
		}));

		panel.Append(SharedWorldsUIState.MakeButton("Publish pending", 164, firstRow, 150, () => {
			if (!_record.PendingUpload) {
				_parent.ShowStatus("There is no pending snapshot for this world.");
				return;
			}

			_parent.ShowStatus("Uploading pending snapshot...");
			_ = Task.Run(async () => {
				try {
					UploadResult result = await SharedWorldsService.Instance.PublishNowAsync(_record, null, CancellationToken.None);
					Main.QueueMainThreadAction(() => _parent.ShowStatus(result.Success ? "Published pending snapshot." : "Upload pending: " + result.Message));
				}
				catch (Exception e) {
					Log.Error("Pending Workshop upload failed.", e);
					Main.QueueMainThreadAction(() => _parent.ShowStatus("Upload pending: " + e.Message));
				}
			});
		}));

		panel.Append(SharedWorldsUIState.MakeButton("Open backups", 324, firstRow, 140, () => {
			string dir = Path.Combine(SharedWorldPaths.BackupsRoot, _record.WorldId);
			if (Directory.Exists(dir))
				Utils.OpenFolder(dir);
			else
				_parent.ShowStatus("No local backups for this world yet.");
		}));

		panel.Append(SharedWorldsUIState.MakeButton("Back", panelWidth - 104, firstRow, 80, () => {
			SoundEngine.PlaySound(SoundID.MenuClose);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}));

		SyncReport? fork = _parent.LastSyncReport;
		if (fork?.Status == SyncStatus.Fork) {
			List<VersionRecord> remotes = fork.ForkHeads
				.Where(h => !string.Equals(h.Hash, _record.LocalHash, StringComparison.OrdinalIgnoreCase))
				.Where(h => ulong.TryParse(h.SourceItemId, out _))
				.Take(3)
				.ToList();

			for (int i = 0; i < remotes.Count; i++) {
				VersionRecord head = remotes[i];
				ulong itemId = ulong.Parse(head.SourceItemId!);
				float left = 24 + i * 208;
				panel.Append(SharedWorldsUIState.MakeButton(
					$"Use {FormatUtil.ShortTimestamp(head.TimestampUtc)}",
					left, secondRow, 196,
					() => ResolveWithRemote(itemId)));
			}
		}
	}

	private ulong _pendingRemoteItemId;

	private void ResolveWithRemote(ulong itemId)
	{
		if (_pendingRemoteItemId != itemId) {
			_pendingRemoteItemId = itemId;
			_parent.ShowStatus("Click the same timestamp button again to confirm. The local branch will be backed up first.");
			return;
		}

		_pendingRemoteItemId = 0;
		_parent.ShowStatus("Installing the selected snapshot...");
		_ = Task.Run(async () => {
			(bool success, string message) = await SharedWorldsService.Instance.InstallRemoteSnapshotAsync(_record, itemId, null, CancellationToken.None);
			Main.QueueMainThreadAction(() => {
				_parent.ShowStatus(success ? "Remote version installed. " + message : "Could not install remote version: " + message);
				Main.MenuUI.SetState(_parent);
				Main.menuMode = 888;
			});
		});
	}

	private string DescribeFork()
	{
		SyncReport? report = _parent.LastSyncReport;
		if (report?.Status != SyncStatus.Fork)
			return "";

		var sb = new System.Text.StringBuilder("\n\nFORK: more than one saved branch exists.\n");
		foreach (VersionRecord head in report.ForkHeads) {
			sb.AppendLine($"  {FormatUtil.Timestamp(head.TimestampUtc)}  ·  host {WorkshopService.PersonaName(head.HostSteamId)}  ·  item {head.SourceItemId ?? "?"}");
		}

		sb.AppendLine("Nothing has been overwritten. Use one of the version buttons below to install that branch (it will be backed up), or Back to keep the local version.");
		return sb.ToString();
	}

	private string DescribeHistory()
	{
		if (_record.History.Count == 0)
			return "\n\nNo version history recorded yet.";

		var sb = new System.Text.StringBuilder("\n\nSaved snapshots (newest first):\n");
		foreach (VersionRecord version in _record.History.OrderByDescending(v => v.TimestampUtc).Take(8)) {
			sb.AppendLine($"  {FormatUtil.Timestamp(version.TimestampUtc)}  ·  host {WorkshopService.PersonaName(version.HostSteamId)}  ·  {HashUtil.Short(version.Hash)}");
		}

		if (_record.History.Count > 8)
			sb.AppendLine($"  ... and {_record.History.Count - 8} older record(s)");

		return sb.ToString();
	}
}
