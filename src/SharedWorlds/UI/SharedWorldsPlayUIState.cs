using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.UI;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// The clean Play screen reached from the real "Play Shared Worlds" multiplayer-menu row.
/// Worlds are grouped by what a player can do right now: join a live host, host a saved
/// snapshot, or fix a sync problem.
/// </summary>
public sealed class SharedWorldsPlayUIState : UIState
{
	private readonly int _returnMenuMode;
	private readonly string? _initialStatus;
	private readonly string? _focusWorldId;
	private readonly string? _autoSyncWorldId;
	private readonly List<PlayWorldCard> _cards = new();

	private UIList _list = null!;
	private UIText _status = null!;
	private bool _busy;
	private string _statusText = "";
	private Task? _refreshTask;
	private DateTime _lastLivePollUtc = DateTime.MinValue;

	public SharedWorldsPlayUIState(int returnMenuMode = 12, string? status = null, string? focusWorldId = null, string? autoSyncWorldId = null)
	{
		_returnMenuMode = returnMenuMode;
		_initialStatus = status;
		_focusWorldId = focusWorldId;
		_autoSyncWorldId = autoSyncWorldId;
	}

	public override void OnInitialize()
	{
		float panelWidth = Math.Clamp(Main.screenWidth - 80, 760, 1040);
		float panelHeight = Math.Clamp(Main.screenHeight - 56, 540, 760);

		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(panelWidth),
			Height = StyleDimension.FromPixels(panelHeight),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(11, 21, 33, 248),
			BorderColor = new Color(48, 88, 108, 255)
		};
		Append(panel);

		var header = new UIPanel {
			Width = StyleDimension.FromPixelsAndPercent(0, 1f),
			Height = StyleDimension.FromPixels(100),
			BackgroundColor = new Color(19, 42, 59, 255),
			BorderColor = new Color(48, 88, 108, 255)
		};
		panel.Append(header);

		panel.Append(new UIText("Play Shared Worlds", 1.16f, large: true) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(2),
			TextOriginX = 0f
		});

		panel.Append(new UIText("Join a live world, or take over the latest saved snapshot.", 0.68f) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(56),
			TextOriginX = 0f,
			TextColor = new Color(126, 196, 210)
		});

		panel.Append(SharedWorldsUIState.MakeButton("Refresh", panelWidth - 376, 22, 94, RefreshAll));
		panel.Append(SharedWorldsUIState.MakeButton("Manage", panelWidth - 270, 22, 112, OpenManage));
		panel.Append(SharedWorldsUIState.MakeButton("Back", panelWidth - 146, 22, 96, GoBack));

		var listPanel = new UIPanel {
			Left = StyleDimension.FromPixels(20),
			Top = StyleDimension.FromPixels(112),
			Width = StyleDimension.FromPixels(panelWidth - 40),
			Height = StyleDimension.FromPixels(panelHeight - 206),
			BackgroundColor = new Color(7, 14, 23, 232),
			BorderColor = new Color(34, 63, 78, 255)
		};
		panel.Append(listPanel);

		_list = new UIList {
			Left = StyleDimension.FromPixels(8),
			Top = StyleDimension.FromPixels(8),
			Width = StyleDimension.FromPixelsAndPercent(-34, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-16, 1f),
			ListPadding = 7f
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
			Top = StyleDimension.FromPixels(panelHeight - 86),
			Width = StyleDimension.FromPixels(panelWidth - 40),
			Height = StyleDimension.FromPixels(48),
			BackgroundColor = new Color(8, 17, 27, 214),
			BorderColor = new Color(34, 63, 78, 255)
		};
		panel.Append(statusPanel);

		_status = new UIText("", 0.66f) {
			Left = StyleDimension.FromPixels(10),
			Top = StyleDimension.FromPixels(6),
			Width = StyleDimension.FromPixels(panelWidth - 64),
			Height = StyleDimension.FromPixels(36),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = new Color(190, 224, 234)
		};
		statusPanel.Append(_status);
	}

	public override void OnActivate()
	{
		SharedWorldsService.Instance.Changed -= OnServiceChanged;
		SharedWorldsService.Instance.Changed += OnServiceChanged;
		SharedWorldsPresence.Invalidate();
		_statusText = _initialStatus ?? "";
		RefreshList();

		if (!string.IsNullOrWhiteSpace(_autoSyncWorldId)) {
			SharedWorldRecord? record = SharedWorldsService.Instance.Registry.FindByWorldId(_autoSyncWorldId);
			if (record != null) {
				SetStatus("Original host left — the server closed. Fetching the latest snapshot...");
				BeginSync(record);
				return;
			}
		}

		RefreshAll();
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

		if ((DateTime.UtcNow - _lastLivePollUtc).TotalSeconds >= 2) {
			_lastLivePollUtc = DateTime.UtcNow;
			foreach (PlayWorldCard card in _cards)
				card.PollLive();
		}

		if (_refreshTask != null && _refreshTask.IsCompleted) {
			_refreshTask = null;
			_busy = false;
			if (string.IsNullOrWhiteSpace(_statusText))
				_statusText = "Ready. Live worlds update automatically.";
			RefreshList();
		}

		_status.SetText(_statusText);
	}

	public void SetStatus(string text)
	{
		_statusText = text ?? "";
		_status?.SetText(_statusText);
	}

	public void Reload()
	{
		RefreshList();
	}

	private void RefreshList()
	{
		List<SharedWorldRecord> records = SharedWorldsService.Instance.Registry.Snapshot()
			.OrderByDescending(r => SharedWorldsLiveService.Get(r.WorldId).Live != null)
			.ThenByDescending(r => r.LastUpdatedUtc)
			.ThenBy(r => r.WorldName)
			.ToList();

		var existing = _cards.ToDictionary(c => c.WorldId, StringComparer.OrdinalIgnoreCase);
		var next = new List<PlayWorldCard>(records.Count);

		_list.Clear();
		foreach (SharedWorldRecord record in records) {
			if (existing.TryGetValue(record.WorldId, out PlayWorldCard? card)) {
				card.UpdateRecord(record);
			}
			else {
				card = new PlayWorldCard(record, this);
				card.Activate();
			}

			next.Add(card);
			_list.Add(card);
		}

		_cards.Clear();
		_cards.AddRange(next);

		if (_cards.Count == 0) {
			var empty = new UIPanel {
				Width = StyleDimension.FromPixelsAndPercent(-6, 1f),
				Height = StyleDimension.FromPixels(170),
				BackgroundColor = new Color(14, 29, 42, 240),
				BorderColor = new Color(34, 63, 78, 255)
			};

			empty.Append(new UIText("Nothing shared yet", 0.94f) {
				HAlign = 0.5f,
				Top = StyleDimension.FromPixels(24)
			});
			empty.Append(new UIText("Register a local world once, or import an invite from another player.", 0.66f) {
				HAlign = 0.5f,
				Top = StyleDimension.FromPixels(58),
				TextColor = new Color(150, 190, 204)
			});
			empty.Append(SharedWorldsUIState.MakeButton("Register world", 210, 108, 150, OpenManage));
			empty.Append(SharedWorldsUIState.MakeButton("Import invite", 374, 108, 150, OpenManage));
			_list.Add(empty);
		}

		_list.Recalculate();
		if (!string.IsNullOrEmpty(_focusWorldId)) {
			// The list is sorted so a live focused world is already near the top; just make
			// sure the list is scrolled to its start rather than a stale position.
			_list.ViewPosition = 0f;
		}
	}

	public void RefreshAll()
	{
		if (_busy)
			return;

		_busy = true;
		SetStatus("Checking the latest snapshots and live Steam sessions...");

		_refreshTask = Task.Run(async () => {
			foreach (SharedWorldRecord record in SharedWorldsService.Instance.Registry.Snapshot().OrderByDescending(r => r.LastUpdatedUtc)) {
				try {
					await SharedWorldsLiveService.CheckAsync(record, downloadIfNewer: false).ConfigureAwait(false);
				}
				catch (Exception e) {
					Log.Warn("Play-screen refresh failed for " + record.WorldName, e);
				}
			}
		});
	}

	public void BeginJoin(SharedWorldRecord record)
	{
		if (_busy)
			return;

		LiveWorldSession? live = SharedWorldsLiveService.FindLive(record);
		if (live == null) {
			SetStatus("That world is not online right now. Host it, or refresh in a moment.");
			return;
		}

		if (!SharedWorldsUI.JoinLobby(live, out string error)) {
			SetStatus(error);
			return;
		}
	}

	public void BeginHost(SharedWorldRecord record)
	{
		if (_busy)
			return;

		LiveWorldSession? live = SharedWorldsLiveService.FindLive(record);
		if (live != null) {
			SetStatus($"{WorkshopService.PersonaName(live.HostSteamId)} is already hosting this world. Join instead of starting a second host.");
			return;
		}

		_busy = true;
		SetStatus($"Checking and preparing '{record.WorldName}'...");
		_ = Task.Run(async () => {
			(bool success, string message) = await SharedWorldsLiveService.PrepareHostAsync(record).ConfigureAwait(false);
			Main.QueueMainThreadAction(() => {
				_busy = false;
				if (!success) {
					SetStatus(message);
					RefreshList();
					return;
				}

				SharedWorldRecord? fresh = SharedWorldsService.Instance.Registry.FindByWorldId(record.WorldId) ?? record;
				bool started = HostAndPlay.Start(fresh, out string error);
				if (!started && HostAndPlay.IsWaitingForCharacter(fresh.WorldId))
					return;

				if (!started) {
					SetStatus(error);
					return;
				}

				SetStatus("Starting Host & Play...");
			});
		});
	}

	public void BeginSync(SharedWorldRecord record)
	{
		if (_busy)
			return;

		_busy = true;
		SetStatus($"Downloading the newest snapshot of '{record.WorldName}'...");
		_ = Task.Run(async () => {
			WorldLiveState state = await SharedWorldsLiveService.CheckAsync(record, downloadIfNewer: true).ConfigureAwait(false);
			Main.QueueMainThreadAction(() => {
				_busy = false;
				SetStatus(state.Report == null ? (state.Error.Length > 0 ? state.Error : "Sync finished.") : FormatReport(state.Report));
				RefreshList();
			});
		});
	}

	private static string FormatReport(SyncReport report)
	{
		return report.Status switch {
			SyncStatus.UpToDate => "Up to date. " + report.Message,
			SyncStatus.RemoteNewer => "Updated. " + report.Message,
			SyncStatus.LocalNewer => "This account has the newest snapshot. " + report.Message,
			SyncStatus.LocalOnly => "Local only. " + report.Message,
			SyncStatus.Fork => "This world needs a fork choice in Manage. " + report.Message,
			SyncStatus.WorkshopUnavailable => "Workshop unavailable; local files are unchanged. " + report.Message,
			_ => report.Message
		};
	}

	public void OpenDetails(SharedWorldRecord record)
	{
		SoundEngine.PlaySound(SoundID.MenuTick);
		Main.MenuUI.SetState(new SharedWorldPlayDetailUIState(record, this));
		Main.menuMode = 888;
	}

	private void OpenManage()
	{
		SoundEngine.PlaySound(SoundID.MenuOpen);
		Main.MenuUI.SetState(new SharedWorldsUIState(
			onBack: () => {
				Main.MenuUI.SetState(this);
				Main.menuMode = 888;
				RefreshList();
			},
			onPlay: () => {
				Main.MenuUI.SetState(this);
				Main.menuMode = 888;
				RefreshList();
			}));
		Main.menuMode = 888;
	}

	private void GoBack()
	{
		SoundEngine.PlaySound(SoundID.MenuClose);
		SharedWorldsUI.CloseTo(_returnMenuMode);
	}
}

internal sealed class PlayWorldCard : UIPanel
{
	private readonly SharedWorldsPlayUIState _owner;
	private SharedWorldRecord _record;

	private readonly UIPanel _accent;
	private readonly UIText _name;
	private readonly UIText _status;
	private readonly UIText _sync;
	private readonly UIText _detail;
	private readonly UITextPanel<string> _primary;
	private readonly UITextPanel<string> _details;

	public string WorldId => _record.WorldId;
	public bool IsLive => SharedWorldsLiveService.Get(_record.WorldId).Live != null;

	public PlayWorldCard(SharedWorldRecord record, SharedWorldsPlayUIState owner)
	{
		_record = record;
		_owner = owner;

		Width = StyleDimension.FromPixelsAndPercent(-8, 1f);
		Height = StyleDimension.FromPixels(108);
		BackgroundColor = new Color(15, 31, 44, 242);
		BorderColor = new Color(40, 75, 92, 255);

		_accent = new UIPanel {
			Left = StyleDimension.FromPixels(0),
			Top = StyleDimension.FromPixels(0),
			Width = StyleDimension.FromPixels(5),
			Height = StyleDimension.FromPixelsAndPercent(0, 1f),
			BackgroundColor = new Color(86, 190, 150, 255),
			BorderColor = new Color(0, 0, 0, 0),
			IgnoresMouseInteraction = true
		};
		Append(_accent);

		_name = new UIText(record.WorldName, 0.94f) {
			Left = StyleDimension.FromPixels(18),
			Top = StyleDimension.FromPixels(8),
			Width = StyleDimension.FromPixelsAndPercent(-270, 1f),
			TextOriginX = 0f
		};
		Append(_name);

		_status = new UIText("", 0.66f) {
			Left = StyleDimension.FromPixels(18),
			Top = StyleDimension.FromPixels(35),
			Width = StyleDimension.FromPixelsAndPercent(-270, 1f),
			TextOriginX = 0f,
			TextColor = new Color(146, 204, 218)
		};
		Append(_status);

		_sync = new UIText("", 0.62f) {
			Left = StyleDimension.FromPixels(18),
			Top = StyleDimension.FromPixels(58),
			Width = StyleDimension.FromPixelsAndPercent(-270, 1f),
			TextOriginX = 0f,
			TextColor = new Color(126, 164, 178)
		};
		Append(_sync);

		_detail = new UIText("", 0.58f) {
			Left = StyleDimension.FromPixels(18),
			Top = StyleDimension.FromPixels(80),
			Width = StyleDimension.FromPixelsAndPercent(-270, 1f),
			TextOriginX = 0f,
			TextColor = new Color(106, 142, 156)
		};
		Append(_detail);

		_primary = new UITextPanel<string>("Host", 0.68f) {
			HAlign = 1f,
			Left = StyleDimension.FromPixels(-18),
			Top = StyleDimension.FromPixels(16),
			Width = StyleDimension.FromPixels(132),
			Height = StyleDimension.FromPixels(36),
			BackgroundColor = new Color(30, 78, 94, 245),
			BorderColor = new Color(82, 174, 192, 235)
		};
		_primary.OnLeftClick += (_, _) => PrimaryClicked();
		Append(_primary);

		_details = new UITextPanel<string>("Details", 0.62f) {
			HAlign = 1f,
			Left = StyleDimension.FromPixels(-18),
			Top = StyleDimension.FromPixels(60),
			Width = StyleDimension.FromPixels(86),
			Height = StyleDimension.FromPixels(30)
		};
		_details.OnLeftClick += (_, _) => {
			SoundEngine.PlaySound(SoundID.MenuTick);
			_owner.OpenDetails(_record);
		};
		Append(_details);

		Refresh();
	}

	public void UpdateRecord(SharedWorldRecord record)
	{
		_record = record;
		Refresh();
	}

	public void PollLive()
	{
		SharedWorldsLiveService.FindLive(_record);
		Refresh();
	}

	public void Refresh()
	{
		WorldLiveState state = SharedWorldsLiveService.Get(_record.WorldId);
		SharedWorldRecord? fresh = SharedWorldsService.Instance.Registry.FindByWorldId(_record.WorldId);
		if (fresh != null)
			_record = fresh;

		_name.SetText(_record.WorldName);
		_status.SetText(SharedWorldsLiveService.StatusLine(_record, state));
		_sync.SetText(SharedWorldsLiveService.SyncLine(_record, state));

		string detail;
		if (state.Report != null) {
			detail = state.Report.Status switch {
				SyncStatus.UpToDate => "Up to date with the newest snapshot",
				SyncStatus.RemoteNewer => "A newer snapshot is available",
				SyncStatus.LocalNewer => "This account has the newest snapshot",
				SyncStatus.LocalOnly => "Local snapshot only; no Workshop item yet",
				SyncStatus.Fork => "Two branches exist — choose one in Manage",
				SyncStatus.WorkshopUnavailable => "Workshop unavailable",
				SyncStatus.Error => "Sync error — open Details",
				_ => state.Report.Message
			};
		}
		else if (_record.PendingNeedsEula) {
			detail = "Upload waiting for the Steam Workshop Legal Agreement";
		}
		else if (_record.PendingUpload) {
			detail = "Newer local snapshot waiting to upload";
		}
		else if (_record.WorkshopItemId == 0) {
			detail = "No Workshop item yet";
		}
		else {
			detail = "Workshop item ready";
		}

		_detail.SetText(detail);

		LiveWorldSession? live = state.Live;
		bool isSelf = live != null && live.HostSteamId == WorkshopService.CurrentSteamId;
		bool hasForkOrError = state.Report?.Status is SyncStatus.Fork or SyncStatus.Error;
		bool noLocal = _record.LocalVersion == 0 && string.IsNullOrEmpty(_record.LocalHash) &&
			!File.Exists(SharedWorldPaths.WorldPath(_record));

		if (live != null) {
			_primary.SetText(isSelf ? "Hosting" : "Join");
			_primary.BackgroundColor = isSelf ? new Color(28, 56, 52, 240) : new Color(28, 96, 78, 245);
			_primary.BorderColor = new Color(96, 210, 164, 235);
			_primary.IgnoresMouseInteraction = isSelf;
			_accent.BackgroundColor = new Color(92, 216, 158, 255);
			_status.TextColor = new Color(130, 230, 188);
		}
		else if (hasForkOrError) {
			_primary.SetText("Resolve");
			_primary.BackgroundColor = new Color(86, 56, 40, 245);
			_primary.BorderColor = new Color(224, 148, 100, 235);
			_primary.IgnoresMouseInteraction = false;
			_accent.BackgroundColor = new Color(236, 158, 96, 255);
			_status.TextColor = new Color(240, 188, 140);
		}
		else if (noLocal) {
			_primary.SetText("Sync");
			_primary.BackgroundColor = new Color(46, 68, 92, 245);
			_primary.BorderColor = new Color(116, 164, 196, 235);
			_primary.IgnoresMouseInteraction = false;
			_accent.BackgroundColor = new Color(116, 164, 196, 255);
			_status.TextColor = new Color(150, 190, 206);
		}
		else {
			_primary.SetText("Host");
			_primary.BackgroundColor = new Color(30, 78, 94, 245);
			_primary.BorderColor = new Color(82, 174, 192, 235);
			_primary.IgnoresMouseInteraction = false;
			_accent.BackgroundColor = new Color(86, 174, 200, 255);
			_status.TextColor = new Color(146, 204, 218);
		}

	}

	private void PrimaryClicked()
	{
		SoundEngine.PlaySound(SoundID.MenuTick);
		WorldLiveState state = SharedWorldsLiveService.Get(_record.WorldId);
		LiveWorldSession? live = state.Live;
		if (live != null) {
			if (live.HostSteamId != WorkshopService.CurrentSteamId)
				_owner.BeginJoin(_record);
			return;
		}

		if (state.Report?.Status is SyncStatus.Fork or SyncStatus.Error) {
			_owner.OpenDetails(_record);
			return;
		}

		bool noLocal = _record.LocalVersion == 0 && string.IsNullOrEmpty(_record.LocalHash) &&
			!File.Exists(SharedWorldPaths.WorldPath(_record));
		if (noLocal) {
			_owner.BeginSync(_record);
			return;
		}

		_owner.BeginHost(_record);
	}
}

public sealed class SharedWorldPlayDetailUIState : UIState
{
	private readonly SharedWorldRecord _record;
	private readonly SharedWorldsPlayUIState _parent;

	public SharedWorldPlayDetailUIState(SharedWorldRecord record, SharedWorldsPlayUIState parent)
	{
		_record = record;
		_parent = parent;
	}

	public override void OnInitialize()
	{
		float panelWidth = Math.Clamp(Main.screenWidth - 100, 700, 920);
		float panelHeight = Math.Clamp(Main.screenHeight - 80, 500, 680);

		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(panelWidth),
			Height = StyleDimension.FromPixels(panelHeight),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(11, 21, 33, 248),
			BorderColor = new Color(48, 88, 108, 255)
		};
		Append(panel);

		panel.Append(new UIPanel {
			Width = StyleDimension.FromPixelsAndPercent(0, 1f),
			Height = StyleDimension.FromPixels(82),
			BackgroundColor = new Color(19, 42, 59, 255),
			BorderColor = new Color(48, 88, 108, 255)
		});

		panel.Append(new UIText(_record.WorldName, 1.05f, large: true) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(9),
			TextOriginX = 0f
		});

		WorldLiveState state = SharedWorldsLiveService.Get(_record.WorldId);
		panel.Append(new UIText(SharedWorldsLiveService.StatusLine(_record, state), 0.66f) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(48),
			TextOriginX = 0f,
			TextColor = new Color(140, 196, 210)
		});

		var body = new UIText("", 0.68f) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(96),
			Width = StyleDimension.FromPixels(panelWidth - 48),
			Height = StyleDimension.FromPixels(panelHeight - 226),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = new Color(188, 218, 230)
		};
		body.SetText(Describe(state));
		panel.Append(body);

		float firstRow = panelHeight - 112;
		panel.Append(SharedWorldsUIState.MakeButton("Sync now", 24, firstRow, 120, () => {
			_parent.BeginSync(_record);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}));
		panel.Append(SharedWorldsUIState.MakeButton("Manage", 156, firstRow, 120, () => {
			Main.MenuUI.SetState(new SharedWorldsUIState(
				onBack: () => {
					Main.MenuUI.SetState(_parent);
					Main.menuMode = 888;
				},
				onPlay: () => {
					Main.MenuUI.SetState(_parent);
					Main.menuMode = 888;
				}));
			Main.menuMode = 888;
		}));
		panel.Append(SharedWorldsUIState.MakeButton("Back", panelWidth - 112, firstRow, 88, () => {
			SoundEngine.PlaySound(SoundID.MenuClose);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}));
	}

	private string Describe(WorldLiveState state)
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"Latest saved snapshot: {FormatUtil.Timestamp(_record.LastUpdatedUtc)} ({FormatUtil.Relative(_record.LastUpdatedUtc)})");
		sb.AppendLine($"Last host: {WorkshopService.PersonaName(_record.LastHostSteamId)}");
		sb.AppendLine($"Workshop item: {(_record.WorkshopItemId == 0 ? "not created yet" : _record.WorkshopItemId.ToString())}");
		sb.AppendLine($"Participants: {_record.Participants.Count}");
		sb.AppendLine($"Upload: {(_record.PendingUpload ? (_record.PendingNeedsEula ? "waiting for the Workshop Legal Agreement" : "pending") : "up to date")}");

		if (state.Live != null) {
			sb.AppendLine();
			sb.AppendLine($"LIVE: {WorkshopService.PersonaName(state.Live.HostSteamId)} · {FormatUtil.PlayerCount(state.Live.Players)}");
			sb.AppendLine($"Live snapshot: {FormatUtil.Timestamp(state.Live.SnapshotUtc)}");
		}

		if (state.Report != null) {
			sb.AppendLine();
			sb.AppendLine("Last check: " + FormatUtil.Relative(state.CheckedUtc));
			sb.AppendLine(state.Report.Message);
		}

		sb.AppendLine();
		sb.AppendLine("Recent saves:");
		foreach (VersionRecord version in _record.History.OrderByDescending(v => v.TimestampUtc).Take(10))
			sb.AppendLine($"  {FormatUtil.Timestamp(version.TimestampUtc)}  ·  {WorkshopService.PersonaName(version.HostSteamId)}");

		if (_record.History.Count == 0)
			sb.AppendLine("  No snapshots recorded yet.");

		return sb.ToString();
	}
}
