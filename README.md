# BIG / Better Inventory Game

By Dezgard

BIG improves hauling in Ostranauts by replacing the old vanilla-haul compactor with BIG-owned selection, markers, planning, and drop-off control.

Version 0.9.0 is the new BIG hauling rewrite. It is designed for large selections, including high-object-count ship cleanup jobs, without flooding vanilla hauling with overlapping tasks.

## Features

- BIG-owned haul selection.
- BIG-owned drag selection.
- BIG-owned item markers.
- Large drag-box selection support.
- Lower task churn on large cleanup jobs.
- Reduced lag from whole-ship haul selections.
- Smarter pickup ordering.
- Batched hauling runs.
- Stockpile-aware drop planning.
- Compatible stack targeting.
- Auto-task aware planner pause.
- Vanilla cancel and mode-switch safeguards.
- Cursor feedback for active BIG mode.
- Session logs for issue reports.

## Requirements

- Ostranauts
- BepInEx 5 x64
- Requires C#

## Install

1. Install BepInEx 5 for Ostranauts.
2. Remove old `BIGLooseHaulPrototype.dll` test builds if present.
3. Replace old `OstranautsHaulingV2.dll` with the new one.
4. Keep the `BIGAssets` folder next to the DLL:

```text
Ostranauts\BepInEx\plugins\OstranautsHaulingV2.dll
Ostranauts\BepInEx\plugins\BIGAssets\
```

5. Restart the game.

When loaded, the BepInEx log should show:

```text
BIG Better Inventory Game 0.9.0 loaded.
```

## Support Logs

BIG logs are written to:

```text
Ostranauts\BepInEx\BIGSupportLogs\
```

If reporting an issue, reproduce it, close the game, and upload the newest `BIG-*.log`.

## Build From Source

This project targets `.NET Framework 4.7.2` and references the local Ostranauts install. If your game is not installed at `G:\Steam\steamapps\common\Ostranauts`, update `GameDir` in `OstranautsSmartHaulingFresh.csproj`.

```powershell
dotnet build -c Release
```

The built DLL will be in:

```text
bin\Release\net472\OstranautsHaulingV2.dll
```
