# Changelog

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
