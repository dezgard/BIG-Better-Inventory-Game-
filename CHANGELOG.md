# Changelog

## 0.8.5

- Switched the release to the V2 hauling plugin.
- Replaced the old single-backpack planner.
- Added real multi-container planning.
- Counts backpack storage.
- Counts hand-held containers.
- Counts carried containers.
- Counts dragged container storage.
- Can use a marked crate as helper storage.
- Can use a marked dolly as helper storage.
- Keeps collecting loose items after grabbing a helper container.
- Sorts loose pickups with path-aware ordering.
- Stops planning when storage is full.
- Keeps vanilla haul icons.
- Keeps vanilla pickup actions.
- Keeps vanilla drop actions.
- Keeps vanilla task cleanup.
- Released DLL is now `OstranautsHaulingV2.dll`.

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
