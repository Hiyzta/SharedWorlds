# Changelog

All notable changes to Shared Worlds are documented here. The format follows Keep a Changelog; versions use SemVer.

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
