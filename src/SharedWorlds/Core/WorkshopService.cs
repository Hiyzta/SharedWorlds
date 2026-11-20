using System.Text.Json;
using Steamworks;
using Terraria.Social.Steam;

namespace SharedWorlds.Core;

/// <summary>
/// All Steam Workshop calls live here. Nothing else in the mod should call Steamworks directly.
/// The API is only usable when tModLoader initialized the Steam client (not GOG/offline).
/// </summary>
public static class WorkshopService
{
	public const uint TmlAppId = 1281930;
	private static readonly AppId_t AppId = new(TmlAppId);
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

	public static bool IsAvailable => SteamedWraps.SteamClient;
	public static bool IsSteamPresent => SteamedWraps.SteamClient;
	public static bool IsClient => SteamedWraps.SteamClient;

	/// <summary>
	/// Pumps Steam callbacks. tModLoader does this on the main thread during normal updates,
	/// but uploads started during world unload or server shutdown need a helper because the
	/// main thread is blocked waiting for the result.
	/// </summary>
	public static void PumpCallbacks()
	{
		if (!IsClient)
			return;

		try {
			SteamAPI.RunCallbacks();
		}
		catch (Exception e) {
			Log.Warn("Steam callback pump failed.", e);
		}
	}

	public static ulong CurrentSteamId
	{
		get {
			try {
				return IsClient ? SteamUser.GetSteamID().m_SteamID : 0;
			}
			catch {
				return 0;
			}
		}
	}

	public static string CurrentPersonaName
	{
		get {
			try {
				return IsClient ? SteamFriends.GetPersonaName() : "";
			}
			catch {
				return "";
			}
		}
	}

	public static string PersonaName(ulong steamId)
	{
		try {
			if (!IsClient || steamId == 0)
				return steamId == 0 ? "unknown" : steamId.ToString();

			if (steamId == CurrentSteamId)
				return SteamFriends.GetPersonaName();

			string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
			return string.IsNullOrWhiteSpace(name) ? steamId.ToString() : name;
		}
		catch {
			return steamId.ToString();
		}
	}

	public static bool IsFriend(ulong steamId)
	{
		try {
			if (!IsClient || steamId == 0)
				return false;

			if (steamId == CurrentSteamId)
				return true;

			return SteamFriends.GetFriendRelationship(new CSteamID(steamId)) == EFriendRelationship.k_EFriendRelationshipFriend;
		}
		catch {
			return false;
		}
	}

	public static string ItemUrl(ulong itemId) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={itemId}";

	public enum EulaState { Unknown, Accepted, NeedsAction, Error }

	public static async Task<EulaState> CheckEulaAsync(TimeSpan timeout)
	{
		if (!IsClient)
			return EulaState.Error;

		try {
			var tcs = new TaskCompletionSource<WorkshopEULAStatus_t>(TaskCreationOptions.RunContinuationsAsynchronously);
			using CallResult<WorkshopEULAStatus_t> call = CallResult<WorkshopEULAStatus_t>.Create((result, ioFailure) => {
				if (ioFailure) {
					tcs.TrySetException(new IOException("Steam IO failure while reading Workshop EULA status."));
					return;
				}

				tcs.TrySetResult(result);
			});

			SteamAPICall_t apiCall = SteamUGC.GetWorkshopEULAStatus();
			if (apiCall == SteamAPICall_t.Invalid)
				return EulaState.Unknown;

			call.Set(apiCall);
			WorkshopEULAStatus_t status = await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);

			if (status.m_eResult != EResult.k_EResultOK)
				return EulaState.Error;

			return status.m_bNeedsAction ? EulaState.NeedsAction : EulaState.Accepted;
		}
		catch (Exception e) {
			Log.Warn("Could not read Steam Workshop EULA status.", e);
			return EulaState.Error;
		}
	}

	public static void ShowEula()
	{
		try {
			if (IsClient)
				SteamUGC.ShowWorkshopEULA();
		}
		catch (Exception e) {
			Log.Warn("Could not open the Workshop EULA in the Steam overlay.", e);
		}
	}

	public static async Task<(bool Success, ulong ItemId, bool NeedsEula, string Message)> CreateItemAsync(TimeSpan timeout)
	{
		if (!IsClient)
			return (false, 0, false, "Steam client API is not available. Start tModLoader through Steam.");

		try {
			var tcs = new TaskCompletionSource<CreateItemResult_t>(TaskCreationOptions.RunContinuationsAsynchronously);
			using CallResult<CreateItemResult_t> call = CallResult<CreateItemResult_t>.Create((result, ioFailure) => {
				if (ioFailure) {
					tcs.TrySetException(new IOException("Steam IO failure while creating the Workshop item."));
					return;
				}

				tcs.TrySetResult(result);
			});

			SteamAPICall_t apiCall = SteamUGC.CreateItem(AppId, EWorkshopFileType.k_EWorkshopFileTypeCommunity);
			if (apiCall == SteamAPICall_t.Invalid)
				return (false, 0, false, "Steam rejected the Workshop item creation request.");

			call.Set(apiCall);
			CreateItemResult_t result = await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);

			if (result.m_eResult != EResult.k_EResultOK)
				return (false, 0, result.m_bUserNeedsToAcceptWorkshopLegalAgreement, "Could not create Workshop item: " + result.m_eResult);

			return (true, result.m_nPublishedFileId.m_PublishedFileId, result.m_bUserNeedsToAcceptWorkshopLegalAgreement, "Workshop item created.");
		}
		catch (Exception e) {
			Log.Error("Workshop item creation failed.", e);
			return (false, 0, false, "Workshop item creation failed: " + e.Message);
		}
	}

	public static async Task<List<WorkshopItemInfo>> QueryItemsAsync(IEnumerable<ulong> itemIds, TimeSpan timeout)
	{
		var results = new List<WorkshopItemInfo>();
		if (!IsClient)
			return results;

		ulong[] ids = itemIds.Where(id => id != 0).Distinct().Take(1000).ToArray();
		if (ids.Length == 0)
			return results;

		// Keep requests small; Steam details queries are cheap but there is a 1000-item hard cap.
		foreach (ulong[] batch in ids.Chunk(100)) {
			try {
				results.AddRange(await QueryBatchAsync(batch, timeout).ConfigureAwait(false));
			}
			catch (Exception e) {
				Log.Warn($"Workshop query failed for {batch.Length} item(s).", e);
			}
		}

		return results;
	}

	private static async Task<List<WorkshopItemInfo>> QueryBatchAsync(ulong[] ids, TimeSpan timeout)
	{
		PublishedFileId_t[] published = ids.Select(id => new PublishedFileId_t(id)).ToArray();
		UGCQueryHandle_t handle = SteamUGC.CreateQueryUGCDetailsRequest(published, (uint)published.Length);
		if (handle == UGCQueryHandle_t.Invalid)
			return new List<WorkshopItemInfo>();

		try {
			return await ExecuteQueryAsync(handle, timeout).ConfigureAwait(false);
		}
		finally {
			SteamUGC.ReleaseQueryUGCRequest(handle);
		}
	}

	/// <summary>Queries a user's published items. Used to discover a participant's item after they create it.</summary>
	public static async Task<List<WorkshopItemInfo>> QueryUserItemsAsync(ulong steamId, TimeSpan timeout)
	{
		if (!IsClient || steamId == 0)
			return new List<WorkshopItemInfo>();

		var results = new List<WorkshopItemInfo>();
		for (uint page = 1; page <= 3; page++) {
			UGCQueryHandle_t handle = SteamUGC.CreateQueryUserUGCRequest(
				new AccountID_t((uint)(steamId & 0xFFFFFFFFu)),
				EUserUGCList.k_EUserUGCList_Published,
				EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items,
				EUserUGCListSortOrder.k_EUserUGCListSortOrder_CreationOrderDesc,
				AppId,
				AppId,
				page);

			if (handle == UGCQueryHandle_t.Invalid)
				break;

			try {
				List<WorkshopItemInfo> pageResults = await ExecuteQueryAsync(handle, timeout).ConfigureAwait(false);
				results.AddRange(pageResults);
				if (pageResults.Count < 50)
					break;
			}
			finally {
				SteamUGC.ReleaseQueryUGCRequest(handle);
			}
		}

		return results;
	}

	private static async Task<List<WorkshopItemInfo>> ExecuteQueryAsync(UGCQueryHandle_t handle, TimeSpan timeout)
	{
		var results = new List<WorkshopItemInfo>();

		SteamUGC.SetReturnMetadata(handle, true);
		SteamUGC.SetReturnKeyValueTags(handle, true);
		SteamUGC.SetAllowCachedResponse(handle, 0);

		var tcs = new TaskCompletionSource<SteamUGCQueryCompleted_t>(TaskCreationOptions.RunContinuationsAsynchronously);
		using CallResult<SteamUGCQueryCompleted_t> call = CallResult<SteamUGCQueryCompleted_t>.Create((result, ioFailure) => {
			if (ioFailure) {
				tcs.TrySetException(new IOException("Steam IO failure while querying Workshop items."));
				return;
			}

			tcs.TrySetResult(result);
		});

		SteamAPICall_t apiCall = SteamUGC.SendQueryUGCRequest(handle);
		if (apiCall == SteamAPICall_t.Invalid)
			return results;

		call.Set(apiCall);
		SteamUGCQueryCompleted_t completed = await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);

		if (completed.m_eResult != EResult.k_EResultOK && completed.m_eResult != EResult.k_EResultNoMatch) {
			Log.Warn($"Workshop query returned {completed.m_eResult}.");
			return results;
		}

		for (uint i = 0; i < completed.m_unNumResultsReturned; i++) {
			if (!SteamUGC.GetQueryUGCResult(handle, i, out SteamUGCDetails_t details))
				continue;

			string? metadata = null;
			if (SteamUGC.GetQueryUGCMetadata(handle, i, out string meta, Constants.k_cchDeveloperMetadataMax))
				metadata = meta;

			WorkshopMetadata? parsed = null;
			if (!string.IsNullOrWhiteSpace(metadata)) {
				try {
					parsed = JsonSerializer.Deserialize<WorkshopMetadata>(metadata, JsonOptions);
				}
				catch (Exception e) {
					Log.Warn($"Workshop item {details.m_nPublishedFileId} has unreadable metadata.", e);
				}
			}

			uint state = SteamUGC.GetItemState(details.m_nPublishedFileId);
			results.Add(new WorkshopItemInfo {
				ItemId = details.m_nPublishedFileId.m_PublishedFileId,
				OwnerSteamId = details.m_ulSteamIDOwner,
				Title = details.m_rgchTitle ?? "",
				Description = details.m_rgchDescription ?? "",
				MetadataJson = metadata,
				Metadata = parsed,
				UpdatedUtc = details.m_rtimeUpdated == 0
					? default
					: DateTimeOffset.FromUnixTimeSeconds(details.m_rtimeUpdated).UtcDateTime,
				FileSize = details.m_nFileSize,
				Banned = details.m_bBanned,
				Installed = (state & (uint)EItemState.k_EItemStateInstalled) != 0,
				NeedsUpdate = (state & (uint)EItemState.k_EItemStateNeedsUpdate) != 0
			});
		}

		return results;
	}

	public static async Task<DownloadResult> DownloadAsync(ulong itemId, TimeSpan timeout, IProgress<float>? progress = null)
	{
		if (!IsClient)
			return new DownloadResult { Success = false, Message = "Steam client API is not available." };

		var id = new PublishedFileId_t(itemId);

		try {
			uint state = SteamUGC.GetItemState(id);
			bool installed = (state & (uint)EItemState.k_EItemStateInstalled) != 0;
			bool needsUpdate = (state & (uint)EItemState.k_EItemStateNeedsUpdate) != 0;

			if (installed && !needsUpdate) {
				string? folder = GetInstallFolder(itemId);
				return new DownloadResult {
					Success = folder != null,
					InstallFolder = folder,
					Message = folder != null ? "Workshop item is already installed." : "Workshop item is installed but its folder is unavailable."
				};
			}

			SteamUGC.SubscribeItem(id);

			var tcs = new TaskCompletionSource<DownloadItemResult_t>(TaskCreationOptions.RunContinuationsAsynchronously);
			using Callback<DownloadItemResult_t> callback = Callback<DownloadItemResult_t>.Create(result => {
				if (result.m_unAppID.m_AppId != TmlAppId)
					return;
				if (result.m_nPublishedFileId.m_PublishedFileId != itemId)
					return;

				tcs.TrySetResult(result);
			});

			if (!SteamUGC.DownloadItem(id, true))
				return new DownloadResult { Success = false, Message = "Steam refused to start the Workshop download (not subscribed, not friends, or not logged in)." };

			DownloadItemResult_t result = await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
			if (result.m_eResult != EResult.k_EResultOK)
				return new DownloadResult { Success = false, Message = "Workshop download failed: " + result.m_eResult };

			DateTime deadline = DateTime.UtcNow + timeout;
			while (DateTime.UtcNow < deadline) {
				state = SteamUGC.GetItemState(id);
				installed = (state & (uint)EItemState.k_EItemStateInstalled) != 0;
				needsUpdate = (state & (uint)EItemState.k_EItemStateNeedsUpdate) != 0;
				bool downloading = (state & (uint)(EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0;

				if (installed && !needsUpdate && !downloading)
					break;

				if (SteamUGC.GetItemDownloadInfo(id, out ulong downloaded, out ulong total) && total > 0)
					progress?.Report(Math.Clamp(downloaded / (float)total, 0f, 1f));

				await Task.Delay(200).ConfigureAwait(false);
			}

			string? installFolder = GetInstallFolder(itemId);
			if (installFolder == null)
				return new DownloadResult { Success = false, Message = "Workshop download did not finish in time." };

			return new DownloadResult { Success = true, InstallFolder = installFolder, Message = "Workshop item downloaded." };
		}
		catch (TimeoutException) {
			return new DownloadResult { Success = false, Message = "Workshop download timed out." };
		}
		catch (Exception e) {
			Log.Error($"Workshop download failed for item {itemId}.", e);
			return new DownloadResult { Success = false, Message = "Workshop download failed: " + e.Message, Exception = e };
		}
	}

	public static string? GetInstallFolder(ulong itemId)
	{
		try {
			if (!IsClient)
				return null;

			if (!SteamUGC.GetItemInstallInfo(new PublishedFileId_t(itemId), out _, out string folder, 1000, out _))
				return null;

			return Directory.Exists(folder) ? folder : null;
		}
		catch {
			return null;
		}
	}

	public static async Task<UploadResult> UploadAsync(
		ulong itemId,
		string contentFolder,
		WorkshopMetadata metadata,
		string title,
		string description,
		string changeNote,
		TimeSpan timeout,
		IProgress<(float Progress, string Status)>? progress = null)
	{
		if (!IsClient)
			return new UploadResult { Success = false, Message = "Steam client API is not available." };

		if (itemId == 0)
			return new UploadResult { Success = false, Message = "No Workshop item ID." };

		if (!Directory.Exists(contentFolder))
			return new UploadResult { Success = false, Message = "Snapshot folder is missing: " + contentFolder };

		string metadataJson = SerializeMetadata(metadata);
		if (System.Text.Encoding.UTF8.GetByteCount(metadataJson) >= Constants.k_cchDeveloperMetadataMax)
			return new UploadResult { Success = false, Message = "Workshop metadata exceeds 5000 bytes." };

		var id = new PublishedFileId_t(itemId);
		UGCUpdateHandle_t handle = SteamUGC.StartItemUpdate(AppId, id);
		if (handle == UGCUpdateHandle_t.Invalid)
			return new UploadResult { Success = false, Message = "Steam rejected the Workshop item update handle." };

		try {
			SteamUGC.SetItemTitle(handle, Truncate(title, 100));
			SteamUGC.SetItemDescription(handle, Truncate(description, Constants.k_cchPublishedDocumentDescriptionMax - 1));
			SteamUGC.SetItemUpdateLanguage(handle, "english");

			if (!SteamUGC.SetItemVisibility(handle, ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly))
				return new UploadResult { Success = false, Pending = true, ItemId = itemId, Message = "Steam refused to set the item to friends-only." };
			if (!SteamUGC.SetItemContent(handle, contentFolder))
				return new UploadResult { Success = false, Pending = true, ItemId = itemId, Message = "Steam refused the snapshot content folder." };
			if (!SteamUGC.SetItemMetadata(handle, metadataJson))
				return new UploadResult { Success = false, Pending = true, ItemId = itemId, Message = "Steam refused the version metadata (5000-byte limit)." };

			// Searchable tags. Keys/values are limited to 255 chars and 100 tags per update.
			SteamUGC.RemoveItemKeyValueTags(handle, "sharedworld");
			SteamUGC.RemoveItemKeyValueTags(handle, "worldid");
			SteamUGC.AddItemKeyValueTag(handle, "sharedworld", "1");
			SteamUGC.AddItemKeyValueTag(handle, "worldid", Truncate(metadata.WorldId, 100));

			var tcs = new TaskCompletionSource<SubmitItemUpdateResult_t>(TaskCreationOptions.RunContinuationsAsynchronously);
			using CallResult<SubmitItemUpdateResult_t> call = CallResult<SubmitItemUpdateResult_t>.Create((result, ioFailure) => {
				if (ioFailure) {
					tcs.TrySetException(new IOException("Steam IO failure while uploading the Workshop item."));
					return;
				}

				tcs.TrySetResult(result);
			});

			SteamAPICall_t apiCall = SteamUGC.SubmitItemUpdate(handle, Truncate(changeNote, 1000));
			if (apiCall == SteamAPICall_t.Invalid)
				return new UploadResult { Success = false, Message = "Steam rejected the Workshop upload." };

			call.Set(apiCall);

			DateTime deadline = DateTime.UtcNow + timeout;
			while (!tcs.Task.IsCompleted && DateTime.UtcNow < deadline) {
				EItemUpdateStatus status = SteamUGC.GetItemUpdateProgress(handle, out ulong done, out ulong total);
				float value = total == 0 ? 0f : Math.Clamp(done / (float)total, 0f, 1f);
				progress?.Report((value, status.ToString()));
				await Task.Delay(200).ConfigureAwait(false);
			}

			if (!tcs.Task.IsCompleted)
				return new UploadResult { Success = false, Pending = true, ItemId = itemId, Message = "Workshop upload timed out; it may still finish in Steam. The snapshot is kept for retry." };

			SubmitItemUpdateResult_t result = await tcs.Task.ConfigureAwait(false);

			if (result.m_eResult != EResult.k_EResultOK) {
				return new UploadResult {
					Success = false,
					Pending = true,
					NeedsEula = result.m_bUserNeedsToAcceptWorkshopLegalAgreement,
					ItemId = itemId,
					Message = "Workshop upload failed: " + result.m_eResult
				};
			}

			progress?.Report((1f, "Committed"));
			return new UploadResult {
				Success = true,
				NeedsEula = result.m_bUserNeedsToAcceptWorkshopLegalAgreement,
				ItemId = itemId,
				Message = "Workshop upload complete."
			};
		}
		catch (Exception e) {
			Log.Error($"Workshop upload failed for item {itemId}.", e);
			return new UploadResult { Success = false, Pending = true, ItemId = itemId, Message = "Workshop upload failed: " + e.Message, Exception = e };
		}
	}

	public static string SerializeMetadata(WorkshopMetadata metadata)
	{
		// Trim the optional participant map until it fits. The content manifest always has the full map.
		string json = JsonSerializer.Serialize(metadata, JsonOptions);
		if (System.Text.Encoding.UTF8.GetByteCount(json) < Constants.k_cchDeveloperMetadataMax)
			return json;

		if (metadata.Participants != null) {
			var trimmed = new Dictionary<string, ulong>(metadata.Participants);
			while (trimmed.Count > 0) {
				string last = trimmed.Keys.Last();
				trimmed.Remove(last);
				metadata.Participants = new Dictionary<string, ulong>(trimmed);
				json = JsonSerializer.Serialize(metadata, JsonOptions);
				if (System.Text.Encoding.UTF8.GetByteCount(json) < Constants.k_cchDeveloperMetadataMax)
					return json;
			}
		}

		metadata.Participants = null;
		json = JsonSerializer.Serialize(metadata, JsonOptions);
		if (System.Text.Encoding.UTF8.GetByteCount(json) >= Constants.k_cchDeveloperMetadataMax) {
			// Last resort: keep only the fields required for version discovery.
			metadata.WorldName = Truncate(metadata.WorldName, 64);
			json = JsonSerializer.Serialize(metadata, JsonOptions);
		}

		return json;
	}

	private static string Truncate(string? value, int max)
	{
		if (string.IsNullOrEmpty(value))
			return "";
		return value.Length <= max ? value : value[..max];
	}
}
