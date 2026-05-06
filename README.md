# Ostranauts Simple Smart Hauling

By Dezgard

Simple Smart Hauling reduces one-item hauling trips without replacing Ostranauts' normal job system.

The game still creates the haul jobs, haul icons, pickup actions, drop actions, and task cleanup. This mod waits for those vanilla jobs, then compacts safe haul work so the crew member can collect more items before walking back to unload.

Version 0.8.8 uses the rebuilt V2 hauling planner. It checks real container space from backpacks, hand-held containers, carried containers, and dragged containers.

If a marked crate, dolly, or storage container can help carry the job, the mod can grab it first. It then keeps collecting loose items if that container has room.

Drag-heavy hauling is handled conservatively. Loose inventory items are picked first. Drag items are kept to one helper container or one normal dragged item when possible.

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
- Picks loose items first.
- Can grab a helper crate first.
- Can fill a helper container.
- Can include one drag item.
- Avoids multiple drag items.
- Keeps stackable ore in inventory hauling.
- Sorts pickups by path.
- Stops when storage is full.
- Keeps vanilla drop actions.
- Supports mixed haul items.
- Limits hauling to 200 tasks per pass.
- Creates support log zips.
- Lists installed BepInEx plugins.
- Lists disabled plugin files.

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
Ostranauts Hauling V2 0.8.8 loaded.
```

## Support Logs

Support logs are written to:

```text
Ostranauts\BepInEx\BIGSupportLogs\
```

Each game session creates a dated `BIG-*.log` and a matching `BIG-*.zip`.

If the game crashes before the zip is created, start the game once more. The mod will zip loose BIG logs on startup.

The zip includes:

- BIG hauling actions.
- BIG startup and shutdown info.
- Loaded BepInEx plugins.
- BepInEx dependency errors.
- Raw files in `BepInEx\plugins`.

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
