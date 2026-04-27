# Ostranauts Simple Smart Hauling

By Dezgard

Simple Smart Hauling reduces one-item hauling and building trips without replacing Ostranauts' normal job system.

The game still creates the haul jobs, haul icons, pickup actions, drop actions, and task cleanup. This mod waits for those vanilla jobs, then compacts safe inventory-haul jobs so the crew member can pick up more loose inventory items before walking back to unload.

Drag-style and bulky items are intentionally left to the base game. That means crates, walls, hull pieces, doors, pumps, regulators, coolers, canisters, bottles, containers, oversized items, and cumbersome items are skipped by the smart batcher and handled later by vanilla hauling. The goal is to speed up normal loose item hauling while avoiding the broken behavior caused by trying to drag several bulky objects at once.

For building and installing, the mod also lets crew grab extra matching loose materials before feeding the first construction job. This reduces repeated trips when placing compatible stackable materials such as floors.

## Features

- Uses the game's normal haul marking and work system.
- Keeps vanilla haul icons and vanilla task cleanup.
- Batches safe loose inventory items into fewer trips.
- Skips drag/bulky items so vanilla handles them normally later.
- Supports mixed safe inventory items going to the same ship.
- Keeps each item's own vanilla drop action, so storage zones can still choose their own drop tiles.
- Adds build/install material fetching for compatible loose materials.
- Hauling batch limit: 80 tasks per pass.
- Building material fetch limit: 80 extra items per pass.

## Requirements

- Ostranauts
- BepInEx 5 x64
- Requires C#

## Install

1. Install BepInEx 5 for Ostranauts.
2. Put `OstranautsSmartHaulingFresh.dll` in:

```text
Ostranauts\BepInEx\plugins\
```

3. Restart the game.

When loaded, the BepInEx log should show:

```text
Ostranauts Simple Smart Hauling loaded.
```

## Build From Source

This project targets `.NET Framework 4.7.2` and references the local Ostranauts install. If your game is not installed at `G:\Steam\steamapps\common\Ostranauts`, update `GameDir` in `OstranautsSmartHaulingFresh.csproj`.

```powershell
dotnet build -c Release
```

The built DLL will be in:

```text
bin\Release\net472\
```

## Notes

Ostranauts is still in Early Access, so game updates can change the hauling code this mod patches. If hauling starts behaving strangely after a game update, check for a newer mod version.
