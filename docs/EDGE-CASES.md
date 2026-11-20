# Edge cases and failure behavior

Shared Worlds cannot merge Terraria worlds and cannot change Steam's permission model. The goal is to make every failure **visible, recoverable, or impossible to do silently**. This page records the cases we designed around and what the mod does today.

Status legend:

- **Prevented** — the code makes the unsafe outcome impossible.
- **Mitigated** — the hazardous outcome is detected and requires an explicit decision / retry.
- **Not fixable** — an external constraint; documented and surfaced.

## Save and upload

| Case | Status | Behavior |
|---|---|---|
| Crash/power loss before save/unload | Not fixable | No snapshot from that session. If Terraria autosaved, the next load records it as a local head; the next normal exit can publish it, possibly as a branch. |
| Host force-killed or server not shut down gracefully | Not fixable | No `OnWorldUnload`, no upload. The last published version remains; local files and backups are unaffected. |
| Save with no content changes, pending upload exists | Prevented | The pending snapshot is retried through the non-blocking single-player path or the callback-pumping server path. |
| Upload interrupted by quitting immediately | Prevented | Registry is marked pending **before** the upload. Retry on next save/unload or **Publish pending**. |
| Steam offline/unavailable | Mitigated | Snapshot is kept locally and marked pending; other participants keep the last published version. |
| Upload timeout | Mitigated | Server exits after the bounded wait with pending recorded; helper threads are background threads and cannot hold the process open forever. |
| Successful upload still marked pending | Prevented | Pending is a timestamped state transition; stale client/server registry copies cannot resurrect a completed pending flag. |
| Pending/EULA fields lost on routine registry update | Prevented | `CopyInto` copies all pending fields, including `PendingNeedsEula` and `PendingUpdatedUtc`. |
| Local world changed outside the mod while an upload is pending | Prevented | The old pending snapshot is cleared if the local head moved to a different hash; it cannot be republished as if it were current. |
| Fast-forward installs a remote version while a local upload is pending | Prevented | The installed snapshot supersedes the pending branch and clears it. |
| Fork resolution installs a remote version while the local branch is pending | Prevented | The local branch is backed up and its pending upload is cleared; the old branch is not silently published later. |

## Versions and forks

| Case | Status | Behavior |
|---|---|---|
| Two people host from the same version | Mitigated | Both produce heads with the same parent; sync reports a fork and refuses to auto-install. |
| A host starts from an old version | Mitigated | Parent/hash ancestry exposes the branch even if its version number is higher. |
| Two branches share a version number | Mitigated | Hash + parent are identity; the version number is display only. |
| Same version number chosen for different content | Mitigated | Fork UI lists each head with hash, host, and Workshop item ID. |
| Clock skew between machines | Mitigated | Timestamps are tie-breakers/display; ancestry and hashes decide install safety. |
| Manifest history trimmed after many versions | Mitigated | The parent link is preserved; unknown ancestry is treated as unrelated/fork rather than guessed. |
| Remote metadata is stale after upload | Mitigated | Sync downloads and verifies the actual manifest/content; a mismatch refuses installation and retries. |
| Local file restored/edited outside the mod | Mitigated | Recorded as a new local head with the previous hash as parent; can become an explicit branch. |
| World name collision on download | Prevented | Install targets the record's filename; a different existing file is never overwritten by name. |

## Steam Workshop and accounts

| Case | Status | Behavior |
|---|---|---|
| One account wants to update another account's item | Not fixable | Steam owner-only writes. Each account publishes to its own item. |
| First publish needs the Workshop Legal Agreement | Mitigated | Item can be uploaded but stays pending/hidden. The mod shows **EULA needed**; the owner accepts both agreements in the Steam client. The mod does not open the overlay. |
| Latest version is on a hidden EULA-pending item | Mitigated | Owner sees the warning; other accounts may only see older items. Peer transfer can still carry the live session. |
| Owner deletes/bans their item | Mitigated | Treated as missing; other items/local copies remain; local world is never deleted because a remote item vanished. |
| Participants unfriend an owner | Mitigated | Friends-only access fails; the mod warns and other participants' items can still provide data. |
| Workshop propagation delay | Mitigated | Metadata query can lag; the mod verifies content and reports "waiting for Steam" rather than installing a mismatch. |
| Non-mod storage item in tModLoader Workshop | Not fixable | Policy risk. Items are friends-only, clearly labelled, contain no executable content, and peer transfer is a fallback. |

## Environment and limits

| Case | Status | Behavior |
|---|---|---|
| World + `.twld` over 64 MB uncompressed | Mitigated | Snapshot build refuses; local world untouched; no upload. |
| Workshop metadata over 5000 bytes | Prevented | Participant map is trimmed; full map stays in `manifest.json`. |
| Malformed/renamed snapshot archive | Prevented | Content hash, world ID, manifest paths, and zip-slip checks are validated before install. |
| Interrupted install | Prevented | Files are copied to temp, hash-verified, then atomically moved; failure restores the backup. |
| Different mods on different machines | Mitigated | Hashes differ and tModLoader blocks joining; the mod records the mod list in the manifest. |
| Dedicated server without a Steam client | Mitigated | No Workshop upload; snapshot stays local/pending. Host & Play is the supported path. |
| "Open to friends" transition | Mitigated | The single-player unload defers to the Host & Play server's shutdown upload. If the server fails to start, the save waits for the next normal save/unload. |
| Private Host & Play API changes | Mitigated | Reflection is isolated; failure falls back to the normal Host & Play screen. |
| Main menu hook availability | Prevented | Menu work uses `PostUpdateInput`; the button attaches to the active ModMenu UI and F9 is a fallback. |

## Deliberate limits

- No merging of diverged worlds. Terraria has no merge format; a fork is always a human choice.
- No always-on backend. A player must be online to host.
- No live single-player → multiplayer conversion. Host & Play starts a separate server.
- Peer transfer is best-effort; Workshop upload is the durability guarantee. A host is not trapped behind a stuck client.
