# Changelog

All notable changes to Shared Worlds are documented here. The format follows Keep a Changelog; versions use SemVer.

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
