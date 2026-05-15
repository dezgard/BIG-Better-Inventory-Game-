# Changelog

## 0.9.0

- Replaced the old vanilla-haul compactor with the new BIG-owned hauling rewrite.
- Added BIG-owned haul and drag selection modes.
- Added BIG-owned item markers and cursor feedback for active BIG mode.
- Improved large-area selection handling for high-object-count cleanup jobs.
- Reduced vanilla task churn when selecting large areas or whole ships for hauling.
- Added smarter pickup ordering and stockpile-aware drop planning.
- Added safeguards so BIG mode switches, vanilla haul selection, and cancel actions do not leave overlapping plans behind.
- Added session logs under `BepInEx\BIGSupportLogs`.
- Install note: remove old `BIGLooseHaulPrototype.dll` test builds before installing this release.

## 0.8.17

- Deferred blocked primary haul jobs when other loose items can still fit.
- Added a controlled one-item drag fallback after loose hauling is exhausted.
- Added clearer logs for skipped duplicate and destination-mismatch haul jobs.
- No install changes from 0.8.16.

## 0.8.16

- Active dragged helpers now only defer drag jobs when a loose haul item can actually fit.
- When the helper can no longer take loose items, BIG queues a helper drop/release before returning to drag hauling.
- Added clearer support-log markers for helper release decisions.
- No install changes from 0.8.15.

## 0.8.15

- Prioritized the active dragged helper container before backpacks and hand-held containers.
- Stopped rejecting bulky loose items just because they are cumbersome or have a drag slot while a helper is active.
- Other containers are still kept out of active-helper filling so the hauler does not swap helpers mid-job.
- This should let equipment trucks and dollies fill with more selected loose items before vanilla drag hauling resumes.
- No install changes from 0.8.14.

## 0.8.14

- Fixed active helper containers treating some bulky loose objects like normal inventory pickups.
- Doors, stools, pumps, crates, and similar drag-capable objects are now left for the drag phase while the helper fills with true loose inventory items.
- Stackable inventory items remain eligible for helper filling.
- Added clearer skip reasons to support logs for active-helper filtering.
- No install changes from 0.8.13.

## 0.8.13

- Locked an already-dragged container as the active hauling helper.
- Skipped haul jobs for that helper while loose inventory items are still available.
- Deferred drag-primary jobs when loose haul jobs can still be batched into the active helper.
- Added support-log markers for active helper and deferred primary decisions.
- No install changes from 0.8.12.

## 0.8.12

- Disabled automatic helper-container grabbing to prevent dolly, crate, and furniture swaps.
- Disabled adding normal drag items into loose-item batches.
- Kept attached storage support for backpacks, hand-held containers, carried containers, and dragged containers.
- Added a log marker when BIG skips a drag primary instead of batching it.
- No install changes from 0.8.11.

## 0.8.11

- Fixed helper containers being planned after no loose haul items were left.
- Stopped batch planning as soon as the carried storage plan is full.
- Tightened helper-container detection so small pickup-only containers are not treated like dollies.
- Added support-log markers for helper guard decisions.
- No install changes from 0.8.10.

## 0.8.10

- Fixed BIG support logs building up across game starts.
- Startup now zips any loose old BIG logs, then keeps only the two newest previous support logs.
- Older `BIG-*.log` and `BIG-*.zip` files are removed from `BepInEx\BIGSupportLogs`.
- No intended hauling behavior changes from 0.8.9.

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
- Containers are no longer blanket-skipped by condition alone; true drag/bulky items are still left for vanilla.
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

- Initial experimental hauling prototype.
