using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.UI;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// The clean Play screen reached from the real "Play Shared Worlds" multiplayer-menu row.
/// The frame, palette and card artwork mirror Terraria's own world select so the screen
/// feels like part of the game: a title tab, a scrollable list of world cards and the two
/// large corner buttons. Each card shows what can be done right now — join a live host,
/// host the latest saved snapshot, or fix a sync problem.
/// </summary>
public sealed class SharedWorldsPlayUIState : UIState
{
	private const float ContainerBottomGap = 110f;
	private const float ToolbarHeight = 46f;

	private readonly int _returnMenuMode;
	private readonly string? _initialStatus;
	private readonly string? _focusWorldId;
	private readonly string? _autoSyncWorldId;
	private readonly List<PlayWorldCard> _cards = new();

	private UIPanel _container = null!;
	private UIList _list = null!;
	private UIScrollbar _scrollbar = null!;
	private bool _scrollbarAttached;
	private UIText _summary = null!;
	private UIText _status = null!;
	private Color _statusColor = SwUI.Muted;
	private bool _busy;
	private string _statusText = "";
	private Task? _refreshTask;
	private DateTime _lastLivePollUtc = DateTime.MinValue;
	private DateTime _lastRefreshUtc;

	private static readonly string IdleHint = "Join a live world, or host the latest saved snapshot.";

	public SharedWorldsPlayUIState(int returnMenuMode = 12, string? status = null, string? focusWorldId = null, string? autoSyncWorldId = null)
	{
		_returnMenuMode = returnMenuMode;
		_initialStatus = status;
		_focusWorldId = focusWorldId;
		_autoSyncWorldId = autoSyncWorldId;
	}

	public override void OnInitialize()
	{
		UIElement root = SwUI.MakeRoot();
		Append(root);
		root.Append(SwUI.TabTitle("Play Shared Worlds"));

		_container = SwUI.MakeContainer(ContainerBottomGap);
		root.Append(_container);

		// Toolbar: a live summary on the left, refresh on the right.
		_summary = new UIText("", 0.75f) {
			Left = StyleDimension.FromPixels(6f),
			VAlign = 0f,
			Top = StyleDimension.FromPixels(10f),
			Width = StyleDimension.FromPixelsAndPercent(-140f, 1f),
			Height = StyleDimension.FromPixels(24f),
			TextOriginX = 0f,
			TextColor = SwUI.Muted
		};
		_container.Append(_summary);

		var refresh = SwUI.Button("Refresh", 124, RefreshAll);
		refresh.HAlign = 1f;
		refresh.Top = StyleDimension.FromPixels(5f);
		refresh.Left = StyleDimension.FromPixels(-6f);
		_container.Append(refresh);

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

		// Status strip in the footer zone vanilla leaves empty below the corner buttons.
		_status = new UIText("", 0.75f) {
			Width = StyleDimension.FromPercent(1f),
			VAlign = 1f,
			Height = StyleDimension.FromPixels(40f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = _statusColor
		};
		root.Append(_status);

		root.Append(SwUI.FatButton("Back", GoBack, 0f));
		root.Append(SwUI.FatButton("Manage", OpenManage, 1f));
	}

	public override void Recalculate()
	{
		// Vanilla behavior: the scrollbar only exists when the list can actually scroll.
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
		SharedWorldsPresence.Invalidate();
		SetStatus(_initialStatus ?? "");
		if (string.IsNullOrWhiteSpace(_statusText))
			SetStatus(IdleHint, SwUI.Bright);

		RefreshList();

		if (!string.IsNullOrWhiteSpace(_autoSyncWorldId)) {
			SharedWorldRecord? record = SharedWorldsService.Instance.Registry.FindByWorldId(_autoSyncWorldId);
			if (record != null) {
				SetStatus("The original host left. Fetching the latest snapshot...", SwUI.Bright);
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
			bool wasBusyMessage = _statusText.StartsWith("Checking", StringComparison.Ordinal);
			_refreshTask = null;
			_busy = false;
			_lastRefreshUtc = DateTime.UtcNow;
			if (wasBusyMessage)
				SetStatus(IdleHint, SwUI.Bright);
			RefreshList();
		}

		UpdateSummary();
		_status.SetText(_statusText);
		_status.TextColor = _statusColor;
	}

	private void UpdateSummary()
	{
		int total = _cards.Count;
		if (total == 0) {
			_summary?.SetText("No shared worlds yet");
			return;
		}

		int live = _cards.Count(c => c.IsLive);
		string worlds = total == 1 ? "1 shared world" : $"{total} shared worlds";
		string liveText = live == 0 ? "none live" : live == 1 ? "1 live now" : $"{live} live now";
		string checkedText = _lastRefreshUtc == default
			? "checking…"
			: "checked " + FormatUtil.Relative(_lastRefreshUtc);

		_summary?.SetText($"{worlds} · {liveText} · {checkedText}");
	}

	public void SetStatus(string text)
		=> SetStatus(text, SwUI.Bright);

	public void SetStatus(string text, Color color)
	{
		_statusText = text ?? "";
		_statusColor = color;
		_status?.SetText(_statusText);
		if (_status != null)
			_status.TextColor = _statusColor;
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
			if (existing.TryGetValue(record.WorldId, out PlayWorldCard? card))
				card.UpdateRecord(record);
			else
				card = new PlayWorldCard(record, this);

			next.Add(card);
			_list.Add(card);
		}

		_cards.Clear();
		_cards.AddRange(next);

		if (_cards.Count == 0)
			_list.Add(BuildEmptyState());

		_list.Recalculate();
		if (!string.IsNullOrEmpty(_focusWorldId))
			_list.ViewPosition = 0f;
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

		empty.Append(new UIText("Register a world you own, or import an invite code from a friend.\nShared worlds appear here, ready to join or host.", 0.75f) {
			HAlign = 0.5f,
			Top = StyleDimension.FromPixels(74f),
			Width = StyleDimension.FromPixelsAndPercent(-30f, 1f),
			IsWrapped = true,
			TextColor = SwUI.Muted
		});

		var manage = SwUI.Button("Manage shared worlds", 210, OpenManage);
		manage.HAlign = 0.5f;
		manage.VAlign = 1f;
		manage.Top = StyleDimension.FromPixels(-14f);
		empty.Append(manage);

		return empty;
	}

	public void RefreshAll()
	{
		if (_busy)
			return;

		_busy = true;
		SetStatus("Checking the latest snapshots and live sessions...", SwUI.Bright);

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
			SetStatus("That world is not online right now. Host it, or refresh again in a moment.", SwUI.Warn);
			return;
		}

		SetStatus($"Joining '{live.WorldName}' through Steam...", SwUI.Bright);
		if (!SharedWorldsUI.JoinLobby(live, out string error))
			SetStatus(error, SwUI.Warn);
	}

	public void BeginHost(SharedWorldRecord record)
	{
		if (_busy)
			return;

		LiveWorldSession? live = SharedWorldsLiveService.FindLive(record);
		if (live != null) {
			SetStatus($"{WorkshopService.PersonaName(live.HostSteamId)} is already hosting this world. Join instead of starting a second host.", SwUI.Warn);
			return;
		}

		_busy = true;
		SetStatus($"Checking and preparing '{record.WorldName}'...", SwUI.Bright);
		_ = Task.Run(async () => {
			(bool success, string message) = await SharedWorldsLiveService.PrepareHostAsync(record).ConfigureAwait(false);
			Main.QueueMainThreadAction(() => {
				_busy = false;
				if (!success) {
					SetStatus(message, SwUI.Warn);
					RefreshList();
					return;
				}

				SharedWorldRecord? fresh = SharedWorldsService.Instance.Registry.FindByWorldId(record.WorldId) ?? record;
				bool started = HostAndPlay.Start(fresh, out string error);
				if (!started && HostAndPlay.IsWaitingForCharacter(fresh.WorldId))
					return;

				if (!started) {
					SetStatus(error, SwUI.Warn);
					return;
				}

				SetStatus("Starting Host & Play...", SwUI.Good);
			});
		});
	}

	public void BeginSync(SharedWorldRecord record)
	{
		if (_busy)
			return;

		_busy = true;
		SetStatus($"Downloading the newest snapshot of '{record.WorldName}'...", SwUI.Bright);
		_ = Task.Run(async () => {
			WorldLiveState state = await SharedWorldsLiveService.CheckAsync(record, downloadIfNewer: true).ConfigureAwait(false);
			Main.QueueMainThreadAction(() => {
				_busy = false;
				_lastRefreshUtc = DateTime.UtcNow;
				if (state.Report == null) {
					SetStatus(state.Error.Length > 0 ? state.Error : "Sync finished.", state.Error.Length > 0 ? SwUI.Warn : SwUI.Good);
				}
				else {
					Color color = state.Report.Status is SyncStatus.Fork or SyncStatus.Error ? SwUI.Warn : SwUI.Good;
					SetStatus(FormatReport(state.Report), color);
				}

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
		Main.MenuUI.SetState(new SharedWorldPlayDetailUIState(record, this));
		Main.menuMode = 888;
	}

	private void OpenManage()
	{
		SoundEngine.PlaySound(SoundID.MenuOpen);
		Main.MenuUI.SetState(new SharedWorldsUIState(onBack: () => {
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

/// <summary>
/// One shared world on the Play screen, drawn the way vanilla draws a world row: name and
/// divider, a row of inner-panel tags for live state / save time / sync state, and action
/// buttons along the bottom edge.
/// </summary>
internal sealed class PlayWorldCard : UIPanel
{
	private const float CardHeight = 104f;

	private readonly SharedWorldsPlayUIState _owner;
	private SharedWorldRecord _record;

	private readonly UITextPanel<string> _primary;
	private readonly UITextPanel<string> _details;
	private readonly UIText _hostInfo;
	private bool _dimmed;

	public string WorldId => _record.WorldId;
	public bool IsLive => SharedWorldsLiveService.Get(_record.WorldId).Live != null;

	public PlayWorldCard(SharedWorldRecord record, SharedWorldsPlayUIState owner)
	{
		_record = record;
		_owner = owner;

		Width = StyleDimension.FromPercent(1f);
		Height = StyleDimension.FromPixels(CardHeight);
		SetPadding(6f);

		_primary = SwUI.Button("Host", 124, () => PrimaryClicked());
		_primary.VAlign = 1f;
		_primary.Left = StyleDimension.FromPixels(4f);
		Append(_primary);

		_details = SwUI.Button("Details", 96, () => {
			_owner.OpenDetails(_record);
		});
		_details.VAlign = 1f;
		_details.Left = StyleDimension.FromPixels(134f);
		Append(_details);

		_hostInfo = new UIText("", 0.7f) {
			HAlign = 1f,
			VAlign = 1f,
			Top = StyleDimension.FromPixels(-10f),
			Width = StyleDimension.FromPixels(250f),
			TextOriginX = 1f,
			TextColor = SwUI.Muted
		};
		Append(_hostInfo);

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
		SwUI.SetItemColors(this, hovered: false, dimmed: _dimmed);
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

		LiveWorldSession? live = state.Live;
		bool isSelf = live != null && live.HostSteamId == WorkshopService.CurrentSteamId;
		bool hasForkOrError = state.Report?.Status is SyncStatus.Fork or SyncStatus.Error;
		bool noLocal = _record.LocalVersion == 0 && string.IsNullOrEmpty(_record.LocalHash) &&
			!File.Exists(SharedWorldPaths.WorldPath(_record));

		_dimmed = hasForkOrError;

		if (live != null) {
			_primary.SetText(isSelf ? "Hosting" : "Join");
			_primary.IgnoresMouseInteraction = isSelf;
			SwUI.SetItemColors(_primary, hovered: false, dimmed: isSelf);
			_details.SetText("Details");
		}
		else if (hasForkOrError) {
			_primary.SetText("Resolve");
			_primary.IgnoresMouseInteraction = false;
			SwUI.SetItemColors(_primary, hovered: false);
		}
		else if (noLocal) {
			_primary.SetText("Sync");
			_primary.IgnoresMouseInteraction = false;
			SwUI.SetItemColors(_primary, hovered: false);
		}
		else {
			_primary.SetText("Host");
			_primary.IgnoresMouseInteraction = false;
			SwUI.SetItemColors(_primary, hovered: false);
		}

		string host = WorkshopService.PersonaName(_record.LastHostSteamId);
		string accounts = _record.Participants.Count == 0 ? "no other accounts" : $"{_record.Participants.Count} participant account(s)";
		_hostInfo.SetText($"Last host: {host} · {accounts}");
	}

	protected override void DrawSelf(SpriteBatch sb)
	{
		base.DrawSelf(sb);

		WorldLiveState state = SharedWorldsLiveService.Get(_record.WorldId);
		SharedWorldRecord? fresh = SharedWorldsService.Instance.Registry.FindByWorldId(_record.WorldId);
		if (fresh != null)
			_record = fresh;

		CalculatedStyle inner = GetInnerDimensions();

		// Name row.
		bool liveOnline = state.Live != null;
		Color nameColor = liveOnline ? Color.White : (Color.Lerp(Color.White, Color.Gray, _dimmed ? 0.45f : 0f));
		string name = SwUI.CropToFit(_record.WorldName, inner.Width - 20f);
		Utils.DrawBorderString(sb, name, new Vector2(inner.X, inner.Y - 2f), nameColor);

		SwUI.DrawDivider(sb, new Vector2(inner.X, inner.Y + 24f), inner.Width);

		// Tag row: live state, save time, sync state.
		Vector2 tagPos = new(inner.X, inner.Y + 32f);
		float stateWidth = 200f;
		float savedWidth = 150f;
		float syncWidth = Math.Max(180f, inner.Width - stateWidth - savedWidth - 10f);

		string stateText;
		Color stateColor;
		if (liveOnline) {
			LiveWorldSession live = state.Live!;
			bool isSelf = live.HostSteamId == WorkshopService.CurrentSteamId;
			string players = live.Players == 1 ? "1 player" : $"{live.Players} players";
			stateText = isSelf
				? $"LIVE · you are hosting · {players}"
				: $"LIVE · {SwUI.CropToFit(WorkshopService.PersonaName(live.HostSteamId), 84f)} · {players}";
			stateColor = SwUI.Live;
		}
		else {
			stateText = "OFFLINE";
			stateColor = SwUI.Muted;
		}

		string savedText = _record.LastUpdatedUtc == default
			? "Never saved"
			: "Saved " + FormatUtil.Relative(_record.LastUpdatedUtc);
		Color savedColor = _record.LastUpdatedUtc == default ? SwUI.Muted : Color.White;

		(string text, Color color) sync = GetSyncTag(state);
		SwUI.DrawTag(sb, tagPos, stateWidth, stateText, stateColor);
		SwUI.DrawTag(sb, new Vector2(tagPos.X + stateWidth + 5f, tagPos.Y), savedWidth, savedText, savedColor);
		SwUI.DrawTag(sb, new Vector2(tagPos.X + stateWidth + savedWidth + 10f, tagPos.Y), syncWidth, sync.text, sync.color);
	}

	private (string text, Color color) GetSyncTag(WorldLiveState state)
	{
		if (state.Report != null) {
			switch (state.Report.Status) {
				case SyncStatus.UpToDate:
					return ("Up to date", SwUI.Good);
				case SyncStatus.RemoteNewer:
					return ("A newer snapshot is available", SwUI.Warn);
				case SyncStatus.LocalNewer:
					return ("This account has the newest snapshot", Color.White);
				case SyncStatus.LocalOnly:
					return ("Local snapshot only; no Workshop item yet", SwUI.Muted);
				case SyncStatus.Fork:
					return ("Fork — two branches exist, resolve in Manage", SwUI.Warn);
				case SyncStatus.WorkshopUnavailable:
					return ("Workshop unavailable", SwUI.Warn);
				case SyncStatus.Error:
					return ("Sync error — open Details", SwUI.Warn);
			}
		}

		if (_record.PendingNeedsEula)
			return ("Upload waiting for the Workshop Legal Agreement", SwUI.Warn);
		if (_record.PendingUpload)
			return ("A newer local snapshot is waiting to upload", SwUI.Warn);
		if (_record.WorkshopItemId == 0)
			return ("No Workshop item yet", SwUI.Muted);
		if (_record.LocalVersion == 0 && string.IsNullOrEmpty(_record.LocalHash))
			return ("No local snapshot yet — sync first", SwUI.Muted);

		return ("Workshop item ready", SwUI.Good);
	}

	private void PrimaryClicked()
	{
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

/// <summary>The per-world details view from the Play screen.</summary>
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
		UIElement root = SwUI.MakeRoot(670f);
		Append(root);
		root.Append(SwUI.TabTitle(SwUI.CropToFit(_record.WorldName, 260f)));

		var container = SwUI.MakeContainer(110f);
		root.Append(container);

		WorldLiveState state = SharedWorldsLiveService.Get(_record.WorldId);

		var statusLine = new UIText(SharedWorldsLiveService.StatusLine(_record, state), 0.85f) {
			Left = StyleDimension.FromPixels(10f),
			Top = StyleDimension.FromPixels(10f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			TextOriginX = 0f,
			TextColor = state.Live != null ? SwUI.Live : SwUI.Bright
		};
		container.Append(statusLine);

		var body = new UIText(Describe(state), 0.8f) {
			Left = StyleDimension.FromPixels(10f),
			Top = StyleDimension.FromPixels(46f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-56f, 1f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = Color.White
		};
		container.Append(body);

		root.Append(SwUI.FatButton("Back", () => {
			SoundEngine.PlaySound(SoundID.MenuClose);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}, 0f));

		root.Append(SwUI.FatButton("Sync now", () => {
			_parent.BeginSync(_record);
			Main.MenuUI.SetState(_parent);
			Main.menuMode = 888;
		}, 1f));
	}

	private string Describe(WorldLiveState state)
	{
		var sb = new System.Text.StringBuilder();

		sb.AppendLine($"Latest saved snapshot: {(_record.LastUpdatedUtc == default ? "never" : FormatUtil.Timestamp(_record.LastUpdatedUtc))}" +
			(_record.LastUpdatedUtc == default ? "" : $" ({FormatUtil.Relative(_record.LastUpdatedUtc)})"));
		sb.AppendLine($"Last host: {WorkshopService.PersonaName(_record.LastHostSteamId)}");
		sb.AppendLine($"Workshop item: {(_record.WorkshopItemId == 0 ? "not created yet" : _record.WorkshopItemId.ToString())}");
		sb.AppendLine($"Participants: {(_record.Participants.Count == 0 ? "only this account" : _record.Participants.Count + " account(s)")}");
		sb.AppendLine($"Upload: {(_record.PendingUpload ? (_record.PendingNeedsEula ? "waiting for the Workshop Legal Agreement" : "pending") : "up to date")}");

		if (state.Live != null) {
			sb.AppendLine();
			sb.AppendLine($"LIVE — {WorkshopService.PersonaName(state.Live.HostSteamId)} · {FormatUtil.PlayerCount(state.Live.Players)}");
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
