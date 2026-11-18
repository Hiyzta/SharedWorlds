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

	public static void Close()
	{
		Main.MenuUI.SetState(null);
		Main.menuMode = 0;
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
	public SyncReport? LastSyncReport { get; private set; }

	public override void OnInitialize()
	{
		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(880),
			Height = StyleDimension.FromPixels(640),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(16, 28, 42, 240)
		};
		Append(panel);

		var title = new UIText("Shared Worlds", 1.15f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(16)
		};
		panel.Append(title);

		var subtitle = new UIText("One world, one latest snapshot, any participant can host.", 0.75f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(48),
			TextColor = new Color(160, 200, 220)
		};
		panel.Append(subtitle);

		var listPanel = new UIPanel {
			Left = StyleDimension.FromPixels(20),
			Top = StyleDimension.FromPixels(82),
			Width = StyleDimension.FromPixels(840),
			Height = StyleDimension.FromPixels(430),
			BackgroundColor = new Color(8, 18, 28, 220)
		};
		panel.Append(listPanel);

		_list = new UIList {
			Left = StyleDimension.FromPixels(8),
			Top = StyleDimension.FromPixels(8),
			Width = StyleDimension.FromPixelsAndPercent(-34, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-16, 1f)
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

		_status = new UIText("", 0.72f) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(522),
			Width = StyleDimension.FromPixels(832),
			Height = StyleDimension.FromPixels(48),
			IsWrapped = true,
			TextColor = new Color(180, 220, 235)
		};
		panel.Append(_status);

		panel.Append(MakeButton("Refresh", 24, 584, 110, () => RefreshList()));
		panel.Append(MakeButton("Register world", 144, 584, 150, OpenPicker));
		panel.Append(MakeButton("Import invite", 304, 584, 140, OpenImport));
		panel.Append(MakeButton("Back", 752, 584, 80, SharedWorldsUI.Close));
	}

	public override void OnActivate()
	{
		RefreshList();
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
		SharedWorldRecord? record = SharedWorldsService.Instance.ImportInvite(text, out string error);
		Main.MenuUI.SetState(this);
		Main.menuMode = 888;
		RefreshList();
		_status.SetText(record == null ? "Import failed: " + error : $"Imported '{record.WorldName}'. Use Sync to download the newest snapshot.");
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

		Width = StyleDimension.FromPixelsAndPercent(-8, 1f);
		Height = StyleDimension.FromPixels(96);
		BackgroundColor = new Color(18, 38, 54, 235);
		BorderColor = new Color(50, 90, 110, 255);

		var name = new UIText(record.WorldName, 0.9f) {
			Left = StyleDimension.FromPixels(14),
			Top = StyleDimension.FromPixels(8),
			Width = StyleDimension.FromPixels(490)
		};
		Append(name);

		string host = WorkshopService.PersonaName(record.LastHostSteamId);
		string version = record.LocalVersion == 0 ? "no local snapshot" : $"v{record.LocalVersion} {HashUtil.Short(record.LocalHash)}";
		string pending = record.PendingUpload ? "  |  upload pending" : "";
		var detail = new UIText($"{version}  |  last host: {host}  |  item: {(record.WorkshopItemId == 0 ? "not created" : record.WorkshopItemId.ToString())}{pending}", 0.68f) {
			Left = StyleDimension.FromPixels(14),
			Top = StyleDimension.FromPixels(38),
			Width = StyleDimension.FromPixels(500),
			TextColor = record.PendingUpload ? new Color(245, 200, 120) : new Color(160, 200, 215)
		};
		Append(detail);

		var hint = new UIText(record.Participants.Count == 0 ? "Only this account so far" : $"{record.Participants.Count} participant account(s)", 0.62f) {
			Left = StyleDimension.FromPixels(14),
			Top = StyleDimension.FromPixels(64),
			TextColor = new Color(130, 165, 180)
		};
		Append(hint);

		Append(MakeSmallButton("Host", 520, 8, () => _owner.BeginSync(_record, hostAfter: true)));
		Append(MakeSmallButton("Sync", 615, 8, () => _owner.BeginSync(_record, hostAfter: false)));
		Append(MakeSmallButton("Invite", 710, 8, () => _owner.CopyInvite(_record)));
		Append(MakeSmallButton("Info", 615, 50, () => _owner.OpenDetail(_record)));
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
	private readonly UIList _list = new();

	public SharedWorldPickerUIState(SharedWorldsUIState parent)
	{
		_parent = parent;
	}

	public override void OnInitialize()
	{
		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(760),
			Height = StyleDimension.FromPixels(560),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(16, 28, 42, 245)
		};
		Append(panel);

		panel.Append(new UIText("Register a local world", 1f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(16)
		});

		panel.Append(new UIText("Steam Cloud worlds are not supported yet; move the world to local storage first.", 0.65f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(50),
			TextColor = new Color(160, 190, 205)
		});

		_list.Left = StyleDimension.FromPixels(20);
		_list.Top = StyleDimension.FromPixels(88);
		_list.Width = StyleDimension.FromPixels(720);
		_list.Height = StyleDimension.FromPixels(380);
		panel.Append(_list);

		panel.Append(SharedWorldsUIState.MakeButton("Back", 640, 500, 100, GoBack));

		foreach (WorldFileData world in Main.WorldList.OrderBy(w => w.Name)) {
			bool registered = SharedWorldsService.Instance.FindByFileName(Path.GetFileName(world.Path)) != null;
			string suffix = world.IsCloudSave ? "  (Steam Cloud - unsupported)"
				: registered ? "  (already shared)"
				: "";

			var button = new UITextPanel<string>(world.Name + suffix, 0.72f) {
				Width = StyleDimension.FromPixelsAndPercent(-8, 1f),
				Height = StyleDimension.FromPixels(44)
			};

			if (!registered && !world.IsCloudSave) {
				button.OnLeftClick += (_, _) => {
					SoundEngine.PlaySound(SoundID.MenuTick);
					SharedWorldRecord record = SharedWorldsService.Instance.RegisterWorld(world, out string error);
					Main.MenuUI.SetState(_parent);
					Main.menuMode = 888;
					_parent.ShowStatus(record == null ? "Could not register: " + error : $"Registered '{record.WorldName}'. Host it once and save to publish the first snapshot.");
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
		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(860),
			Height = StyleDimension.FromPixels(600),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(16, 28, 42, 245)
		};
		Append(panel);

		panel.Append(new UIText(_record.WorldName, 1f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(16)
		});

		_text.Left = StyleDimension.FromPixels(24);
		_text.Top = StyleDimension.FromPixels(64);
		_text.Width = StyleDimension.FromPixels(812);
		_text.Height = StyleDimension.FromPixels(400);
		_text.TextColor = new Color(185, 215, 230);
		_text.SetText(SharedWorldsService.Instance.DescribeRecord(_record) + DescribeFork() + DescribeHistory());
		panel.Append(_text);

		panel.Append(SharedWorldsUIState.MakeButton("Copy invite", 24, 500, 130, () => {
			_parent.CopyInvite(_record);
			_parent.ShowStatus("Invite copied (if this world has a Workshop item).");
		}));

		panel.Append(SharedWorldsUIState.MakeButton("Publish pending", 164, 500, 150, () => {
			if (!_record.PendingUpload) {
				_parent.ShowStatus("There is no pending snapshot for this world.");
				return;
			}

			_parent.ShowStatus("Uploading pending snapshot...");
			_ = Task.Run(async () => {
				UploadResult result = await SharedWorldsService.Instance.PublishNowAsync(_record, null, CancellationToken.None);
				Main.QueueMainThreadAction(() => _parent.ShowStatus(result.Success ? "Published pending snapshot." : "Upload pending: " + result.Message));
			});
		}));

		panel.Append(SharedWorldsUIState.MakeButton("Open backups", 324, 500, 140, () => {
			string dir = Path.Combine(SharedWorldPaths.BackupsRoot, _record.WorldId);
			if (Directory.Exists(dir))
				Utils.OpenFolder(dir);
			else
				_parent.ShowStatus("No local backups for this world yet.");
		}));

		SyncReport? fork = _parent.LastSyncReport;
		if (fork?.Status == SyncStatus.Fork &&
			fork.BestRemote != null &&
			ulong.TryParse(fork.BestRemote.SourceItemId, out ulong remoteItemId) &&
			remoteItemId != 0) {
			panel.Append(SharedWorldsUIState.MakeButton($"Use remote v{fork.BestRemote.Version}", 484, 500, 190, () => ResolveWithRemote(remoteItemId, fork.BestRemote.Version)));
		}

		panel.Append(SharedWorldsUIState.MakeButton("Back", 756, 500, 80, () => {
			SoundEngine.PlaySound(SoundID.MenuClose);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}));
	}

	private bool _confirmRemote;

	private void ResolveWithRemote(ulong itemId, int version)
	{
		if (!_confirmRemote) {
			_confirmRemote = true;
			_parent.ShowStatus($"Click 'Use remote v{version}' again to confirm. The local branch will be backed up first.");
			return;
		}

		_parent.ShowStatus($"Installing remote v{version}...");
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

		var sb = new System.Text.StringBuilder("\n\nFORK: more than one version exists.\n");
		foreach (VersionRecord head in report.ForkHeads) {
			sb.AppendLine($"  v{head.Version}  {HashUtil.Short(head.Hash)}  host {WorkshopService.PersonaName(head.HostSteamId)}  item {head.SourceItemId ?? "?"}");
		}

		sb.AppendLine("Nothing has been overwritten. Use 'Use remote' below, or keep the local version and let the other branch stay in backups.");
		return sb.ToString();
	}

	private string DescribeHistory()
	{
		if (_record.History.Count == 0)
			return "\n\nNo version history recorded yet.";

		var sb = new System.Text.StringBuilder("\n\nVersion history (newest first):\n");
		foreach (VersionRecord version in _record.History.OrderByDescending(v => v.Version).Take(20)) {
			sb.AppendLine($"  v{version.Version}  {HashUtil.Short(version.Hash)}  parent {HashUtil.Short(version.ParentHash)}  host {WorkshopService.PersonaName(version.HostSteamId)}  {version.TimestampUtc:yyyy-MM-dd HH:mm} UTC");
		}

		if (_record.History.Count > 20)
			sb.AppendLine($"  ... and {_record.History.Count - 20} older record(s)");

		return sb.ToString();
	}
}
