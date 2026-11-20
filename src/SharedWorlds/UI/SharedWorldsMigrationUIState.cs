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
/// </summary>
public sealed class SharedWorldsMigrationUIState : UIState
{
	private UIText _title = null!;
	private UIText _body = null!;
	private UIText _detail = null!;
	private UITextPanel<string> _primary = null!;
	private UITextPanel<string> _decline = null!;
	private UITextPanel<string> _back = null!;

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
		float panelWidth = Math.Clamp(Main.screenWidth - 120, 680, 860);
		float panelHeight = Math.Clamp(Main.screenHeight - 120, 420, 520);

		var panel = new UIPanel {
			Width = StyleDimension.FromPixels(panelWidth),
			Height = StyleDimension.FromPixels(panelHeight),
			HAlign = 0.5f,
			VAlign = 0.5f,
			BackgroundColor = new Color(11, 21, 33, 250),
			BorderColor = new Color(64, 96, 118, 255)
		};
		Append(panel);

		var header = new UIPanel {
			Width = StyleDimension.FromPixelsAndPercent(0, 1f),
			Height = StyleDimension.FromPixels(92),
			BackgroundColor = new Color(38, 48, 62, 255),
			BorderColor = new Color(64, 96, 118, 255)
		};
		panel.Append(header);

		_title = new UIText("Shared world session ended", 1.05f, large: true) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(12),
			TextOriginX = 0f
		};
		panel.Append(_title);

		_body = new UIText("", 0.76f) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(52),
			Width = StyleDimension.FromPixels(panelWidth - 48),
			Height = StyleDimension.FromPixels(52),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = new Color(194, 220, 232)
		};
		panel.Append(_body);

		_detail = new UIText("", 0.66f) {
			Left = StyleDimension.FromPixels(24),
			Top = StyleDimension.FromPixels(116),
			Width = StyleDimension.FromPixels(panelWidth - 48),
			Height = StyleDimension.FromPixels(panelHeight - 220),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = new Color(140, 184, 198)
		};
		panel.Append(_detail);

		float buttonTop = panelHeight - 68;
		_primary = SharedWorldsUIState.MakeButton("Rehost now", 24, buttonTop, 166, PrimaryClicked);
		_decline = SharedWorldsUIState.MakeButton("Not now", 202, buttonTop, 124, DeclineClicked);
		_back = SharedWorldsUIState.MakeButton("Back", panelWidth - 164, buttonTop, 140, BackClicked);
		panel.Append(_primary);
		panel.Append(_decline);
		panel.Append(_back);

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

		if (string.IsNullOrEmpty(_handoffBy) && !_preparing && !_declined && context.IsSelfTurn) {
			_primary.SetText("Rehost now");
			_primary.IgnoresMouseInteraction = false;
			_primary.BackgroundColor = new Color(28, 108, 86, 245);
			_primary.BorderColor = new Color(96, 220, 170, 240);

			_decline.SetText("Not now");
			_decline.IgnoresMouseInteraction = false;
			_decline.BackgroundColor = new Color(30, 48, 62, 240);
			_decline.BorderColor = new Color(92, 132, 150, 220);
		}
		else if (_rejoinHost != null) {
			_primary.SetText("Rejoin now");
			_primary.IgnoresMouseInteraction = false;
			_primary.BackgroundColor = new Color(30, 78, 94, 245);
			_primary.BorderColor = new Color(82, 174, 192, 235);

			_decline.SetText("Wait");
			_decline.IgnoresMouseInteraction = true;
			_decline.BackgroundColor = new Color(24, 36, 48, 235);
			_decline.BorderColor = new Color(62, 88, 102, 220);
		}
		else {
			_primary.SetText(_preparing ? "Preparing..." : _handoffBy.Length > 0 ? "Handoff..." : "Waiting...");
			_primary.IgnoresMouseInteraction = true;
			_primary.BackgroundColor = new Color(24, 38, 50, 235);
			_primary.BorderColor = new Color(62, 88, 102, 220);

			_decline.SetText("Not now");
			_decline.IgnoresMouseInteraction = true;
			_decline.BackgroundColor = new Color(24, 36, 48, 235);
			_decline.BorderColor = new Color(62, 88, 102, 220);
		}

		if (context.IsExhausted && string.IsNullOrEmpty(_handoffBy) && _newHost == null && _rejoinHost == null && !_preparing) {
			GiveUp("Original host left — the server closed. Fetching the latest snapshot...");
			return;
		}

		RefreshText(context);
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
		_title.SetText("Shared world session ended");
		_body.SetText($"The original host left '{context.Record.WorldName}'. The world is closed for a moment while a new host is chosen.");

		if (_message.Length > 0) {
			_detail.SetText(_message + "\n\n" + CandidateText(context));
			return;
		}

		if (_rejoinHost != null) {
			_detail.SetText(_message + "\n\n" + CandidateText(context));
			return;
		}

		if (_preparing) {
			_detail.SetText("Preparing the latest snapshot and starting Host & Play...");
			return;
		}

		if (_declined) {
			ulong? next = context.CurrentCandidate;
			_detail.SetText(next == null || next == WorkshopService.CurrentSteamId
				? "Not this time. Waiting for another player..."
				: $"Not this time. Waiting for {WorkshopService.PersonaName(next.Value)}...");
			return;
		}

		if (context.IsSelfTurn) {
			_detail.SetText(
				"You are the next host in the recovery order.\n\n" +
				$"Rehost '{context.Record.WorldName}' now?\n\n" +
				CandidateText(context));
			return;
		}

		ulong? candidate = context.CurrentCandidate;
		if (candidate == null) {
			_detail.SetText(CandidateText(context));
			return;
		}

		double until = SharedWorldsMigrationContext.StartDelaySeconds + (context.CurrentSlot + 1) * SharedWorldsMigrationContext.SlotSeconds - context.ElapsedSeconds;
		_detail.SetText(
			$"Waiting for {WorkshopService.PersonaName(candidate.Value)} to rehost...\n" +
			$"Next offer in {Math.Max(0, (int)Math.Ceiling(until))}s.\n\n" +
			CandidateText(context));
	}

	private static string CandidateText(SharedWorldsMigrationContext context)
	{
		string own = WorkshopService.PersonaName(WorkshopService.CurrentSteamId);
		var names = context.Candidates.Select((id, index) => {
			string marker = id == WorkshopService.CurrentSteamId ? " (you)" : "";
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
		SoundEngine.PlaySound(SoundID.MenuTick);
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
		SoundEngine.PlaySound(SoundID.MenuTick);
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
