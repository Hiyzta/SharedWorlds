using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.UI;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// Shown when a client loses its host. One account at a time is offered the rehost action;
/// everyone else sees who is being asked. If a new lobby appears, clients follow it.
/// Framed like the rest of the mod's screens so recovery feels like part of the game.
/// </summary>
public sealed class SharedWorldsMigrationUIState : UIState
{
	private const float ContainerBottomGap = 110f;

	private UIText _body = null!;
	private UIText _status = null!;
	private UIText _detail = null!;
	private UITextPanel<string> _primary = null!;
	private UITextPanel<string> _decline = null!;

	private DateTime _lastPollUtc = DateTime.MinValue;
	private DateTime _foundUtc = DateTime.MinValue;
	private LiveWorldSession? _newHost;
	private LiveWorldSession? _rejoinHost;
	private string _handoffBy = "";
	private bool _preparing;
	private bool _declined;
	private bool _gaveUp;
	private string _message = "";

	public override void OnInitialize()
	{
		UIElement root = SwUI.MakeRoot(620f);
		Append(root);
		root.Append(SwUI.TabTitle("Host Disconnected"));

		var container = SwUI.MakeContainer(ContainerBottomGap);
		root.Append(container);

		_body = new UIText("", 0.85f) {
			Left = StyleDimension.FromPixels(10f),
			Top = StyleDimension.FromPixels(10f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixels(64f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = Color.White
		};
		container.Append(_body);

		_status = new UIText("", 0.85f) {
			Left = StyleDimension.FromPixels(10f),
			Top = StyleDimension.FromPixels(84f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixels(46f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = SwUI.Warn
		};
		container.Append(_status);

		_detail = new UIText("", 0.75f) {
			Left = StyleDimension.FromPixels(10f),
			Top = StyleDimension.FromPixels(140f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-192f, 1f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = SwUI.Muted
		};
		container.Append(_detail);

		_decline = SwUI.Button("Not now", 120, DeclineClicked);
		_decline.VAlign = 1f;
		_decline.Left = StyleDimension.FromPixels(4f);
		container.Append(_decline);

		root.Append(SwUI.FatButton("Back to menu", BackClicked, 0f));

		_primary = SwUI.FatButton("Waiting...", PrimaryClicked, 1f);
		_primary.SetText("Waiting...");
		root.Append(_primary);

		Refresh();
	}

	public override void Update(GameTime gameTime)
	{
		base.Update(gameTime);

		if (_gaveUp)
			return;

		SharedWorldsMigrationContext? context = SharedWorldsMigration.Current;
		if (context == null) {
			Main.MenuUI.SetState(null);
			Main.menuMode = 0;
			return;
		}

		if ((DateTime.UtcNow - _lastPollUtc).TotalSeconds >= 2) {
			_lastPollUtc = DateTime.UtcNow;
			Poll(context);
		}

		if (_newHost != null) {
			if (_foundUtc == default)
				_foundUtc = DateTime.UtcNow;

			if ((DateTime.UtcNow - _foundUtc).TotalMilliseconds > 2500)
				JoinNewHost(context);
			else
				Refresh();
			return;
		}

		// The big corner button is always visible; it lights up when it can be used.
		if (_rejoinHost != null) {
			SetPrimary("Rejoin now", enabled: true);
			_decline.SetText("Wait");
			_decline.IgnoresMouseInteraction = true;
		}
		else if (!string.IsNullOrEmpty(_handoffBy)) {
			SetPrimary("Handoff in progress...", enabled: false);
			_decline.SetText("Not now");
			_decline.IgnoresMouseInteraction = true;
		}
		else if (_preparing) {
			SetPrimary("Preparing...", enabled: false);
			_decline.SetText("Not now");
			_decline.IgnoresMouseInteraction = true;
		}
		else if (context.IsSelfTurn && !_declined) {
			SetPrimary("Rehost now", enabled: true);
			_decline.SetText("Not now");
			_decline.IgnoresMouseInteraction = false;
		}
		else {
			SetPrimary(_declined ? "Waiting for others..." : "Waiting...", enabled: false);
			_decline.SetText("Not now");
			_decline.IgnoresMouseInteraction = true;
		}

		if (context.IsExhausted && string.IsNullOrEmpty(_handoffBy) && _newHost == null && _rejoinHost == null && !_preparing) {
			GiveUp("Original host left — the server closed. Fetching the latest snapshot...");
			return;
		}

		RefreshText(context);
	}

	private void SetPrimary(string text, bool enabled)
	{
		if (_primary.Text != text)
			_primary.SetText(text);

		_primary.IgnoresMouseInteraction = !enabled;
		if (enabled) {
			_primary.BackgroundColor = SwUI.ButtonBg;
			_primary.BorderColor = SwUI.ButtonBorder;
		}
		else {
			_primary.BackgroundColor = SwUI.ItemBg;
			_primary.BorderColor = SwUI.ItemBorder;
		}
	}

	private void Refresh()
	{
		SharedWorldsMigrationContext? context = SharedWorldsMigration.Current;
		if (context != null)
			RefreshText(context);
	}

	private void Poll(SharedWorldsMigrationContext context)
	{
		// A new lobby for the same world is the signal every client follows.
		foreach (ulong id in context.Candidates.Concat(new[] { context.Session.HostSteamId }).Distinct()) {
			LiveWorldSession? live = SharedWorldsPresence.Read(id, context.Record.WorldId, forceRefresh: true);
			if (live == null)
				continue;

			if (live.LobbyId != context.Session.LobbyId) {
				_newHost = live;
				_message = $"New host found: {WorkshopService.PersonaName(live.HostSteamId)}. Joining...";
				return;
			}

			if (context.ElapsedSeconds > 8 && _rejoinHost == null) {
				// The host may simply have been unreachable from this client. Offer a way
				// back into the original lobby rather than starting a second server.
				_rejoinHost = live;
				_message = "The original host is still online. You can rejoin the existing session.";
			}
		}

		string handoff = "";
		foreach (ulong id in context.Candidates) {
			if (id == WorkshopService.CurrentSteamId)
				continue;

			if (SharedWorldsPresence.HasHandoff(id, context.Record.WorldId)) {
				handoff = WorkshopService.PersonaName(id);
				break;
			}
		}

		if (handoff != _handoffBy) {
			_handoffBy = handoff;
			if (handoff.Length > 0)
				_message = $"{handoff} is taking over the world...";
			else
				_message = "";
		}
	}

	private void RefreshText(SharedWorldsMigrationContext context)
	{
		_body.SetText($"The host of '{context.Record.WorldName}' left the game. The group is choosing a new host — you will rejoin automatically once one is running.");

		Color statusColor = SwUI.Warn;
		string statusText;

		if (_message.Length > 0) {
			statusText = _message;
			if (_message.StartsWith("New host found", StringComparison.Ordinal) || _message.StartsWith("The original host is still online", StringComparison.Ordinal))
				statusColor = SwUI.Live;
		}
		else if (_preparing) {
			statusText = "Preparing the latest snapshot and starting Host & Play...";
			statusColor = SwUI.Bright;
		}
		else if (_rejoinHost != null) {
			statusText = "The original host is still online. Rejoin the existing session?";
			statusColor = SwUI.Live;
		}
		else if (_declined) {
			ulong? next = context.CurrentCandidate;
			statusText = next == null || next == WorkshopService.CurrentSteamId
				? "Not this time. Waiting for another player..."
				: $"Not this time. Waiting for {WorkshopService.PersonaName(next.Value)}...";
		}
		else if (context.IsSelfTurn) {
			statusText = $"It is your turn — rehost '{context.Record.WorldName}' now?";
			statusColor = SwUI.Live;
		}
		else {
			ulong? candidate = context.CurrentCandidate;
			if (candidate == null) {
				statusText = "Waiting for a new host...";
			}
			else {
				double until = SharedWorldsMigrationContext.StartDelaySeconds + (context.CurrentSlot + 1) * SharedWorldsMigrationContext.SlotSeconds - context.ElapsedSeconds;
				statusText = $"Waiting for {WorkshopService.PersonaName(candidate.Value)} to rehost... next offer in {Math.Max(0, (int)Math.Ceiling(until))}s.";
			}
		}

		_status.SetText(statusText);
		_status.TextColor = statusColor;
		_detail.SetText(CandidateText(context));
	}

	private static string CandidateText(SharedWorldsMigrationContext context)
	{
		string own = WorkshopService.PersonaName(WorkshopService.CurrentSteamId);
		var names = context.Candidates.Select((id, index) => {
			string marker = id == WorkshopService.CurrentSteamId ? "  (you)" : "";
			return $"  {index + 1}. {WorkshopService.PersonaName(id)}{marker}";
		});
		return "Recovery order:\n" + string.Join("\n", names.Take(8)) + (context.Candidates.Count > 8 ? "\n  ..." : "") +
			$"\n\nYour account: {own}";
	}

	private void PrimaryClicked()
	{
		SharedWorldsMigrationContext? context = SharedWorldsMigration.Current;
		if (context == null || _preparing)
			return;

		if (_rejoinHost != null) {
			Join(_rejoinHost, context);
			return;
		}

		if (!context.IsSelfTurn || _declined || _handoffBy.Length > 0)
			return;

		_preparing = true;
		_message = "Preparing the latest snapshot...";
		SharedWorldsPresence.SetHandoff(context.Record.WorldId);
		RefreshText(context);

		_ = Task.Run(async () => {
			(bool success, string message) = await SharedWorldsLiveService.PrepareHostAsync(context.Record).ConfigureAwait(false);
			Main.QueueMainThreadAction(() => {
				if (_gaveUp || !ReferenceEquals(SharedWorldsMigration.Current, context)) {
					SharedWorldsPresence.ClearHandoff();
					return;
				}

				_preparing = false;
				if (!success) {
					SharedWorldsPresence.ClearHandoff();
					_declined = true;
					_message = "Could not rehost: " + message;
					Refresh();
					return;
				}

				SharedWorldRecord? fresh = SharedWorldsService.Instance.Registry.FindByWorldId(context.Record.WorldId) ?? context.Record;
				Main.MenuUI.SetState(null);
				Main.menuMode = 0;
				bool started = HostAndPlay.Start(fresh, out string error);
				if (started || HostAndPlay.IsWaitingForCharacter(fresh.WorldId)) {
					SharedWorldsMigration.Clear();
					return;
				}

				SharedWorldsPresence.ClearHandoff();
				Main.MenuUI.SetState(new SharedWorldsMigrationUIState());
				Main.menuMode = 888;
				Log.Warn("Rehost did not start: " + error);
			});
		});
	}

	private void DeclineClicked()
	{
		SharedWorldsMigrationContext? context = SharedWorldsMigration.Current;
		if (context == null || !context.IsSelfTurn)
			return;

		_declined = true;
		_message = "";
		RefreshText(context);
	}

	private void BackClicked()
	{
		GiveUp("Original host left — the server closed. Fetching the latest snapshot...");
	}

	private void JoinNewHost(SharedWorldsMigrationContext context)
	{
		if (_newHost == null)
			return;

		Join(_newHost, context);
	}

	private void Join(LiveWorldSession live, SharedWorldsMigrationContext context)
	{
		_gaveUp = true;
		if (!SharedWorldsUI.JoinLobby(live, out string error)) {
			_gaveUp = false;
			_newHost = null;
			_rejoinHost = null;
			_message = error;
			Refresh();
			return;
		}

		SharedWorldsMigration.Clear();
	}

	private void GiveUp(string message)
	{
		if (_gaveUp)
			return;

		_gaveUp = true;
		SharedWorldsPresence.ClearHandoff();
		SharedWorldsMigration.GiveUp(message);
		Main.MenuUI.SetState(null);
		Main.menuMode = 0;
	}
}
