# Changelog

## 0.8.9

- Added smart drop-zone planning for compatible stockpiles.
- Fixed crowded stockpiles being favored too strongly when another valid zone has open space.
- Stackable items now prefer valid existing stacks before using new tiles.
- Batch planning now reserves chosen stack and tile destinations to reduce repeated bad picks.
- Added `[V2DropPlan]` support-log entries for destination choices.
- No install changes from 0.8.8.

## 0.8.8

- Fixed stackable asteroid ore and regolith being allowed into drag hauling.
- Stackable non-bulky inventory items now stay on the normal inventory pickup path.
- Kept crates, dollies, furniture, and other bulky items on the existing drag path.
- No install changes from 0.8.7.

## 0.8.7

- Added a mod snapshot to BIG support logs.
- Logged loaded BepInEx plugin GUIDs, names, versions, and DLL locations.
- Logged BepInEx dependency errors.
- Logged raw files in `BepInEx\plugins`, including disabled or renamed DLL files.
- No intended hauling behavior changes.

## 0.8.6

- Added built-in BIG support logging.
- Copied normal BIG hauling action logs into a dated support log.
- Added dated support zip creation on clean game shutdown.
- Added startup zipping for loose logs left behind after crashes.
- No intended hauling behavior changes.

## 0.8.5

- Rebuilt the public release around the newer V2 hauling planner.
- Replaced the old backpack-only capacity check with full carried-container planning.
- Fixed planning so marked crates and dollies can be used as helper storage.
- Fixed helper-container runs so loose pickup planning continues after the container is grabbed.
- Improved loose pickup ordering with path-aware sorting.
- Improved full-inventory handling by stopping the batch when planned storage is full.
- Kept the vanilla haul lifecycle in control of icons, pickup actions, drop actions, and task cleanup.
- Changed the release DLL name to `OstranautsHaulingV2.dll`.
- Install note: remove old `OstranautsSmartHaulingFresh.dll` copies before installing this version.

## 0.7.0

- Rebased the mod on the stable 0.6.0 hauling/building behavior.
- Removed the later experimental smart-haul session/unload controller work.
- Removed LaunchControl dependency usage; this version only requires BepInEx.
- Kept the safer item filter changes for smart batching.
- Bottles/flasks are no longer treated as bulky by name alone, so normal inventory bottles can batch when the game says they fit.
- Containers are no longer blanket-skipped by condition alone; true drag/bulky items are still left to vanilla.
- Items with non-hand equipment slots are skipped by smart batching to avoid pulling equipped/slot-style items into mass haul batches.

## 0.6.0

- Raised hauling and build/install batch limits to 200.
- Added a planned carry capacity check for hauling so the mod stops adding extra pickup jobs when inventory space is already reserved.
- Added a planned stack budget check for build/install material fetching so a batch does not reserve more than a stack can hold.
- Added dolly-aware construction fetching for dragged dollies that can fit bulky build materials.
- Improved build-fetch logging with capacity skip counts.
- Note: local wall stack-size data edits are not part of this code mod release.

## 0.5.0

- Raised the hauling batch limit to 80 tasks per pass.
- Added a stricter carry filter based on runtime item data.
- Normal inventory items are now treated as safe loose hauling, even when they do not stack.
- Drag-style and bulky items are skipped and left for vanilla hauling later.
- Skipped items include crates, walls, hull pieces, doors, pumps, regulators, coolers, canisters, bottles, containers, oversized items, cumbersome items, and most direct drag-slot items.
- Added better diagnostic logging for stack limits and item slot effects.

## 0.4.0

- Added build/install material batching.
- Crew can now pick up extra matching loose construction materials before feeding the first build/install action.
- Building material pickup is capped at 80 extra items per fetch pass.
- This helps bulk floor, wall, hull, and similar install jobs avoid repeated one-item trips.
- Kept hauling behavior separate from building behavior, so the clean haul queue compactor remains unchanged.
- Removed temporary build-fetch probe logging from the release build.

## 0.3.0

- Reworked hauling around a vanilla queue compactor.
- The mod now lets Ostranauts create and own the haul jobs first, then rearranges the queue to reduce repeated walking.
- Fixed the persistent haul icon problem caused by manually moving extra items outside the normal vanilla task flow.
- Added support for batching mixed haul jobs going to the same destination ship.
- Each item keeps its own vanilla drop action, so different drop tiles inside the same ship can still be used.
- Removed the old manual task completion and direct task removal logic.

## 0.2.0

- Tested custom bulk pickup behavior.
- Increased batch pickup behavior for marked loose items.
- This version could leave haul icons stuck because extra items were moved outside the full vanilla haul lifecycle.

## 0.1.0

- Fresh restart of the hauling mod.
- Removed the custom selection takeover approach.
- Added a simple first-pass batcher for marked loose items.
