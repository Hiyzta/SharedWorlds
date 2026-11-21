using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.UI;
using SharedWorlds.Core;

namespace SharedWorlds.UI;

/// <summary>
/// One-time Steam Workshop agreement notice, shown from the Play/Register/Import entry
/// points until Steam reports both agreements (Steam Subscriber Agreement and Workshop
/// Legal Agreement) as accepted on this machine. Continue skips for the moment; Don't
/// remind me again persists the choice in the mod config.
/// </summary>
internal static class SharedWorldsEulaGate
{
	internal const string WorkshopHubUrl = "https://steamcommunity.com/app/1281930/workshop/";

	private static WorkshopService.EulaState? _cached;
	private static DateTime _cachedUtc = DateTime.MinValue;
	private static Task<WorkshopService.EulaState>? _checkTask;
	private static bool _prewarmed;

	/// <summary>Starts the background status check once per session, while the title screen is up.</summary>
	public static void PrewarmOnce()
	{
		if (_prewarmed || !WorkshopService.IsClient || SharedWorldsConfig.Current.DontRemindWorkshopEula)
			return;

		_prewarmed = true;
		_ = Task.Run(() => GetStateAsync(TimeSpan.FromSeconds(10)));
	}

	/// <summary>Returns a fresh-enough status, or runs a new check with the given timeout.</summary>
	public static async Task<WorkshopService.EulaState> GetStateAsync(TimeSpan timeout)
	{
		Task<WorkshopService.EulaState>? running = _checkTask;
		if (running != null && !running.IsCompleted)
			return await running.ConfigureAwait(false);

		if (_cached != null && (DateTime.UtcNow - _cachedUtc).TotalSeconds < 30)
			return _cached.Value;

		Task<WorkshopService.EulaState> task = WorkshopService.CheckEulaAsync(timeout);
		_checkTask = task;
		WorkshopService.EulaState state = await task.ConfigureAwait(false);
		_cached = state;
		_cachedUtc = DateTime.UtcNow;
		_checkTask = null;
		return state;
	}

	/// <summary>
	/// Runs <paramref name="onContinue"/> unless Steam reports that the Workshop agreements
	/// still need accepting, in which case the notice is shown first and Continue re-runs it.
	/// </summary>
	public static void ShowIfNeeded(Action onContinue)
	{
		if (onContinue == null)
			return;

		// No Steam client, no agreements to accept; and the mod stays fully usable without them.
		if (!WorkshopService.IsClient || SharedWorldsConfig.Current.DontRemindWorkshopEula) {
			onContinue();
			return;
		}

		if (_cached == WorkshopService.EulaState.Accepted) {
			onContinue();
			return;
		}

		if (_cached == WorkshopService.EulaState.NeedsAction) {
			Show(onContinue);
			return;
		}

		// Unknown, error or not checked yet: verify quickly, then decide. This stays
		// invisible in the menu while the (usually instant) Steam answer arrives.
		_ = Task.Run(async () => {
			WorkshopService.EulaState state = await GetStateAsync(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
			if (state != WorkshopService.EulaState.NeedsAction) {
				Main.QueueMainThreadAction(onContinue);
				return;
			}

			Main.QueueMainThreadAction(() => Show(onContinue));
		});
	}

	private static void Show(Action onContinue)
	{
		if (Main.dedServ)
			return;

		SoundEngine.PlaySound(SoundID.MenuOpen);
		Main.MenuUI.SetState(new SharedWorldsEulaNoticeUIState(onContinue));
		Main.menuMode = 888;
	}
}

/// <summary>The vanilla-styled agreement notice itself.</summary>
internal sealed class SharedWorldsEulaNoticeUIState : UIState
{
	private readonly Action _onContinue;
	private UIText _status = null!;

	public SharedWorldsEulaNoticeUIState(Action onContinue)
	{
		_onContinue = onContinue;
	}

	public override void OnInitialize()
	{
		UIElement root = SwUI.MakeRoot(560f, 240f);
		Append(root);
		root.Append(SwUI.TabTitle("Steam Workshop agreements"));

		var container = SwUI.MakeContainer(110f);
		root.Append(container);

		var body = new UIText(
			"Shared Worlds stores world snapshots in your own friends-only Steam Workshop items.\n\n" +
			"Steam requires two one-time agreements for that: the Steam Subscriber Agreement and the Steam Workshop Legal Agreement. " +
			"Steam shows both the first time you publish anything, and accepting once covers every later upload.\n\n" +
			"Open the Workshop page below and start any upload once — the agreements appear there. " +
			"Until they are accepted, this mod's first snapshot upload stays pending and is retried after you accept.",
			0.8f) {
			Left = StyleDimension.FromPixels(10f),
			Top = StyleDimension.FromPixels(12f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			Height = StyleDimension.FromPixelsAndPercent(-124f, 1f),
			IsWrapped = true,
			TextOriginX = 0f,
			TextColor = Color.White
		};
		container.Append(body);

		_status = new UIText("Checking this account's agreement status...", 0.75f) {
			Left = StyleDimension.FromPixels(10f),
			VAlign = 1f,
			Top = StyleDimension.FromPixels(-56f),
			Width = StyleDimension.FromPixelsAndPercent(-20f, 1f),
			TextOriginX = 0f,
			TextColor = SwUI.Muted
		};
		container.Append(_status);

		var checkAgain = SwUI.Button("Check again", 124, CheckAgain);
		checkAgain.VAlign = 1f;
		checkAgain.Left = StyleDimension.FromPixels(4f);
		container.Append(checkAgain);

		var dontRemind = SwUI.Button("Don't remind me again", 210, DontRemind);
		dontRemind.VAlign = 1f;
		dontRemind.HAlign = 1f;
		dontRemind.Left = StyleDimension.FromPixels(-10f);
		container.Append(dontRemind);

		root.Append(SwUI.FatButton("Open the Steam page", OpenSteamPage, 0f));
		root.Append(SwUI.FatButton("Continue", Continue, 1f));
	}

	public override void OnActivate()
	{
		RefreshStatus();
	}

	private void RefreshStatus()
	{
		_status.SetText("Checking this account's agreement status...");
		_status.TextColor = SwUI.Muted;
		_ = Task.Run(async () => {
			WorkshopService.EulaState state = await SharedWorldsEulaGate.GetStateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			Main.QueueMainThreadAction(() => {
				switch (state) {
					case WorkshopService.EulaState.Accepted:
						_status.SetText("Both agreements are accepted on this account. Nothing more to do.");
						_status.TextColor = SwUI.Good;
						break;
					case WorkshopService.EulaState.NeedsAction:
						_status.SetText("Steam reports the Workshop Legal Agreement has not been accepted yet.");
						_status.TextColor = SwUI.Warn;
						break;
					case WorkshopService.EulaState.Error:
						_status.SetText("Could not read the agreement status from Steam right now.");
						_status.TextColor = SwUI.Warn;
						break;
					default:
						_status.SetText("Steam has not answered the status check yet.");
						_status.TextColor = SwUI.Muted;
						break;
				}
			});
		});
	}

	private void CheckAgain()
	{
		RefreshStatus();
	}

	private void OpenSteamPage()
	{
		try {
			Utils.OpenToURL(SharedWorldsEulaGate.WorkshopHubUrl);
			Log.Info("Opened the tModLoader Workshop page for the Steam agreements.");
		}
		catch (Exception e) {
			Log.Warn("Could not open the Workshop page.", e);
		}
	}

	private void DontRemind()
	{
		try {
			SharedWorldsConfig config = SharedWorldsConfig.Current;
			config.DontRemindWorkshopEula = true;
			config.SaveChanges(config, null, silent: true, broadcast: false);
			Log.Info("The Workshop agreement notice was suppressed in the mod config (DontRemindWorkshopEula).");
		}
		catch (Exception e) {
			Log.Warn("Could not save the 'don't remind' preference.", e);
		}

		Continue();
	}

	private void Continue()
	{
		Action onContinue = _onContinue;
		Main.QueueMainThreadAction(() => onContinue());
	}
}
