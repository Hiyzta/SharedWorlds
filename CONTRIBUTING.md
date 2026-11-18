# Contributing

Shared Worlds is a small side project. Keep changes focused and safe.

## Before coding

1. Read `docs/RESEARCH.md` (especially the Steam Workshop and tModLoader findings).
2. Read `docs/DECISIONS.md` (the per-account item model, version DAG, fork rules, Host & Play).
3. Build against the tModLoader version you actually test with. `src/SharedWorlds/Local.props.example` shows the path setting.

## Rules that keep the mod shippable

- All Steam calls go through `Core/WorkshopService.cs`.
- All file writes stay inside the tModLoader save directory (`Main.SavePath` / `Main.WorldPath`).
- Never replace a world file without: SHA-256 verification, a backup, and an atomic move.
- Never resolve a fork automatically. Keep both branches until the user chooses.
- UI code is client-only. Storage/networking code must load on both sides.
- Test the server path as well as the client path. Most multiplayer bugs are side-of-the-wire bugs.
- Do not add a backend, telemetry, or external service.

## Build and test

```bash
cd src/SharedWorlds
dotnet build -c Release

cd ../../tests/SharedWorlds.Tests
dotnet run -c Release
```

Server smoke test from the README is strongly recommended after touching save/unload code.

## Pull requests

- Describe what changed, why, and what you tested.
- Include the tModLoader version and OS.
- Update `docs/DECISIONS.md` if you change an architectural decision.
- Add a CHANGELOG entry for user-visible changes.
