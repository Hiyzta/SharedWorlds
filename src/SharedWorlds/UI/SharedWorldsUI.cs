using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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

/// <summary>
/// The setup surface: register worlds, import invites, sync, publish pending uploads and
/// resolve forks. Framed like the Play screen so both read as one feature.
/// </summary>
public sealed class SharedWorldsUIState : UIState
{
	private const float ContainerBottomGap = 110f;
	private const float ToolbarHeight = 46f;

	private readonly List<SharedWorldRecord> _records = new();
	private UIList _list = null!;
	private UIPanel _container = null!;
	private UIScrollbar _scrollbar = null!;
	private bool _scrollbarAttached;
	private UIText _status = null!;
	private Color _statusColor = SwUI.Muted;
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
		UIElement root = SwUI.MakeRoot();
		Append(root);
		root.Append(SwUI.TabTitle("Shared Worlds"));

		_container = SwUI.MakeContainer(ContainerBottomGap);
		root.Append(_container);

		// Toolbar: status text left, the two setup actions right.
		_status = new UIText("", 0.75f) {
			Left = StyleDimension.FromPixels(6f),
			Top = StyleDimension.FromPixels(10f),
			Width = StyleDimension.FromPixelsAndPercent(-320f, 1f),
			Height = StyleDimension.FromPixels(26f),
			TextOriginX = 0f,
			TextColor = _statusColor
		};
		_container.Append(_status);

		var register = SwUI.Button("Register world", 150, OpenPicker);
		register.HAlign = 1f;
		register.Left = StyleDimension.FromPixels(-6f);
		register.Top = StyleDimension.FromPixels(5f);
		_container.Append(register);

		var import = SwUI.Button("Import invite", 140, OpenImport);
		import.HAlign = 1f;
		import.Left = StyleDimension.FromPixels(-162f);
		import.Top = StyleDimension.FromPixels(5f);
		_container.Append(import);

		_list = new UIList {
			Top = StyleDimension.FromPixels(ToolbarHeight),
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixelsAndPercent(-ToolbarHeight, 1f),
			ListPadding = 5f
		};
		_container.Append(_list);

		_scrollbar = new UIScrollbar();
		_scrollbar.SetView(100f, 1000f);
		_scrollbar.HAlign = 1f;
		_scrollbar.Top = StyleDimension.FromPixels(ToolbarHeight);
		_scrollbar.Height = StyleDimension.FromPixelsAndPercent(-ToolbarHeight, 1f);
		_list.SetScrollbar(_scrollbar);

		root.Append(SwUI.FatButton("Back", () => {
			SoundEngine.PlaySound(SoundID.MenuClose);
			if (_onBack != null)
				_onBack();
			else
				SharedWorldsUI.Close();
		}, 0f));

		root.Append(SwUI.FatButton("Play", () => {
			SoundEngine.PlaySound(SoundID.MenuOpen);
			if (_onPlay != null)
				_onPlay();
			else
				SharedWorldsUI.OpenPlay(0);
		}, 1f));
	}

	public override void Recalculate()
	{
		if (_scrollbar != null) {
			if (_scrollbarAttached && !_scrollbar.CanScroll) {
				_container.RemoveChild(_scrollbar);
				_scrollbarAttached = false;
				_list.Width = StyleDimension.FromPercent(1f);
			}
			else if (!_scrollbarAttached && _scrollbar.CanScroll) {
				_container.Append(_scrollbar);
				_scrollbarAttached = true;
				_list.Width = StyleDimension.FromPixelsAndPercent(-25f, 1f);
			}
		}

		base.Recalculate();
	}

	public override void OnActivate()
	{
		SharedWorldsService.Instance.Changed -= OnServiceChanged;
		SharedWorldsService.Instance.Changed += OnServiceChanged;
		ShowStatus("Register a world, import an invite, then keep each world synced from here.");
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
				ShowStatus("Steam Workshop EULA is accepted. Use Publish pending when ready.", SwUI.Good);
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
		Color color;
		bool ok = false;
		try {
			SyncReport report = task.Result;
			LastSyncReport = report;
			message = FormatReport(report);
			ok = report.Status is not SyncStatus.Fork and not SyncStatus.Error;
			color = report.Status switch {
				SyncStatus.UpToDate or SyncStatus.RemoteNewer or SyncStatus.LocalNewer => SwUI.Good,
				SyncStatus.LocalOnly => SwUI.Muted,
				_ => SwUI.Warn
			};
		}
		catch (Exception e) {
			message = "Error: " + e.Message;
			color = SwUI.Warn;
			Log.Error("Shared Worlds operation failed.", e);
		}

		ShowStatus(message, color);
		RefreshList();

		if (ok && !string.IsNullOrEmpty(_hostAfterSync)) {
			string worldId = _hostAfterSync;
			_hostAfterSync = "";
			SharedWorldRecord? record = SharedWorldsService.Instance.Registry.FindByWorldId(worldId);
			if (record != null && !HostAndPlay.Start(record, out string error))
				ShowStatus(error, SwUI.Warn);
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
			_list.Add(BuildEmptyState());
		}
		else {
			foreach (SharedWorldRecord record in _records)
				_list.Add(new ManageWorldCard(record, this));
		}

		_list.Recalculate();
	}

	private UIElement BuildEmptyState()
	{
		var empty = new UIPanel {
			Height = StyleDimension.FromPixels(240f),
			BackgroundColor = SwUI.ItemBg,
			BorderColor = SwUI.ItemBorder
		};
		empty.SetPadding(12f);

		empty.Append(new UIText("No shared worlds yet", 0.95f, large: true) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(30f)
		});

		empty.Append(new UIText("Register a world from this machine, or paste an invite code from a friend.", 0.75f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(74f),
			Width = StyleDimension.FromPixelsAndPercent(-30f, 1f),
			IsWrapped = true,
			TextColor = SwUI.Muted
		});

		var register = SwUI.Button("Register world", 170, OpenPicker);
		register.HAlign = 0.25f;
		register.VAlign = 1f;
		register.Top = StyleDimension.FromPixels(-14f);
		empty.Append(register);

		var import = SwUI.Button("Import invite", 170, OpenImport);
		import.HAlign = 0.75f;
		import.VAlign = 1f;
		import.Top = StyleDimension.FromPixels(-14f);
		empty.Append(import);

		return empty;
	}

	public void BeginSync(SharedWorldRecord record, bool hostAfter)
	{
		if (_busy)
			return;

		if (hostAfter) {
			LiveWorldSession? live = SharedWorldsLiveService.FindLive(record);
			if (live != null && live.HostSteamId != WorkshopService.CurrentSteamId) {
				ShowStatus($"{WorkshopService.PersonaName(live.HostSteamId)} is already hosting this world. Use Play Shared Worlds to join instead.", SwUI.Warn);
				return;
			}
		}

		_busy = true;
		_hostAfterSync = hostAfter ? record.WorldId : "";
		ShowStatus($"Checking Steam Workshop for '{record.WorldName}'...", SwUI.Bright);
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
			ShowStatus(record == null
				? "Import failed: " + error
				: $"Imported '{record.WorldName}'. Use Sync to download the newest snapshot.",
				record == null ? SwUI.Warn : SwUI.Good);
		}
		catch (Exception e) {
			Log.Error("Could not import a shared-world invite.", e);
			Main.MenuUI.SetState(this);
			Main.menuMode = 888;
			ShowStatus("Import failed: " + e.Message, SwUI.Warn);
		}
	}

	public void CopyInvite(SharedWorldRecord record)
	{
		string code = SharedWorldsService.Instance.CreateInvite(record);
		if (string.IsNullOrEmpty(code)) {
			ShowStatus("Invite unavailable: publish this world once so it has a Workshop item.", SwUI.Warn);
			return;
		}

		try {
			Platform.Get<IClipboard>().Value = code;
			ShowStatus("Invite code copied to the clipboard. It contains the world ID and this account's Workshop item ID.", SwUI.Good);
		}
		catch (Exception e) {
			ShowStatus("Could not copy the invite code: " + e.Message, SwUI.Warn);
		}
	}

	public void ShowStatus(string text, Color color)
	{
		_status?.SetText(text);
		if (_status != null)
			_status.TextColor = color;
	}

	public void ShowStatus(string text) => ShowStatus(text, SwUI.Muted);
}

/// <summary>One shared world on the Manage screen: snapshot info plus its maintenance actions.</summary>
internal sealed class ManageWorldCard : UIPanel
{
	private const float CardHeight = 104f;

	private readonly SharedWorldRecord _record;
	private readonly SharedWorldsUIState _owner;
	private readonly UIText _participants;

	public ManageWorldCard(SharedWorldRecord record, SharedWorldsUIState owner)
	{
		_record = record;
		_owner = owner;

		Width = StyleDimension.FromPercent(1f);
		Height = StyleDimension.FromPixels(CardHeight);
		SetPadding(6f);

		var sync = SwUI.Button("Sync", 90, () => _owner.BeginSync(_record, hostAfter: false));
		sync.VAlign = 1f;
		sync.Left = StyleDimension.FromPixels(4f);
		Append(sync);

		var host = SwUI.Button("Host & play", 150, () => _owner.BeginSync(_record, hostAfter: true));
		host.VAlign = 1f;
		host.Left = StyleDimension.FromPixels(100f);
		Append(host);

		var invite = SwUI.Button("Invite", 100, () => _owner.CopyInvite(_record));
		invite.VAlign = 1f;
		invite.Left = StyleDimension.FromPixels(256f);
		Append(invite);

		var info = SwUI.Button("Info", 90, () => _owner.OpenDetail(_record));
		info.VAlign = 1f;
		info.Left = StyleDimension.FromPixels(362f);
		Append(info);

		_participants = new UIText("", 0.7f) {
			HAlign = 1f,
			VAlign = 1f,
			Top = StyleDimension.FromPixels(-10f),
			Width = StyleDimension.FromPixels(210f),
			TextOriginX = 1f,
			TextColor = SwUI.Muted
		};
		Append(_participants);

		Refresh();
	}

	public override void MouseOver(UIMouseEvent evt)
	{
		base.MouseOver(evt);
		SwUI.SetItemColors(this, hovered: true);
	}

	public override void MouseOut(UIMouseEvent evt)
	{
		base.MouseOut(evt);
		SwUI.SetItemColors(this, hovered: false, dimmed: _record.PendingUpload && _record.PendingNeedsEula);
	}

	private void Refresh()
	{
		string accounts = _record.Participants.Count == 0
			? "only this account"
			: _record.Participants.Count + " participant account(s)";
		_participants.SetText($"{accounts} · {_record.History.Count} saved snapshot(s)");
	}

	protected override void DrawSelf(SpriteBatch sb)
	{
		base.DrawSelf(sb);

		CalculatedStyle inner = GetInnerDimensions();

		string name = SwUI.CropToFit(_record.WorldName, inner.Width - 20f);
		Utils.DrawBorderString(sb, name, new Vector2(inner.X, inner.Y - 2f), Color.White);

		SwUI.DrawDivider(sb, new Vector2(inner.X, inner.Y + 24f), inner.Width);

		Vector2 tagPos = new(inner.X, inner.Y + 32f);
		float savedWidth = 150f;
		float itemWidth = 200f;
		float stateWidth = Math.Max(180f, inner.Width - savedWidth - itemWidth - 10f);

		string savedText = _record.LastUpdatedUtc == default
			? "Never saved"
			: "Saved " + FormatUtil.Relative(_record.LastUpdatedUtc);
		Color savedColor = _record.LastUpdatedUtc == default ? SwUI.Muted : Color.White;

		string itemText = _record.WorkshopItemId == 0 ? "No Workshop item yet" : "Item " + _record.WorkshopItemId;
		Color itemColor = _record.WorkshopItemId == 0 ? SwUI.Muted : Color.White;

		string stateText;
		Color stateColor;
		if (_record.PendingNeedsEula) {
			stateText = "Upload waiting for the Workshop Legal Agreement";
			stateColor = SwUI.Warn;
		}
		else if (_record.PendingUpload) {
			stateText = "A newer local snapshot is waiting to upload";
			stateColor = SwUI.Warn;
		}
		else if (_record.WorkshopItemId == 0) {
			stateText = "Host and save once to publish the first snapshot";
			stateColor = SwUI.Muted;
		}
		else {
			stateText = "No pending upload";
			stateColor = SwUI.Good;
		}

		SwUI.DrawTag(sb, tagPos, savedWidth, savedText, savedColor);
		SwUI.DrawTag(sb, new Vector2(tagPos.X + savedWidth + 5f, tagPos.Y), itemWidth, itemText, itemColor);
		SwUI.DrawTag(sb, new Vector2(tagPos.X + savedWidth + itemWidth + 10f, tagPos.Y), stateWidth, stateText, stateColor);
	}
}

public sealed class SharedWorldPickerUIState : UIState
{
	private const float ContainerBottomGap = 110f;

	private readonly SharedWorldsUIState _parent;
	private UIList _list = new() { ListPadding = 5f };
	private UIPanel _container = null!;
	private UIScrollbar _scrollbar = null!;
	private bool _scrollbarAttached;

	public SharedWorldPickerUIState(SharedWorldsUIState parent)
	{
		_parent = parent;
	}

	public override void OnInitialize()
	{
		UIElement root = SwUI.MakeRoot(670f);
		Append(root);
		root.Append(SwUI.TabTitle("Register a World"));

		_container = SwUI.MakeContainer(ContainerBottomGap);
		root.Append(_container);

		_container.Append(new UIText("Steam Cloud worlds are not supported yet; move the world to local storage first.", 0.7f) {
			Left = StyleDimension.FromPixels(6f),
			Top = StyleDimension.FromPixels(10f),
			Width = StyleDimension.FromPixelsAndPercent(-12f, 1f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = SwUI.Muted
		});

		_list = new UIList {
			Top = StyleDimension.FromPixels(46f),
			Width = StyleDimension.FromPercent(1f),
			Height = StyleDimension.FromPixelsAndPercent(-46f, 1f),
			ListPadding = 5f
		};
		_container.Append(_list);

		_scrollbar = new UIScrollbar();
		_scrollbar.SetView(100f, 1000f);
		_scrollbar.HAlign = 1f;
		_scrollbar.Top = StyleDimension.FromPixels(46f);
		_scrollbar.Height = StyleDimension.FromPixelsAndPercent(-46f, 1f);
		_list.SetScrollbar(_scrollbar);

		var hint = new UIText("Click a world to register it as a shared world.", 0.75f) {
			Width = StyleDimension.FromPercent(1f),
			VAlign = 1f,
			Height = StyleDimension.FromPixels(30f),
			TextOriginX = 0f,
			TextColor = SwUI.Bright
		};
		root.Append(hint);

		root.Append(SwUI.FatButton("Back", () => {
			SoundEngine.PlaySound(SoundID.MenuClose);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}, 0f));

		foreach (WorldFileData world in Main.WorldList.Where(w => w.IsValid).OrderBy(w => w.Name)) {
			bool registered = SharedWorldsService.Instance.FindByFileName(Path.GetFileName(world.Path)) != null;
			string suffix = world.IsCloudSave ? "  (Steam Cloud — unsupported)"
				: registered ? "  (already shared)"
				: "";
			bool available = !registered && !world.IsCloudSave;

			var button = new UITextPanel<string>(SwUI.CropToFit(world.Name + suffix, 560f, 0.8f), 0.8f) {
				Width = StyleDimension.FromPercent(1f),
				Height = StyleDimension.FromPixels(46f)
			};

			if (available) {
				SwUI.WireHover(button);
				button.OnLeftClick += (_, _) => {
					try {
						SharedWorldRecord record = SharedWorldsService.Instance.RegisterWorld(world, out string error);
						Main.MenuUI.SetState(_parent);
						Main.menuMode = 888;
						_parent.ShowStatus(record == null
							? "Could not register: " + error
							: $"Registered '{record.WorldName}'. Host it once and save to publish the first snapshot.",
							record == null ? SwUI.Warn : SwUI.Good);
					}
					catch (Exception e) {
						Log.Error("Could not register a shared world.", e);
						Main.MenuUI.SetState(_parent);
						Main.menuMode = 888;
						_parent.ShowStatus("Could not register: " + e.Message, SwUI.Warn);
					}
				};
			}
			else {
				button.IgnoresMouseInteraction = true;
				SwUI.SetItemColors(button, hovered: false, dimmed: true);
			}

			_list.Add(button);
		}

		if (_list.Count == 0)
			_list.Add(new UIText("No local worlds found.", 0.8f) { TextColor = SwUI.Muted });
	}

	public override void Recalculate()
	{
		if (_scrollbar != null) {
			if (_scrollbarAttached && !_scrollbar.CanScroll) {
				_container.RemoveChild(_scrollbar);
				_scrollbarAttached = false;
				_list.Width = StyleDimension.FromPercent(1f);
			}
			else if (!_scrollbarAttached && _scrollbar.CanScroll) {
				_container.Append(_scrollbar);
				_scrollbarAttached = true;
				_list.Width = StyleDimension.FromPixelsAndPercent(-25f, 1f);
			}
		}

		base.Recalculate();
	}
}

public sealed class SharedWorldDetailUIState : UIState
{
	private const float ContainerBottomGap = 110f;

	private readonly SharedWorldRecord _record;
	private readonly SharedWorldsUIState _parent;

	public SharedWorldDetailUIState(SharedWorldRecord record, SharedWorldsUIState parent)
	{
		_record = record;
		_parent = parent;
	}

	public override void OnInitialize()
	{
		UIElement root = SwUI.MakeRoot(670f);
		Append(root);
		root.Append(SwUI.TabTitle(SwUI.CropToFit(_record.WorldName, 260f)));

		var container = SwUI.MakeContainer(ContainerBottomGap);
		root.Append(container);

		var body = new UIText(Describe(), 0.8f) {
			Left = StyleDimension.FromPixels(10f),
			Top = StyleDimension.FromPixels(10f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-120f, 1f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = Color.White
		};
		container.Append(body);

		// Maintenance row inside the container.
		var invite = SwUI.Button("Copy invite", 130, () => {
			_parent.CopyInvite(_record);
		});
		invite.VAlign = 1f;
		invite.Left = StyleDimension.FromPixels(4f);
		container.Append(invite);

		var publish = SwUI.Button("Publish pending", 160, PublishPending);
		publish.VAlign = 1f;
		publish.Left = StyleDimension.FromPixels(140f);
		container.Append(publish);

		var backups = SwUI.Button("Open backups", 150, () => {
			string dir = Path.Combine(SharedWorldPaths.BackupsRoot, _record.WorldId);
			if (Directory.Exists(dir))
				Utils.OpenFolder(dir);
			else
				_parent.ShowStatus("No local backups for this world yet.", SwUI.Warn);
		});
		backups.VAlign = 1f;
		backups.Left = StyleDimension.FromPixels(306f);
		container.Append(backups);

		// Fork choices, when a sync found two branches.
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
				var choice = SwUI.Button($"Use {FormatUtil.ShortTimestamp(head.TimestampUtc)}", 196, () => ResolveWithRemote(itemId));
				choice.VAlign = 1f;
				choice.HAlign = 1f;
				choice.Left = StyleDimension.FromPixels(-10f - i * 202f);
				container.Append(choice);
			}
		}

		root.Append(SwUI.FatButton("Back", () => {
			SoundEngine.PlaySound(SoundID.MenuClose);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}, 0f));

		root.Append(SwUI.FatButton("Sync now", () => {
			_parent.BeginSync(_record, hostAfter: false);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}, 1f));
	}

	private ulong _pendingRemoteItemId;

	private void PublishPending()
	{
		if (!_record.PendingUpload) {
			_parent.ShowStatus("There is no pending snapshot for this world.", SwUI.Warn);
			return;
		}

		_parent.ShowStatus("Uploading pending snapshot...", SwUI.Bright);
		_ = Task.Run(async () => {
			try {
				UploadResult result = await SharedWorldsService.Instance.PublishNowAsync(_record, null, CancellationToken.None);
				Main.QueueMainThreadAction(() => _parent.ShowStatus(
					result.Success ? "Published the pending snapshot." : "Upload pending: " + result.Message,
					result.Success ? SwUI.Good : SwUI.Warn));
			}
			catch (Exception e) {
				Log.Error("Pending Workshop upload failed.", e);
				Main.QueueMainThreadAction(() => _parent.ShowStatus("Upload pending: " + e.Message, SwUI.Warn));
			}
		});
	}

	private void ResolveWithRemote(ulong itemId)
	{
		if (_pendingRemoteItemId != itemId) {
			_pendingRemoteItemId = itemId;
			_parent.ShowStatus("Click the same timestamp button again to confirm. The local branch will be backed up first.", SwUI.Warn);
			return;
		}

		_pendingRemoteItemId = 0;
		_parent.ShowStatus("Installing the selected snapshot...", SwUI.Bright);
		_ = Task.Run(async () => {
			(bool success, string message) = await SharedWorldsService.Instance.InstallRemoteSnapshotAsync(_record, itemId, null, CancellationToken.None);
			Main.QueueMainThreadAction(() => {
				_parent.ShowStatus(success ? "Remote version installed. " + message : "Could not install remote version: " + message,
					success ? SwUI.Good : SwUI.Warn);
				Main.MenuUI.SetState(_parent);
				Main.menuMode = 888;
			});
		});
	}

	private string Describe()
	{
		string core = SharedWorldsService.Instance.DescribeRecord(_record);
		return core + DescribeFork() + DescribeHistory();
	}

	private string DescribeFork()
	{
		SyncReport? report = _parent.LastSyncReport;
		if (report?.Status != SyncStatus.Fork)
			return "";

		var sb = new System.Text.StringBuilder("\nFORK: more than one saved branch exists.\n");
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
