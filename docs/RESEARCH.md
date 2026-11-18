# Research: Steam Workshop as shared-world storage for tModLoader

Status: research completed **before implementation**, 2026-11-18. This document records what was verified, what is inferred, and what remains to be tested in-game. Sources are listed at the bottom.

## 0. Baseline

| Item | Value |
|---|---|
| tModLoader | **v2026.09.3.1** stable (`BuildInfo` informational version `2026.09.3.1\|2026.09`), 1.4.4 branch. Started on 2026.08.3.0; Steam updated the install mid-project on 2026-11-18. The mod built for both and loaded on both. |
| Runtime | .NET 8 / `net8.0`; tModLoader ships its own `dotnet` runtime |
| Steamworks.NET | **2025.162.4** (`Libraries/steamworks.net.anycpu/2025.162.4`) |
| Game | Terraria 1.4.4.9-compatible tModLoader |
| Test install | `~/.local/share/Steam/steamapps/common/tModLoader` |
| Save dir | `~/.local/share/Terraria/tModLoader` (`Main.SavePath`), worlds in `Main.WorldPath = SavePath/Worlds` |
| Source studied | `github.com/tModLoader/tModLoader` branch `1.4.4`, commit `86edf910` (2026-11-02); installed `tModLoader.dll` decompiled for exact runtime behaviour |

The installed build and the source branch are close but not identical. Behaviour below was cross-checked against the installed assembly with ILSpy where it matters. The 2026.09.3.1 update was installed by Steam during this work; the mod was built and smoke-tested against it.

---

## 1. Steamworks / Steam Workshop

### 1.1 The Workshop API is `ISteamUGC`

A Workshop item is a **folder of files**, not a single file. The lifecycle is:

1. `SteamUGC.CreateItem(AppId_t nConsumerAppId, EWorkshopFileType eFileType)` → `CreateItemResult_t` (async call result) → `m_nPublishedFileId`.
2. `SteamUGC.StartItemUpdate(AppId_t, PublishedFileId_t)` → `UGCUpdateHandle_t`.
3. Configure the update on the handle:
   - `SetItemTitle`, `SetItemDescription`, `SetItemUpdateLanguage`
   - `SetItemVisibility(ERemoteStoragePublishedFileVisibility)`
   - `SetItemTags(SteamParamStringArray_t*)`
   - `AddItemKeyValueTag(handle, key, value)` / `RemoveItemKeyValueTags`
   - `SetItemMetadata(handle, string)` (returns without download; see 1.4)
   - `SetItemContent(handle, absoluteFolderPath)`
   - `SetItemPreview(handle, absoluteImagePath)` (optional; uses Steam Cloud quota)
4. `SteamUGC.SubmitItemUpdate(handle, changeNote)` → `SubmitItemUpdateResult_t`.
5. Progress via `SteamUGC.GetItemUpdateProgress(handle, out bytesProcessed, out bytesTotal)`; status enum `EItemUpdateStatus` (PreparingConfig, PreparingContent, UploadingContent, UploadingPreviewFile, CommittingChanges).

Important facts:

- **There is no cancel.** Once `SubmitItemUpdate` is called the upload cannot be cancelled through the API.
- **One update at a time per item.** `StartItemUpdate` returns a handle; do not start a second update for the same item concurrently.
- `SubmitItemUpdateResult_t` can return:
  - `k_EResultOK`
  - `k_EResultFail`, `k_EResultInvalidParam` (wrong app ID / UGC not enabled / preview too small)
  - `k_EResultAccessDenied` (user does not own the app)
  - `k_EResultFileNotFound` (content folder or preview missing)
  - `k_EResultLockingFailed` (another update holds the UGC lock)
  - `k_EResultLimitExceeded` (preview > 1 MB or no Steam Cloud space)
  - `m_bUserNeedsToAcceptWorkshopLegalAgreement`
- **Only the item's author/owner can update it.** Steam's Workshop documentation says "Authors of ready-to-use items can update their items". There is no API to add a co-author with upload rights; the web UI's "contributors" are credit, not write access. This is the reason for the per-account item design.
- **Creating/updating requires accepting the Steam Workshop Legal Agreement.** The result flags `m_bUserNeedsToAcceptWorkshopLegalAgreement`. New API (used by tModLoader): `SteamUGC.GetWorkshopEULAStatus()` / `SteamUGC.ShowWorkshopEULA()`.
- **The Workshop must be enabled for the app.** tModLoader has it enabled (that is how mods upload). The API must be called with the tModLoader app ID **1281930**, not Terraria's 105600. tModLoader's `ModLoader.Engine.Steam.TMLAppID = 1281930`.
- **`CreateItem` file type:** tModLoader itself uses `EWorkshopFileType.k_EWorkshopFileTypeFirst` (value 0, alias of `k_EWorkshopFileTypeCommunity`). `k_EWorkshopFileTypeCommunity` is the normal "users can subscribe" type. We use `k_EWorkshopFileTypeCommunity`.

### 1.2 Visibility

`ERemoteStoragePublishedFileVisibility` (official values):

| Value | Meaning | Usable for our case? |
|---|---|---|
| `Public` (0) | Visible to everyone. | No — project constraint. |
| `FriendsOnly` (1) | **Visible to friends only.** | **Yes — primary.** |
| `Private` (2) | **Only visible to the creator.** | Owner-only; other accounts cannot query/download it. Not usable for sharing. |
| `Unlisted` (3) | Visible to everyone, but excluded from global queries; not returned in user lists unless caller is creator or subscriber. | Not used (constraint says private/friends-only). |

Consequences:

- **Private is owner-only.** If Alice creates a private item, Bob cannot query or download it, even if he knows the ID. It cannot be the shared storage primitive.
- **FriendsOnly is the correct primitive.** Bob must be a Steam friend of Alice. He can then query the item by ID (and tModLoader already does this for friends-only mods) and subscribe/download it.
- **Friendship is part of the trust model.** If a participant removes the owner from friends, they lose access to that owner's latest snapshot. The UI must say so, and the group should keep at least one other participant up to date.
- **Unlisted would avoid the friendship requirement but violates the stated privacy constraint.** Do not silently switch to it.

### 1.3 Querying item details without downloading content

`SteamUGC.CreateQueryUGCDetailsRequest(PublishedFileId_t[] ids, uint count)` + `SetReturnMetadata(true)` + `SendQueryUGCRequest` returns `SteamUGCDetails_t` and the metadata string **without downloading the folder**.

Useful `SteamUGCDetails_t` fields:

- `m_nPublishedFileId`, `m_nCreatorAppID`, `m_nConsumerAppID`
- `m_ulSteamIDOwner`
- `m_rgchTitle`, `m_rgchDescription`
- `m_rtimeCreated`, `m_rtimeUpdated` (Unix time)
- `m_eVisibility`, `m_bBanned`, `m_bAcceptedForUse`
- `m_eFileType`, `m_nFileSize`, `m_pchFileName`
- `m_rgchTags` (comma-separated), `m_unNumChildren`

Metadata is retrieved separately with:

```csharp
SteamUGC.GetQueryUGCMetadata(handle, index, out string metadata, Constants.k_cchDeveloperMetadataMax);
```

- **`k_cchDeveloperMetadataMax = 5000` bytes.** This is the hard limit for `SetItemMetadata`.
- Metadata is explicitly documented as readable from queries **without downloading/installing the content**.
- Key-value tags are also queryable (`SetReturnKeyValueTags`, `GetQueryUGCNumKeyValueTags`, `GetQueryUGCKeyValueTag`) and are limited to **100 tags per update**, key/value ≤255 chars, keys alphanumeric/underscore only. Metadata is simpler for a JSON version header.

Practical gotcha: query results can be cached. tModLoader disables caching with `SetAllowCachedResponse(handle, 0)` because "anything other than 0 may cause Access Denied errors" (their comment). We do the same and retry because Workshop propagation can still return the previous metadata for a short time after an upload.

### 1.4 Item state, subscribing and downloading

`SteamUGC.GetItemState(PublishedFileId_t)` returns a bitmask (`EItemState`):

| Flag | Value | Meaning |
|---|---|---|
| `k_EItemStateNone` | 0 | Not tracked on this client |
| `k_EItemStateSubscribed` | 1 | Current user is subscribed |
| `k_EItemStateLegacyItem` | 2 | Old RemoteStorage item |
| `k_EItemStateInstalled` | 4 | Installed and usable (may be out of date) |
| `k_EItemStateNeedsUpdate` | 8 | Not installed yet, or creator updated content |
| `k_EItemStateDownloading` | 16 | Download in progress |
| `k_EItemStateDownloadPending` | 32 | `DownloadItem` called; content not available until `DownloadItemResult_t` |

Flow:

- `SteamUGC.SubscribeItem(id)` → `RemoteStorageSubscribePublishedFileResult_t` (async; subscription applies as soon as possible).
- `SteamUGC.DownloadItem(id, bHighPriority)` → `DownloadItemResult_t` callback (includes app ID and item ID). **Do not read/install the item until this callback fires.**
- `SteamUGC.GetItemDownloadInfo(id, out downloaded, out total)` for progress.
- `SteamUGC.GetItemInstallInfo(id, out sizeOnDisk, out folder, 1000, out timestamp)` gives the absolute content folder once `Installed` is set.
- `SteamUGC.UnsubscribeItem(id)`; the item is removed **after the game quits**, not immediately.

Notes:

- `DownloadItem` works for an item the user is not subscribed to (Steam caches it temporarily), but for a friends-only item we subscribe first. This also keeps the item available for future updates.
- tModLoader's own `SteamedWraps.ModDownloadInstance.Download` does exactly `SubscribeItem` (client) → `DownloadItem` → poll state/progress → wait for `DownloadItemResult_t`. That code is `internal`, but we can use the same public Steamworks calls.
- A friends-only item is visible to friends, so the friend can subscribe/download it. This is corroborated by tModLoader's workshop code, which explicitly supports finding friends-only mods via an author query, and by the visibility definition. **Still verify with two real accounts in-game before release.**

### 1.5 Size / update limits

Public Steamworks pages do not state a single universal Workshop item size limit; it is configured per app and enforced server-side. What is certain:

- `SetItemMetadata` max **5000 bytes**.
- Preview images must be **< 1 MB** and require Steam Cloud quota.
- Title/description/tags have their own character limits (`k_cchPublishedDocumentTitleMax`, etc.).
- tModLoader mods are routinely uploaded at tens to hundreds of MB, so world snapshots (typically **2–15 MB**, compressed far less) are comfortably within normal limits.
- `SubmitItemUpdate` has no public per-minute quota documented, but it is a network upload; we do **not** upload every autosave. We upload at most once per session/world save, and only if the content hash changed.
- World snapshots are compressed and capped by us at **64 MB** (uncompressed) as a sanity limit; larger worlds are rejected with a clear error rather than risking a partial upload.

### 1.6 Workshop Legal Agreement / Steam Subscriber Agreement

- Items are hidden until the contributor accepts the **Steam Workshop Legal Agreement**.
- Steam Subscriber Agreement §6.B ("Content Uploaded to the Steam Workshop") says Workshop contributions can be viewed by the Steam community and that Valve may restrict or remove them for any reason. Uploading grants Valve a broad, non-exclusive licence to use/distribute the contribution.
- This applies even to friends-only/private items (the content is still made available through Steam). The mod must require an explicit user action and show a notice before the first upload. Do not upload automatically without the player having registered the world and accepted the explanation.
- tModLoader's own **Mod Upload Rules** apply to all mod content, including private/friends-only content:
  - Rule 1: do not silently destroy worlds/players/settings.
  - Rule 2: file system access must stay inside tModLoader save/config directories; no external programs.
  - Rule 3: no obfuscation/native libraries.
  - Rule 4: **no Steam API abuse** — "Do not interact with Steam on behalf of the user (outside the context of the tModLoader/Terraria game)."
  - Our interpretation: uploading the player's own Terraria world, from inside tModLoader, for the purpose of playing that world with friends, is within the game context. We keep every Steam action user-initiated, visible, rate-limited, and inside `Main.SavePath`. This is a judgement call, not a legal ruling; document it publicly and be ready to change the storage layer if tModLoader moderators object.
- Because the mod is intended to be published publicly later, the Workshop items it creates are always `FriendsOnly`, titled/described as private shared-world storage, and never contain executable content.

### 1.7 Steam Cloud and Steam Remote Play — why they do not solve the problem

- **Steam Cloud / Remote Storage** is per Steam account (and per app). It syncs a user's files between that user's machines. There is no supported API for sharing a Cloud file with another account. Quota is per user, not per group.
- **Steam Remote Play Together** streams a running game from a host to friends. The host must be online and running the game. It provides no storage and no offline hand-off.
- **Steam Datagram Relay** is transport for Steam networking; not storage.
- Therefore Steam Workshop is the only always-on, Valve-hosted, account-shareable storage available under the constraints.

### 1.8 Steam networking and the friends join flow (what we rely on)

Terraria/tModLoader 1.4.4 uses the **legacy `ISteamNetworking` P2P session API** (`P2PSessionRequest_t`, `SteamNetworking.SendP2PPacket`, `AcceptP2PSessionWithUser`), not `ISteamNetworkingSockets` directly. Lobbies are `ISteamMatchmaking`:

- Host launches a server with `-lobby friends` (or `-lobby private`) and optionally `-friendsoffriends`.
- The server rejects a P2P session unless the sender is a friend of the host, unless FriendsOfFriends is enabled.
- `ModPacket` is sent over this existing Terraria connection, so we do not need a second networking stack.
- Friends join through the Steam friends list / lobby ("Join Game"); the host must be online while hosting.

This is exactly the flow the mod piggybacks on. There is no dedicated server component in our design.

---

## 2. tModLoader internals

### 2.1 Can mod code call Steam UGC directly?

**Yes, verified in source and in the shipped assembly.**

1. Mods are compiled against **every DLL in `tModLoader/Libraries`** except native/runtime/tModCodeAssist assemblies. `ModCompile.GetTerrariaReferences()` enumerates `Libraries/**/*.dll`. Steamworks.NET is in that set.
2. At runtime, tModLoader registers an assembly redirect:
   ```csharp
   ["Steamworks.NET"] = typeof(Steamworks.SteamApps).Assembly
   ```
   in `AssemblyManager.AssemblyRedirects`. This explicitly exists so mods can reference Steamworks.NET across version changes and bind to the host's copy.
3. tModLoader has already initialized Steam (`SteamAPI.Init()` in `CoreSocialModule`) before mods load, and its callback thread pumps Steam callbacks. `SteamedWraps.SteamAvailable` / `SteamClient` expose whether the client API is usable.
4. `Terraria.Social.Steam.SteamedWraps` is a **public** static helper, but only some members are public. `SteamClient` is public; `SteamAvailable` is `internal` in the shipped build. `ModDownloadInstance` and the upload-handle helpers are `internal`. The raw `Steamworks.SteamUGC` API is public and reachable, so we use it directly and only use `SteamedWraps.SteamClient` for availability.

Conclusion: we can implement a small `WorkshopService` using `Steamworks.SteamUGC` directly, isolated behind our own interface, with `SteamedWraps` used only for availability checks and convenience. This is a real dependency on a bundled assembly, not a hack. We must handle Steam being unavailable (GOG, family share, offline) gracefully.

### 2.2 Can a mod upload non-mod content to a tModLoader Workshop item?

**Yes, at the Steam level.** Steam Workshop items are opaque folders. tModLoader's own publish code does:

```csharp
SteamUGC.SetItemContent(handle, contentFolderPath);
SteamUGC.SetItemTags(handle, tags);
SteamUGC.SubmitItemUpdate(handle, changeNotes);
```

Nothing in Steam validates that the folder contains a `.tmod`. A world snapshot folder with `.wld`, `.twld`, and `manifest.json` uploads fine.

tModLoader-side consequences:

- On startup and when scanning workshop content, `ModOrganizer` searches the workshop content directory for `*.tmod` recursively (`ReadModFiles(ModLocation.Workshop, Directory.GetFiles(repo, "*.tmod", ...))`). A folder with no `.tmod` is ignored as a mod. It does not get loaded or auto-enabled.
- `WorkshopBrowserModule` only caches/downloads items that correspond to installed `.tmod` files. A world item without a `.tmod` is not treated as a mod.
- It may still appear in the owner's "My Published" workshop UI and on the Steam Community Workshop page (friends-only). It should be clearly titled "Shared World storage — do not subscribe as a mod" and contain no executable content.
- Risk: tModLoader moderators could consider non-mod items in the tModLoader Workshop to be outside its intended use. Keep items FriendsOnly, clearly labelled, and be prepared to fall back to peer transfer. This is a **policy risk**, not a technical blocker.

### 2.3 The per-account item model is forced by the API

There is no API for "add an uploader" to an item. `StartItemUpdate` is authorized against the item owner. Therefore:

- One item **per Steam account per shared world**.
- Each item has exactly one writer (its owner).
- The manifest records `steamId -> publishedFileId` for all known participants.
- The newest version is the maximum version across all these items, subject to fork detection.
- If a participant leaves, their item remains readable (if friendship remains) but is no longer written.

This matches the project plan and is confirmed as the correct workaround.

**Discovery gotcha:** if a participant joins before they have created their item, the other accounts know their Steam ID but not an item ID. A participant can also delete/recreate their item. To find it later, query the participant's published items with `CreateQueryUserUGCRequest(accountId, k_EUserUGCList_Published, k_EUGCMatchingUGCType_Items, ...)`, then match `WorkshopMetadata.worldId`. tModLoader itself uses this route for friends-only mods. The mod therefore stores participant Steam IDs with item ID 0 until the item is discovered.

### 2.4 Version metadata without downloading

We can put a compact JSON header in `SetItemMetadata` (≤5000 bytes) containing at least:

- schema, worldId, version, contentHash, parentHash, lastHostSteamId, timestamp, world name
- optionally the participant item-ID map

Then `CreateQueryUGCDetailsRequest` + `SetReturnMetadata(true)` gives us all known items' version headers without downloading any content. Only the winner is downloaded.

If metadata is missing/corrupt (older upload, Steam hiccup), fall back to:
- use `m_rtimeUpdated` and `m_nFileSize` as hints;
- download the item (last resort) and read its `manifest.json`;
- surface a warning rather than guessing silently.

### 2.5 World files, save hooks, and when a snapshot is valid

- Terraria world: `<name>.wld`.
- tModLoader mod world data: `<name>.twld`, written by `WorldIO.Save` **after** the `.wld` is written.
- `WorldFile.SaveWorld(...)` writes both. `Main.worldPathName` is `Main.ActiveWorldFileData.Path`.
- `ModSystem.SaveWorldData(TagCompound)` runs while the `.twld` is being written.
- `ModSystem.OnWorldUnload()` is called:
  - **single-player Save & Quit**, after `WorldFile.SaveWorld()` (`WorldGen.SaveAndQuitCallBack`);
  - **dedicated/Host & Play server shutdown**, after `WorldFile.SaveWorld()` (`Main.DedServ` loop);
  - on manual client disconnect, but clients are not authoritative.
- `ModSystem.OnWorldLoad()` / `PostWorldLoad()` run on load.
- `.twld` is at `Path.ChangeExtension(worldPath, ".twld")`.
- tModLoader itself creates rolling `.wld.bak`/`.twld.bak` backups. We add our own snapshot backup to avoid relying on vanilla backup rotation.

**Critical:** snapshot only on the authoritative side (`Main.netMode == 0` single-player, or `Main.netMode == 2` server). A multiplayer client (`netMode == 1`) must never upload its in-memory world or overwrite its local file with partial data.

### 2.6 Host & Play: it is a separate server process

This is the biggest practical finding. In 1.4.4, "Host & Play" does **not** convert the current client into a server. It:

1. Saves/sets the active world.
2. Builds command-line arguments (`-server`, `-world`, `-modpath`, `-autoshutdown`, `-password`, …).
3. Spawns a **second process**: `Environment.ProcessPath` + `tModLoader.dll -server ...`.
4. Calls `SocialAPI.Network.LaunchLocalServer(tServer, MenuServerMode)`, which (in the client-side `NetClientSocialModule`) appends:
   - `-steam -localsteamid <id>`
   - `-lobby friends` or `-lobby private`
   - `-friendsoffriends` if enabled
5. Starts the process and connects the client to `127.0.0.1`.
6. The server process runs the world; the host client is a normal multiplayer client.

Consequences:

- **True live conversion from single-player to multiplayer is not realistic.** The world would have to be unloaded from the client, a server process started on the same files, and the client reconnected. The plan's "save, return to menu/loading, relaunch as Host & Play" is the correct implementation.
- There are two `OnSubmitServerPassword` methods on `Main` (a `string` overload and a parameterless one). Reflection must request the parameterless overload explicitly (`Type.EmptyTypes`), otherwise it throws `AmbiguousMatchException` during mod load.
- The server process is where the authoritative world file and the latest save live. It must perform the Workshop upload at shutdown. tModLoader's server calls `OnWorldUnload()` after saving, so that is our hook.
- The server process has Steam initialized (`SteamAPI.Init()` is called for `Main.dedServ` when Steam is available) and is the same Steam account as the host. It can call `SteamUGC`.
- `Main.OnSubmitServerPassword()` is **private**, but it is the exact routine that performs this launch. We will call it by reflection after setting `Main.ActiveWorldFileData`, `Netplay.ServerPassword`, and `Main.MenuServerMode`. If reflection ever fails (tModLoader update), fall back to opening the normal Host & Play settings screen (`Main.menuMode = 889`) with a clear message. This is documented as a compatibility risk.
- "Open to friends" therefore means: save the world, return to the title screen, then automatically start Host & Play with `ServerMode.Lobby | ServerMode.FriendsCanJoin` (not FriendsOfFriends). The host must stay online while hosting.

### 2.7 Networking with ModPacket

- `Mod.GetPacket(capacity)` creates a `ModPacket` (a `BinaryWriter` over a memory stream). It writes a 2-byte length placeholder, a message ID, and the mod's net ID. `HandlePacket(BinaryReader reader, int whoAmI)` on the `Mod` class receives them.
- Maximum packet size is **65535 bytes** (`ModPacket.Finish` throws if `OutStream.Position > ushort.MaxValue`). That includes the length and mod/message IDs. Use chunks of ~16 KB to stay well below the limit and avoid buffering/latency problems.
- Packets from a client go to the server; the server must relay to other clients. Clients never talk directly to each other.
- Steam P2P sockets are reliable and ordered, so chunk loss is not expected except on disconnect. We still verify the final SHA-256 and keep partial files out of the real world path.
- tModLoader deliberately does not sync arbitrary custom packets for us. Every piece of state we change on one side must be sent explicitly.

### 2.8 UI system and the main-menu problem

- `UIState` + `UserInterface` + `UIElement` is the standard UI stack. `UIPanel`, `UIText`, `UITextPanel<string>`, `UIList`, `UIScrollbar`, `UIImageButton` are available. ExampleMod's `ExampleCoinsUI` and `ExampleFullscreenUI` are good references.
- UI code must be client-only. Use `[Autoload(Side = ModSide.Client)]` on the UI `ModSystem`/`ModMenu`, or guard with `Main.dedServ`.
- There is **no public tModLoader hook to add a button to the vanilla main menu**. `Terraria.ModLoader.UI.Interface` is `internal`; `MenuLoader.Add` is internal; the vanilla menu button array is patched by tModLoader itself.
- `ModMenu` is public and lets a mod provide a whole main-menu theme, with its own `UserInterface` that is updated/drawn while that theme is selected. That is the intended public UI surface.
- Workaround we will use: a small client `ModSystem` appends a "Shared Worlds" button to `MenuLoader.CurrentMenu.UserInterface` while on the title screen. `MenuLoader.CurrentMenu` and `ModMenu.UserInterface` are public. The button opens a full-screen `UIState` via `Main.MenuUI.SetState(...)` / `Main.menuMode = 888`. We re-attach the button if the player switches menu themes and remove it while a full-screen state is open. This is less invasive than Harmony-patching `Main.DrawMenu`, and it is easy to disable if another menu mod conflicts.
- Fallback: a keybind (default `F9`) opens the same screen. This guarantees access even if the menu injection breaks after a tModLoader update.
- `Main.MenuUI.SetState(state); Main.menuMode = 888;` is how tModLoader's own menus open. Going back: `Main.MenuUI.SetState(null); Main.menuMode = 0;`.

### 2.9 Mod structure / build

- Mods are folders with `build.txt`, `description.txt`, optionally `icon.png`, and C# sources.
- `build.txt` fields: `author`, `version`, `displayName`, `side`, `homepage`, `description`, `modReferences`, `weakReferences`, `noCompile`, etc.
- `tMLMod.targets` provides references to `tModLoader.dll` and all `Libraries/**/*.dll`, and after `dotnet build` invokes `dotnet tModLoader.dll -build <project> -eac <dll>` to produce the `.tmod`.
- `BuildMod` can be disabled for CI compile checks. The mod's `.csproj` needs `tMLSteamPath` set to the tModLoader install (or a downloaded release).
- Mods are loaded into a collectible `ModLoadContext`; `Steamworks.NET`, `FNA`, and `tModLoader` are redirected to the host assemblies.
- Mod code that only makes sense on a client must be isolated. Our networking/storage core loads on both sides; UI and Steam *upload* UI do not.

### 2.10 World data sync (ModSystem.NetSend / NetReceive)

- `ModSystem.NetSend(BinaryWriter)` / `NetReceive(BinaryReader)` exchange mod world data whenever the world data is synced (on join, and when `NetMessage.SendData(MessageID.WorldData)` is called).
- They must be overridden together. This is the right way to send the shared `worldId` and manifest summary to joining clients, rather than a custom packet that might race the join.
- For larger/on-demand data (snapshot chunks, item-ID exchange) use `ModPacket`.

---

## 3. Answers to the plan's open questions

| Question | Answer | Confidence |
|---|---|---|
| Can mod code call Steam UGC directly? | **Yes.** `Steamworks.NET` is a compile reference and is explicitly redirected to the host assembly in `AssemblyManager`. `SteamAPI` is initialized by tModLoader. | Verified in source + shipped DLL |
| Can we upload a non-mod payload? | **Yes.** Workshop items are folders. tModLoader only treats folders containing `.tmod` as mods; a world snapshot folder is ignored. | Verified in tModLoader source; needs live Steam test |
| Does a friends-only item let friends subscribe/download programmatically? | **Yes, if they are Steam friends.** Friends-only items are visible to friends, can be queried by ID, and `SubscribeItem` + `DownloadItem` work. Private items cannot be used by others. | High; tModLoader code supports friends-only mod browsing; verify live |
| Can item metadata be read without downloading? | **Yes.** `SetItemMetadata` + `SetReturnMetadata` + `GetQueryUGCMetadata`; max **5000 bytes**. | Official docs |
| Size/update limits? | Metadata 5000 B; preview <1 MB; per-app content size configured by Valve (not public). Our 2–15 MB worlds are safe; we cap at 64 MB and upload once per changed session. | Partial; empirical test needed |
| Can only the owner update an item? | **Yes.** No API for contributor upload rights. Per-account items are required. | Official + API behaviour |
| Can we convert single-player to multiplayer live? | **No, not realistically.** Host & Play spawns a separate server process; the client reconnects. Save → title → Host & Play is the correct flow. | Verified in decompiled `Main` |
| Steam Cloud / Remote Play as fallback? | **No.** Cloud is per-account; Remote Play needs an online host. | Official docs |
| Can we use ModPacket for peer transfer? | **Yes.** Max 65535 B; use 16 KB chunks, verify hash, server relays. | Official API + source |

---

## 4. Risks and gotchas to design around

1. **Workshop propagation delay.** An upload may not be visible to other accounts immediately. Query with `SetAllowCachedResponse(0)`, retry with backoff, and show "uploaded, waiting for Steam to propagate" rather than "failed".
2. **Metadata staleness / cache.** Same as above. The uploader's local registry is authoritative for its own item; other accounts may see the previous version for a while.
3. **Friends-only access.** If two participants are not Steam friends, the download fails. Detect with `SteamFriends.GetFriendRelationship` and explain exactly who needs to add whom.
4. **Item moderation/removal.** Valve may remove a Workshop item. The mod must treat a missing item as "no data from that account" and fall back to other participants/local copies; never delete local data because a remote item vanished.
5. **tModLoader policy risk.** Non-mod items in the tModLoader Workshop may be considered out of scope. Keep FriendsOnly, label clearly, and keep peer transfer as a fully functional fallback.
6. **Owner-only upload.** A participant who has never created an item cannot publish. Create the item lazily on first share/host, and make the UI explain the one-time Workshop EULA.
7. **Steam unavailable.** GOG/family-share/offline: no Workshop. Show "Workshop storage unavailable" and use peer transfer + stale warnings.
8. **Separate server process.** The server, not the host client, owns the saved world. Upload at server `OnWorldUnload`; the host client must not upload a stale copy.
9. **Two hosts at once.** Inevitable fork. Detect via `parentHash` and version DAG; never auto-overwrite a diverged head. Present a choice and keep both backups.
10. **Huge worlds / slow connections.** 64 MB cap, compression, chunked transfer with progress, timeout and cancel; never block the game's main thread for an unbounded time.
11. **Interrupted save/upload.** Hash before swapping; keep a backup; write to temp and atomically move; never leave a half-written `.wld`.
12. **Private API use for Host & Play.** `Main.OnSubmitServerPassword` is private. Isolate it in one method with a fallback to the normal Host & Play screen.
13. **Main-menu injection.** Appending to another ModMenu's `UserInterface` is a public-but-unusual workaround. Keep a keybind fallback and make it easy to disable.
14. **Mod list mismatch.** tModLoader already refuses joins on mod mismatch; we still record the mod list in the manifest for diagnostics and for the "which version was this?" history.
15. **World name collisions on download.** A participant may already have a different local world with the same name. Never overwrite by name; match by `worldId`, and choose a suffixed filename if needed.
16. **Cloud-saved worlds.** Steam Cloud worlds are per-account and their paths are managed differently. For v1, shared-world registration is limited to local worlds; detect and explain.

---

## 5. Implementation verification (as of 2026-11-18)

What was actually run:

- **Build:** `dotnet build -c Release` against the Steam-installed tModLoader 2026.09.3.1. The `.tmod` is produced successfully. The only build warning is `Image loading failed: unknown image type` for the mod icon on this Linux setup; the mod still packages. This is a tooling warning, not a mod-load failure.
- **Dedicated server smoke test:** a copy of a real `.wld`/`.twld` was loaded in a temp save directory with only this mod enabled. The mod loaded, `OnWorldLoad` ran, `OnWorldUnload` ran after the save, and a verified snapshot (`world.wld`, `world.twld`, `manifest.json`) was written. No exceptions in `server.log`.
- **Recovery test:** deleted the registry, kept the `.twld`, loaded the world again. The mod read `SharedWorldId` from the `.twld`, recreated the registration, and produced a snapshot.
- **Version/hash tests:** a small console harness covers linear history, fast-forward, forks, missing ancestry, invite round-trips (including Unicode), and deterministic content hashes. All pass.
- **Not yet tested:** the two-account friends-only Steam Workshop flow (create/upload/query/download) and the graphical menu, because the test machine's Steam client was not logged in during this session. These are the first manual tests to run before publishing.

The smoke tests confirmed an important detail: a freshly registered world with no prior snapshot gets `ParentHash = ""` for v1, not a self-parent. The snapshot builder was adjusted accordingly.

---

## 6. Sources

Steamworks (official):

- ISteamUGC API: <https://partner.steamgames.com/doc/api/ISteamUGC>
- ISteamRemoteStorage / visibility / file types: <https://partner.steamgames.com/doc/api/ISteamRemoteStorage>
- Workshop implementation guide: <https://partner.steamgames.com/doc/features/workshop/implementation>
- Workshop overview: <https://partner.steamgames.com/doc/features/workshop>
- Workshop item versioning: <https://partner.steamgames.com/doc/features/workshop/itemversioning>
- Steam Workshop Legal Agreement: <https://steamcommunity.com/sharedfiles/workshoplegalagreement>
- Steam Subscriber Agreement §6: <https://store.steampowered.com/subscriber_agreement/>
- Steam Cloud: <https://partner.steamgames.com/doc/features/cloud>
- Steam Remote Play: <https://partner.steamgames.com/doc/features/remoteplay>
- Steam Networking: <https://partner.steamgames.com/doc/features/multiplayer/networking>
- ISteamNetworking: <https://partner.steamgames.com/doc/api/ISteamNetworking>

tModLoader (official source / wiki):

- Repository, branch 1.4.4: <https://github.com/tModLoader/tModLoader/tree/1.4.4>
- `patches/tModLoader/Terraria/Social/Steam/SteamedWraps.cs`
- `patches/tModLoader/Terraria/Social/Steam/WorkshopHelper.TML.cs`
- `patches/tModLoader/Terraria/Social/Steam/WorkshopSocialModule.TML.cs`
- `patches/tModLoader/Terraria/ModLoader/Core/ModCompile.cs` (references)
- `patches/tModLoader/Terraria/ModLoader/Core/AssemblyManager.cs` (Steamworks.NET redirect)
- `patches/tModLoader/Terraria/ModLoader/ModPacket.cs`
- `patches/tModLoader/Terraria/ModLoader/ModSystem.cs`
- `patches/tModLoader/Terraria/ModLoader/MenuLoader.cs`, `ModMenu.cs`
- `patches/tModLoader/Terraria/WorldGen.cs.patch` (Save & Quit + OnWorldUnload)
- `patches/tModLoader/Terraria/Main.cs.patch` / decompiled installed `tModLoader.dll` (`OnSubmitServerPassword`, `AutoHost`, `ReleaseHostAndPlayProcess`)
- `patches/tModLoader/Terraria/Social/Steam/NetClientSocialModule.cs.patch` / `NetServerSocialModule.cs` (legacy P2P, lobby flags)
- `patches/tModLoader/Terraria/ModLoader/UI/Interface.cs` (internal menu buttons)
- Mod Upload Rules: `ModUploadRules.md` in the tModLoader repository
- Wiki: Basic Netcode, Intermediate Netcode, Saving and loading using TagCompound, Basic UI Element, Advanced guide to custom UI
- API docs: <https://docs.tmodloader.net/docs/stable/>
