# Decision log

Running log of design decisions, findings, and changes from the original plan. Newest entries are appended.

---

## D-001 — Baseline: tModLoader 1.4.4 / v2026.08.3.0 stable

**Decision:** Develop against the installed tModLoader v2026.08.3.0 stable (1.4.4 branch), .NET 8, Steamworks.NET 2025.162.4. CI will pin the matching `tModLoader.zip` release and also be able to build against a configurable version.

**Why:** This is the version actually installed for testing. Building against an arbitrary newer release risks using APIs that are not in the player's build; tModLoader refuses mods built for a different major/minor version.

**Consequence:** A future tModLoader update will require a rebuild/release. Avoid internal APIs where possible.

---

## D-002 — Per-account Workshop items, FriendsOnly

**Decision:** Each Steam account gets its own Workshop item per shared world. Items are created as `k_EWorkshopFileTypeCommunity`, visibility `FriendsOnly`, title `Shared World: <name>`, description explicitly saying it is storage, not a mod. No public items.

**Why:** Only the owner can `StartItemUpdate` an item; contributors are credit-only. Private items cannot be read by other accounts. FriendsOnly is the only visibility in the allowed set that other participants can query/download.

**Consequences:**
- Participants must be Steam friends with each other (or at least with each item owner) to read that item.
- Removing a friendship removes access to that account's latest snapshot.
- The item is still visible to friends on the Steam Community Workshop page; keep it clearly labelled and non-executable.
- If tModLoader/Valve removes such items, peer transfer and local copies remain the fallback.

---

## D-003 — Snapshot package and manifest sidecar

**Decision:** A snapshot folder contains:

```
manifest.json
world.wld
world.twld
```

`manifest.json` is authoritative for version history; it is **not** stored inside `.twld` (avoiding a hash/manifest circularity). The `.twld` stores only the stable `SharedWorldId` (GUID) via `ModSystem.SaveWorldData`, so a world can identify itself if the local registry is lost.

**Content hash:** `SHA-256( SHA-256(world.wld bytes) || SHA-256(world.twld bytes) )`, lowercase hex. This is deterministic, covers both files, and is independent of file names.

**Why:** The world file and mod data are separate; a sidecar lets us version and hash exactly the bytes that are stored, without modifying the world format. `worldId` in `.twld` survives copies/renames.

**Consequence:** The manifest is duplicated on Workshop (content + metadata summary). Metadata is only a query hint; the content manifest is verified.

---

## D-004 — One writer at a time; version DAG + fork detection

**Decision:** Every save that changes the world produces a new version record:

```
{ version, hash, parentHash, lastHostSteamId, timestamp, worldName, mods, participants }
```

`version = max(all known heads) + 1` (Lamport-style), but a fork can still produce two versions with the same number. The hash is the identity.

Heads are compared as a DAG:

- If candidate is a descendant of local → fast-forward (auto-download).
- If local is a descendant of candidate → keep local.
- If they are unrelated → **fork**: show both versions, never auto-overwrite; let the group pick one, and keep the other as a backup.
- If two remote items have different hashes and neither descends from the other → fork warning.

**Why:** Terraria cannot merge worlds. A silent "highest version wins" rule would lose one branch's progress. The hash/parent chain makes divergence explicit.

**Consequence:** Version numbers alone are never used to overwrite. The UI must make forks understandable and must not delete the losing branch.

---

## D-005 — Upload at authoritative world unload, with pending retry

**Decision:** Build and upload a snapshot in `ModSystem.OnWorldUnload()` **only when** `Main.netMode == NetModeID.SinglePlayer` or `Main.netMode == NetModeID.Server`. If the content hash is unchanged, skip. If Steam/Workshop is unavailable, keep the snapshot + metadata in a local pending queue and retry from the Shared Worlds screen / next launch.

**Why:** `OnWorldUnload` is the verified hook that runs after `WorldFile.SaveWorld()` on both single-player Save & Quit and Host & Play server shutdown. A multiplayer client is not authoritative and must never upload.

**Consequence:** The upload is synchronous and bounded (timeout + progress) because the server process exits immediately after unload. A failed upload cannot lose the local world; it only delays availability. The UI must say "saved locally, upload pending" when this happens.

---

## D-006 — Host & Play automation via the game's own private launcher, with fallback

**Decision:** "Host with friends" sets the active world and calls the private `Main.OnSubmitServerPassword()` by reflection. This is the exact code path the vanilla Host & Play button uses: it spawns `tModLoader.dll -server ...`, `NetClientSocialModule.LaunchLocalServer` appends `-steam -localsteamid ... -lobby friends`, and the client connects to localhost.

Fallback if reflection fails: open the normal Host & Play settings screen (`Main.menuMode = 889`) with the world selected and show an explanatory message.

**Why:** Host & Play is a separate process, not an in-process mode. Replicating the argument construction is more fragile than calling the game's own routine.

**Consequence:** This is a private-API dependency and must be isolated + tested after tModLoader updates. The host client is a normal multiplayer client; the server process owns the world and performs the snapshot upload.

---

## D-007 — Main-menu access workaround

**Decision:** A client-only `ModSystem` appends a small "Shared Worlds" button to `MenuLoader.CurrentMenu.UserInterface` while on the title screen, and opens a full-screen `UIState` through `Main.MenuUI` (`menuMode = 888`). A configurable keybind (default `F9`) is a fallback.

**Why:** tModLoader has no public hook to add a vanilla main-menu button (`Interface` and `MenuLoader.Add` are internal). `ModMenu.UserInterface` is public and is updated/drawn by tModLoader for the active theme, so appending one element is the least invasive public-API solution and avoids Harmony-patching `Main.DrawMenu`.

**Consequence:** This is unusual and could conflict with other menu mods. The button is re-attached after theme changes, removed while another full-screen state is open, and can be disabled in config. If it breaks, the keybind still works.

---

## D-008 — Peer transfer is a verified secondary layer, not a blocking quit gate (v1)

**Decision:** Implement chunked snapshot transfer over `ModPacket` (16 KB chunks, Brotli, SHA-256 verification, temp file, atomic install). The server can broadcast a snapshot on request ("Sync to everyone") and when a snapshot is published. Full "host cannot quit until every client ACKs" is a stretch goal; v1 will show per-client ACK status and rely on the Workshop upload for durability.

**Why:** A hard quit gate is dangerous: a crashed/stuck client could trap the host. The Workshop upload already guarantees the latest version is available after the host quits. ACK tracking gives feedback without making the session unquittable.

**Consequence:** A client that disconnects during transfer simply doesn't get that snapshot; it can download it from the Workshop before its next session, or receive it next time. The UI must be honest about this.

---

## D-009 — Steam actions are isolated and user-visible

**Decision:** Put all Steamworks calls behind `WorkshopService`/`SteamUgc` wrappers. Never call Steam APIs during background autosaves. Require an explicit "Enable Workshop storage" action per world (the registration flow), show the Workshop Legal Agreement requirement, and rate-limit uploads to once per changed session.

**Why:** tModLoader's Mod Upload Rules (including private/friends-only content) prohibit Steam API abuse and unexpected behaviour. The mod should look and behave like a game feature, not a background uploader.

**Consequence:** If Steam is unavailable or the EULA is not accepted, the mod remains fully usable with local files and peer transfer; Workshop status is clearly shown as unavailable/pending.

---

## D-010 — Storage locations stay inside tModLoader's save directory

**Decision:** All registries, snapshots, backups, temp files and pending uploads live under `Main.SavePath/SharedWorlds/`. Workshop item content folders are also built there. We do not write outside `Main.SavePath`/`Main.WorldPath`. World files are only swapped inside `Main.WorldPath`.

**Why:** tModLoader Mod Upload Rules Rule 2 and general safety. It also makes backups easy for users to find.

---

## D-011 — License and repository

**Decision:** MIT license. Repository contains only code/docs (no runtime dependency). GitHub Pages is built from `/docs`. GitHub Actions compiles on push and attaches the `.tmod` to tagged releases.

**Why:** Matches the project brief; keeps distribution simple and avoids an external backend.

---

## Open items to verify in-game (not blockers to coding)

1. Friends-only item query/subscribe/download across two real Steam accounts.
2. Whether tModLoader's Workshop UI or startup checks produce warnings for a subscribed item with no `.tmod`.
3. Workshop propagation delay after `SubmitItemUpdate` (measure metadata visibility).
4. Reflection stability of `Main.OnSubmitServerPassword` on the installed build.
5. Main-menu `MenuLoader.CurrentMenu.UserInterface` injection behaviour with the default tML theme and with the vanilla theme.
6. Actual upload/download throughput and whether synchronous upload at server shutdown is acceptable.
7. `Main.OnWorldUnload` ordering when the user closes the window rather than using Save & Quit.

---

## D-012 — Item discovery through friend-published queries

**Decision:** Store participant Steam IDs even before they have an item (`itemId = 0`). During sync, if a participant's item is missing/unknown, query that friend's **published** Workshop items via `CreateQueryUserUGCRequest(k_EUserUGCList_Published, …)` and match `WorkshopMetadata.worldId`. Also do this for the current account if its own item ID was lost.

**Why:** A participant can join before creating their item, or can delete and recreate their item. Without discovery, the group would never learn the new item ID, and the newest snapshot could become invisible. tModLoader uses the same author query for friends-only mods.

**Consequence:** Discovery costs one or more extra Workshop queries per sync (bounded to 20 participants). It only works while the accounts are Steam friends. The manifest/metadata still carries known IDs for the normal path.

---

## D-013 — Fast-forward only after inspecting the downloaded manifest

**Decision:** Version metadata is a query hint, not proof of ancestry. If the published head is not demonstrably a descendant of the local head, download that item, verify its hash, read its full `manifest.json` history, and only then decide fast-forward vs fork. Never install the snapshot until the manifest proves the local hash is an ancestor (or the user explicitly chooses it in the fork UI).

**Why:** Metadata only contains the latest version. A legitimate fast-forward can have several intermediate versions whose hashes are not in local metadata. Without inspecting the manifest, the mod would raise false forks constantly.

**Consequence:** Some syncs download before they can say "fork"; that is acceptable because no local file is changed until the check passes. The downloaded content stays in Steam's cache if rejected.

---

## D-014 — Fork resolution is explicit and backed up

**Decision:** A fork report lists the heads. The detail screen offers "Use remote vN" (two-click confirmation). It downloads and installs that item with `allowDowngrade: true`; `SnapshotService.Install` backs up the current local world first. The losing branch is never deleted.

**Why:** Terraria cannot merge world files. The only safe resolution is an explicit user choice with a preserved copy of the other branch.

**Consequence:** The UI must keep the "keep local" path trivial (just don't press the button). Version history and backups remain available after resolution.

---

## D-015 — tModLoader baseline moved to 2026.09.3.1

**Decision:** Update the documented/tested baseline from 2026.08.3.0 to **2026.09.3.1** (stable, 2026.09) after Steam updated the test install during development. The mod built for both and loaded on both. CI pins the matching `tModLoader.zip` GitHub release.

**Why:** The test machine is the source of truth for the current version. The 2026.09 release also contains the message-frame length fix that matters for large ModPackets.

**Consequence:** A future stable update still requires a rebuild/release. The code avoids unstable internals except `Main.OnSubmitServerPassword`, which is isolated and has a fallback.

---

## D-016 — Server-side snapshot upload is synchronous at unload; manual/peer upload is asynchronous

**Decision:** At authoritative world unload (single-player or server), build the snapshot and attempt the Workshop upload with a bounded synchronous wait so the process does not exit before the item is committed. If it fails/times out, keep the snapshot pending. In-session "Sync & publish" requests run the Workshop upload on a background task and broadcast the already-built snapshot immediately.

**Why:** The Host & Play server process exits immediately after `OnWorldUnload`; an async upload there would be killed. In-session uploads must not freeze the game.

**Consequence:** A very slow connection can delay the host's exit screen by up to the configured timeout (default 180s) before falling back to pending. The UI/config documents this. Pending snapshots retry from the Shared Worlds screen or the next unload.

---

## D-017 — Peer transfer is opt-in and does not gate quitting (v1)

**Decision:** The in-game panel offers "Sync & publish", which asks the server to save, build, upload, and chunk the snapshot to all connected players. It tracks ACKs. It does not block the host from quitting if a client fails. The Workshop upload is the durability guarantee.

**Why:** A hard gate can trap the host behind a stuck client. The value of peer transfer is faster propagation and a second copy, not a replacement for the Workshop layer.

**Consequence:** "The host can't finish quitting until everyone confirms" from the original plan is **not** implemented as a hard block in v1. This is recorded as a deliberate adaptation; a future version can add an explicit "wait for all ACKs" mode for groups that want it.

---

## D-018 — No hard dependency on a GitHub repo or external service

**Decision:** README, Pages and CI are for distribution/development only. The mod reads only local tML save data and Steam APIs. No HTTP calls, no telemetry, no backend.

**Why:** Project constraint and tModLoader Mod Upload Rules.

---

## D-019 — Authoritative-side detection uses the mode captured at world load

**Decision:** Record whether the world was loaded as a multiplayer client in `OnWorldLoad`/`NetReceive`. `OnWorldUnload` uses that flag, not the current `Main.netMode`, to decide whether to snapshot/upload.

**Why:** Terraria resets `Main.netMode` to 0 during a multiplayer client disconnect **before** calling `OnWorldUnload`. Checking the current value would make a client look like single-player and risk uploading its non-authoritative copy. The server process is netMode 2 and single-player is 0, so both remain authoritative.

**Consequence:** A client never snapshots or uploads. The Host & Play server process does, after its own save.

---

## D-020 — Workshop uploads are serialized; stale uploads are skipped

**Decision:** A static upload gate allows one Workshop upload at a time. After acquiring it, the mod refreshes the world's registry entry and skips the upload if the local version is newer than the snapshot being uploaded.

**Why:** A slow background "sync & publish" upload can overlap a session-end upload. Without serialization/ordering, the older upload could commit last and leave the item on an older version.

**Consequence:** A very slow upload may delay a later one, but versions cannot move backwards on the item. The newer snapshot is kept pending/uploaded next.

---

## D-021 — Stale-copy warning on single-player load

**Decision:** When a registered world is loaded in single-player, the mod asynchronously checks known items (metadata only, no download) and shows an in-game warning if a newer or diverged version is known, or if a previous upload is pending. It does not block the load.

**Why:** The primary entry point is the Shared Worlds screen, but players can still double-click a world in the normal list. A warning plus fork detection is safer than silently allowing an avoidable fork.

**Consequence:** The check is best-effort (Steam/network may be unavailable) and the message tells the player to save & exit and sync. It is not a hard gate, so a player can still choose to create a fork deliberately.

---

## D-022 — Registry saves merge on-disk state under a cross-process lock

**Decision:** The registry write path acquires a short exclusive lock (`registry.json.lock`), re-reads the on-disk registry, merges participants/version history/local heads, then writes atomically. The Host & Play server and the host client are separate processes sharing the same save directory.

**Why:** Without merging, the server's shutdown write could drop a client-side participant update, or a client write could hide the server's new snapshot. The lock prevents torn read-modify-write cycles; the merge keeps both sides' data.

**Consequence:** The merge is intentionally conservative: it never lowers a local version, unions history by hash, and prefers nonzero participant item IDs. It is not a distributed consensus protocol; the group's single-writer-at-a-time rule is still what prevents forks.

---

## D-023 — Menu-only work uses `PostUpdateInput`, not `PostUpdateEverything`

**Decision:** Attach the main-menu button, watch for the menu F9 hotkey, and run queued Open-to-friends/Host & Play continuation from `ModSystem.PostUpdateInput`. `SharedWorldsService.Tick()` is also called there when `Main.gameMenu` is true; in-game updates remain in `PostUpdateEverything`.

**Why:** tModLoader 2026.09.3.1's `Main.Update` returns early on the title screen (`gameMenu && netMode != 2`) before `SystemLoader.PostUpdateEverything()`. Menu code placed there never runs. `PostUpdateInput` runs in both menus and gameplay. Reading a `ModKeybind` before input triggers are initialized (while mods are loading at the menu) throws `KeyNotFoundException`, so the in-game keybind is now read only when `!Main.gameMenu`.

**Consequence:** Any future menu-only feature must use a menu hook; `PostUpdateEverything` is gameplay-only for this purpose. The main menu button is attached to `MenuLoader.CurrentMenu.UserInterface`, which is drawn by tModLoader over the title screen, and F9-at-menu is a direct `Main.keyState` edge check.

---

## D-024 — Workshop uploads never block the thread that pumps Steam callbacks

**Decision:** Snapshot building remains synchronous, but uploads are asynchronous. Single-player/Host-client unloads start a background task; the menu/game loop pumps Steam callbacks via `WorkshopService.PumpCallbacks()`. Authoritative server shutdown blocks, but a helper thread runs the upload and pumps callbacks while the main thread waits (bounded by the upload timeout). The registry is marked pending **before** the upload starts and cleared only on success.

**Why:** Steamworks `CallResult` callbacks are dispatched by `SteamAPI.RunCallbacks()`. `OnWorldUnload` originally waited for the upload on the main thread, so the callbacks could never run and the server deadlocked at "Saving before exit...". Marking pending first also makes a process exit or crash retryable instead of silently losing the upload.

**Consequence:** A normal save/exit returns to the title screen immediately; publishing finishes in the background and the UI refreshes. A Host & Play server can take up to the configured upload timeout to exit (tModLoader's watchdog logs a warning after 10s). If Steam requires the Workshop Legal Agreement, the result stays pending and the world record carries `PendingNeedsEula` for the UI.

---

## D-025 — EULA is a per-account user action, surfaced not automated

**Decision:** The server upload result's `NeedsEula` flag is stored on the pending record. The world list/detail shows **EULA needed** and tells the user to accept it in the Steam client; the mod does not open the overlay or add a permanent EULA button. The one-time user action is: open the app-specific `workshoplegalagreement` page for tModLoader (or the Steam overlay), accept **both** the Steam Subscriber Agreement and the Workshop Legal Agreement, then **Publish pending**. The mod never tries to accept the agreement itself.

**Why:** The Workshop Legal Agreement is a legal acceptance tied to a Steam account, not an API state the mod can click through. A permanent EULA button is noise for the 99% of sessions after the one-time acceptance; auto-opening the overlay is intrusive. The server process has no overlay anyway.

**Consequence:** A newly created item can be fully uploaded but remain pending/invisible to friends until the owner accepts once. The snapshot and item ID are retained; **Publish pending** retries after acceptance. This is a release gate for the two-account test. The app-specific page has two agreements, and accepting only the Subscriber Agreement is not enough.

---

## D-026 — Pending upload is a timestamped transition, not a monotonic flag

**Decision:** `SharedWorldRecord` stores `PendingUpdatedUtc`. `QueuePending`, `MarkPending`, successful clears, peer-transfer clears, and retries all update it. The registry merge applies pending fields from whichever copy has the newer timestamp.

**Why:** The Host & Play client and server share the registry file. The previous merge preserved any on-disk pending flag when the in-memory copy had cleared it, so a completed upload could be marked pending again by the next save. That made the UI show a false pending state and could trigger a redundant re-upload. `PendingNeedsEula` was also missing from the copy path entirely.

**Consequence:** The latest pending transition wins across processes. A stale client can still contribute participants/history, but it cannot resurrect a pending upload the server has completed, and a server can still publish a pending update from the client.

---

## D-027 — Every remote head is an explicit fork choice

**Decision:** When sync finds more than one head, the detail screen creates one confirmed “Use vN short-hash” button per remote head that has a Workshop item ID, instead of offering only `BestRemote`.

**Why:** A version snapshot is not a merge. If Alice and Bob both branched from v3, choosing only the “highest” v4 could silently discard the other branch's changes. Showing each head with its hash, host, and item ID makes the decision explicit.

**Consequence:** Up to three remote heads are shown directly; further heads remain listed in the text. Installing any choice backs up the current world first, and the other branches remain on their owners' items.
