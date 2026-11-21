# Changelog

All notable changes to Shared Worlds are documented here. The format follows Keep a Changelog; versions use SemVer.

## [0.2.0] - 2026-11-21

### Added

- A real **Play Shared Worlds** row in Terraria's multiplayer menu, injected through a narrow `Main.DrawMenu` IL hook with the existing main-menu button as a fallback.
- A clean Play screen: live/offline state, host name, online player count, last-saved timestamp, sync state, errors and a per-world Host/Join/Resolve action.
- Live host advertisement through Steam Rich Presence (`world`, `lobby`, `hash`, `version`, `players`, timestamp). Friends can join an active Host & Play lobby directly.
- Host-left recovery. Clients detect an unexpected disconnect, wait through a deterministic SteamID-ordered rehost offer, and show who is taking over. If nobody accepts, the client returns to the title screen, opens the Play screen and fetches the latest snapshot.
- Configurable Shared Worlds autosave upload (default 10 minutes) while playing or hosting. Every autosave updates the same per-account Workshop item and broadcasts the snapshot to connected clients.
- A Steam Rich Presence handoff marker so two clients cannot start competing hosts during recovery.
- Client snapshot requests on join, keeping a local recovery copy ready.
- Timestamp-first UI for world rows, details, history and fork choices. Internal version numbers remain for ancestry only.
- A full **vanilla-styled UI pass**: every screen (Play, Manage, world picker, details, host-disconnected recovery, the in-game F9 panel and the title-screen entry) now uses Terraria's own "fancy UI" design language — the same frame, palette, overlapping title tab, inner-panel tag chips, divider lines, hover menu-tick sounds and large corner buttons as the vanilla world/character select, driven by one shared design kit (`SwUI`).
- A **single-player guard**: registered shared worlds are dimmed in the vanilla world list (the same treatment vanilla gives Journey-mode mismatches) and clicking Play opens vanilla's rejection menu pointing at Play Shared Worlds. One hosted session at a time is how the mod keeps a single writer; solo play happens by hosting locally.
- **Navigation cleanup.** The floating title-screen button is gone (the multiplayer-menu row and the F9 hotkey are the entries); the Play screen is the single hub, Manage is reachable only from it and returns only to it, and the per-world Details views no longer cross-link. Manage's second corner button is now **Sync all** (check every registered world and pull the newest verified snapshot). The unreachable "Open to friends" conversion (dead since the single-player guard) was removed along with its plumbing, and the in-game F9 panel was simplified to session status, Sync & publish and Upload pending.
- **Layout hardening on the world cards.** Cards grew to a fixed 140px grid with strictly banded rows — name, divider, tag chips, host/participant line, action buttons — so buttons can no longer clip into the tag chips or the participants text. Tag chips now auto-crop their labels (a long status can never spill onto a neighbour), the long status strings were rewritten to fit, and Manage's status/feedback moved from the toolbar into the footer like the Play screen, so the toolbar keeps only a short static label next to Register/Import.
- **A Steam Workshop agreement notice.** The first Play Shared Worlds press, Register world and Import invite check the account's Workshop agreement status locally (`SteamUGC.GetWorkshopEULAStatus`). Until Steam reports both the Steam Subscriber Agreement and the Workshop Legal Agreement as accepted, a vanilla-styled notice explains what is needed and offers **Open the Steam page**, **Check again**, **Continue** (skip for now) and **Don't remind me again** (persisted in the mod config). The notice returns on every entry point until the agreements are accepted.

### Changed

- The Workshop item description and change note now show the snapshot timestamp instead of a version number.
- The Play screen is opened from the multiplayer menu; the existing Shared Worlds screen is now the setup/manage surface.

### Fixed

- Menu-row selection is mapped back to the vanilla handlers so the added row does not steal the Back action.
- `Recalculate`/UI text alignment on custom screens now uses explicit left alignment for left-hand labels.
- Autosave uploads are serialized with exit uploads so a newer local snapshot cannot be overwritten by an older background upload.

## [0.1.4] - 2026-11-20

### Fixed

- World-row action buttons (`Host`, `Sync`, `Invite`, `Info`) now use explicit right-aligned positions derived from the window width. The previous `HAlign` offsets could overlap the row text at 800x600.

## [0.1.3] - 2026-11-20

### Changed

- Removed the in-mod Workshop EULA opener (both the permanent screen button and the automatic overlay call). The one-time Steam agreement is handled in the Steam client/overlay; the mod only shows **EULA needed** and asks the user to accept it before publishing again.
- Redesigned the Shared Worlds, world picker, detail, and in-game panels: responsive sizing for 800x600 and up, cleaner headers/status bars, two-line world rows, readable version/host/item status, and less clipped text.
- Fork resolution now offers a separate confirmed choice for **each remote head**, not just one “best” remote.
- Opening the Shared Worlds screen checks a previously pending EULA status in the background and clears the warning if Steam reports it accepted.

### Fixed

- `PendingUpload` could be resurrected by the registry merge because pending was treated as a monotonic flag. Pending state now carries `PendingUpdatedUtc`; the last transition wins, so a completed upload can clear a stale pending record from the other process.
- `PendingNeedsEula` and `PendingUpdatedUtc` were not copied by the registry update path.
- A successful server-side upload could still leave `PendingUpload = true` because the pre-upload record was saved again after the upload cleared it. The registry now re-reads the completed record before the final save.
- Peer “Sync & publish” uploads now record the EULA-needed flag and clear pending on success like the normal path.
- Server upload helper threads are background threads; a timeout no longer leaves a foreground thread able to hold the process open indefinitely.

## [0.1.2] - 2026-11-20

### Fixed

- The "world unchanged, pending upload" path in `OnWorldUnload` still waited synchronously for Steam callbacks on the main thread. It now uses the same asynchronous single-player path and callback-pumping server path as new snapshots, so a save with no content changes cannot deadlock the server or the title-screen return.

## [0.1.1] - 2026-11-20

### Fixed

- Main-menu button and "Open to friends" continuation: `PostUpdateEverything` is not called on the title screen in tModLoader, so menu work now runs from `PostUpdateInput`.
- In-game `F9` no longer throws `KeyNotFoundException` while mods are still initializing at the menu; the keybind is only read in-game.
- Workshop uploads no longer block the Steam callback thread. Single-player uploads run in the background and the menu pumps callbacks; server-shutdown uploads use a helper thread that pumps callbacks while the main thread waits.
- Snapshots are marked pending before the upload starts, so an interrupted upload is retried instead of being lost.
- The main UI now fits an 800x720 window; fixed oversized panels and clipped buttons.
- `icon_small.png` is now the expected 30x30.

### Added

- Steam Workshop EULA status is kept on the pending record; the world detail screen offers an "Accept Workshop EULA" action and shows "EULA needed" on the list.
- Single-account end-to-end verification: item creation, friends-only upload, metadata query, download, hash verification, install, and version fast-forward (temporary Workshop item, deleted after testing).

### Changed

- Repository links and mod metadata now point at `Hiyzta/SharedWorlds`; version is `0.1.1`.
- CI now prepares the Linux tModLoader native libraries before building, so the GitHub Actions build and tagged `.tmod` release succeed.

### Known limitations

- Friends-only visibility to a second Steam account and the Steam overlay EULA flow still need a second logged-in account/manual check.
- No Steam Cloud worlds.
- No live single-player → multiplayer conversion.
- Peer transfer does not hard-block quitting.
- A server shutdown can wait up to the configured upload timeout (the watchdog logs a warning after 10s) while a snapshot uploads.

## [0.1.0] - 2026-11-18

### Added

- Initial tModLoader 1.4.4 mod scaffold and build setup.
- SharedWorldId persistence in `.twld` and a local JSON registry.
- Snapshot package (`world.wld`, `world.twld`, `manifest.json`) with SHA-256 content hashing.
- Per-account friends-only Steam Workshop items, created lazily on first publish.
- Compact Workshop metadata headers for version queries without content downloads.
- Participant item-ID exchange over `ModPacket`, plus discovery via friends' published items.
- Version DAG with fast-forward detection and explicit fork detection.
- Local backups and snapshot/backup pruning.
- Workshop upload at authoritative world unload (single-player or Host & Play server), with pending retry.
- Download-before-host, hash verification, atomic install, and fork refusal.
- Chunked peer transfer (16 KB `ModPacket` chunks, zip payload, transport hash, final verification, ACK).
- Shared Worlds main-menu screen, world picker, invite import, detail/fork view, in-game F9 panel.
- "Open to friends" save → title → Host & Play flow.
- Main-menu button injection with F9 fallback.
- Config entries for auto-upload, timeouts, backups, history, menu button, peer transfer.
- Unit test harness for version graph, invite codes, and hashing.
- Research and decision documentation; GitHub Pages site; CI/release workflows.

### Known limitations

- Two-account Steam Workshop flow and graphical UI still need manual verification.
- No Steam Cloud worlds.
- No live single-player → multiplayer conversion.
- Peer transfer does not hard-block quitting.
