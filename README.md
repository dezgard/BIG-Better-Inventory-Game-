# Ostranauts Simple Smart Hauling

By Dezgard

Simple Smart Hauling reduces one-item hauling trips without replacing Ostranauts' normal job system.

The game still creates the haul jobs, haul icons, pickup actions, drop actions, and task cleanup. This mod waits for those vanilla jobs, then compacts safe haul work so the crew member can collect more items before walking back to unload.

Version 0.8.17 uses the rebuilt V2 hauling planner. It checks real container space from backpacks, hand-held containers, carried containers, and dragged containers.

If the crew member already has usable storage equipped, carried, or dragged, the mod can fill that storage during loose-item hauling.

Drag-heavy hauling is handled conservatively. Loose inventory items are batched first. Drag items are kept separate from loose batching for stability.

When a usable container is already in the drag slot, BIG treats it as the active helper and keeps other drag jobs out of the loose-item pass.

While filling an active helper, BIG lets the helper decide what can fit instead of blocking bulky loose items too early.

When the active helper can no longer take loose haul items, BIG drops/releases it before returning to normal drag hauling.

If loose hauling is exhausted, BIG can hand off one drag item cleanly instead of trying to mix drag work into the loose-item batch.

Drop-off planning now checks compatible stockpile zones before unloading. Stackable items prefer valid stacks, then usable open zone space.

The mod also creates support logs. If something goes wrong, close the game and upload the newest zip from `BepInEx\BIGSupportLogs`.

## Features

- Uses vanilla haul jobs.
- Keeps vanilla haul icons.
- Keeps vanilla task cleanup.
- Batches loose inventory items.
- Uses real container space.
- Uses backpack storage.
- Uses hand-held containers.
- Uses carried containers.
- Uses dragged container storage.
- Locks the active dragged helper.
- Prioritizes the active dragged helper.
- Releases the helper before drag hauling.
- Uses controlled drag fallback.
- Picks loose items first.
- Can fill attached helper storage.
- Does not auto-grab helper containers.
- Keeps drag items separate.
- Avoids mixing drag items into loose batches.
- Lets helper storage accept bulky loose items.
- Keeps stackable ore in inventory hauling.
- Sorts pickups by path.
- Stops when storage is full.
- Keeps vanilla drop actions.
- Supports mixed haul items.
- Limits hauling to 200 tasks per pass.
- Scores drop zones.
- Prefers valid stacks.
- Reserves planned stacks.
- Avoids crowded zones.
- Creates support log zips.
- Lists installed BepInEx plugins.
- Lists disabled plugin files.
- Keeps recent support logs only.

## Requirements

- Ostranauts
- BepInEx 5 x64
- Requires C#

## Install

1. Install BepInEx 5 for Ostranauts.
2. Remove old `OstranautsSmartHaulingFresh.dll` versions if present.
3. Put `OstranautsHaulingV2.dll` in:

```text
Ostranauts\BepInEx\plugins\
```

4. Restart the game.

When loaded, the BepInEx log should show:

```text
Ostranauts Hauling V2 0.8.17 loaded.
```

## Support Logs

Support logs are written to:

```text
Ostranauts\BepInEx\BIGSupportLogs\
```

Each game session creates a dated `BIG-*.log` and a matching `BIG-*.zip`.

If the game crashes before the zip is created, start the game once more. The mod will zip loose BIG logs on startup.

The folder keeps the two newest previous support logs and removes older BIG support logs on startup.

The zip includes:

- BIG hauling actions.
- BIG startup and shutdown info.
- Loaded BepInEx plugins.
- BepInEx dependency errors.
- Raw files in `BepInEx\plugins`.
- Drop destination choices.

## Build From Source

This project targets `.NET Framework 4.7.2` and references the local Ostranauts install. If your game is not installed at `G:\Steam\steamapps\common\Ostranauts`, update `GameDir` in `OstranautsSmartHaulingFresh.csproj`.

```powershell
dotnet build -c Release
```

The built DLL will be in:

```text
bin\Release\net472\OstranautsHaulingV2.dll
```

## Notes

Ostranauts is still in Early Access, so game updates can change the hauling code this mod patches. If hauling starts behaving strangely after a game update, check for a newer mod version.
