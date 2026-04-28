using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace OstranautsSmartHaulingFresh
{
    [BepInPlugin("com.dezgard.ostranauts.simplehauling", "Ostranauts Simple Smart Hauling", "0.6.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; }
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony("com.dezgard.ostranauts.simplehauling");
            _harmony.PatchAll();
            Log.LogInfo("Ostranauts Simple Smart Hauling loaded. Vanilla haul queues are compacted, and build material fetches are batched.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    internal static class ItemDiagnostics
    {
        internal static string DescribeCarryState(CondOwner hauler, CondOwner item)
        {
            if (item == null)
                return "<null>";

            return SafeCO(item)
                + " parent=" + SafeCO(item.objCOParent)
                + " slot=" + (item.slotNow?.strName ?? "<none>")
                + " stack=" + item.StackCount
                + "/" + item.nStackLimit
                + " carried=" + item.HasCond("IsCarried")
                + " installed=" + item.HasCond("IsInstalled")
                + " draggingHauler=" + (hauler?.HasCond("IsDragging") ?? false)
                + " dragSlotItem=" + SafeCO(GetDragSlotItem(hauler))
                + " itemSlots=" + DescribeItemSlots(item)
                + " fits=" + CanFitInventory(hauler, item)
                + " pickupStack=" + CanTrigger(hauler, item, "PickupItemStack")
                + " dragStart=" + CanTrigger(hauler, item, "PickupDragStart");
        }

        internal static string SafeCO(CondOwner co)
        {
            if (co == null)
                return "<null>";

            var name = string.IsNullOrEmpty(co.strNameFriendly) ? co.strName : co.strNameFriendly;
            return (co.strID ?? "?") + "/" + (name ?? "?");
        }

        internal static CondOwner GetDragSlotItem(CondOwner hauler)
        {
            try
            {
                var slot = hauler?.compSlots?.GetSlot("drag");
                return slot?.GetOutermostCO();
            }
            catch
            {
                return null;
            }
        }

        internal static string DescribeItemSlots(CondOwner item)
        {
            try
            {
                if (item?.mapSlotEffects == null || item.mapSlotEffects.Count == 0)
                    return "<none>";

                return string.Join(",", item.mapSlotEffects.Keys);
            }
            catch
            {
                return "<error>";
            }
        }

        internal static bool CanFitInventory(CondOwner hauler, CondOwner item)
        {
            if (hauler == null || item == null)
                return false;

            try
            {
                if (hauler.objContainer != null && hauler.objContainer.CanFit(item, bAuto: false, bSub: true))
                    return true;

                if (hauler.compSlots != null)
                {
                    foreach (var slot in hauler.compSlots.GetSlotsHeldFirst(bDeep: true))
                    {
                        if (slot != null && slot.CanFit(item, bAuto: false, bSub: true))
                            return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        internal static bool CanTrigger(CondOwner hauler, CondOwner item, string interactionName)
        {
            if (hauler == null || item == null || string.IsNullOrEmpty(interactionName))
                return false;

            var interaction = DataHandler.GetInteraction(interactionName);
            if (interaction == null)
                return false;

            try
            {
                interaction.bVerboseTrigger = false;
                interaction.bHumanOnly = false;
                return interaction.Triggered(hauler, item, bStats: false, bIgnoreItems: false, bCheckPath: true, bFetchItems: false);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsDolly(CondOwner item)
        {
            if (item == null)
                return false;

            try
            {
                if (item.HasCond("IsDolly"))
                    return true;
            }
            catch
            {
            }

            var text = ((item.strNameFriendly ?? "") + " " + (item.strName ?? "") + " " + (item.strCODef ?? "")).ToLowerInvariant();
            return text.Contains("dolly") || text.Contains("equipment truck");
        }

        internal static CondOwner GetDraggedDolly(CondOwner hauler)
        {
            var dragged = GetDragSlotItem(hauler);
            return IsDolly(dragged) ? dragged : null;
        }

        internal static bool CanFitDraggedDolly(CondOwner hauler, CondOwner item)
        {
            var dolly = GetDraggedDolly(hauler);
            if (dolly?.objContainer == null || item == null)
                return false;

            try
            {
                return dolly.objContainer.CanFit(item, bAuto: false, bSub: true);
            }
            catch
            {
                return false;
            }
        }

        internal static int GetDraggedDollyBatchLimit(CondOwner hauler)
        {
            var dolly = GetDraggedDolly(hauler);
            if (dolly == null)
                return 0;

            return 200;
        }
    }

    internal static class BatchSafety
    {
        internal static bool ShouldUseVanillaCarry(CondOwner hauler, CondOwner item, out string reason)
        {
            reason = "";
            if (item == null)
                return false;

            if (ItemDiagnostics.CanTrigger(hauler, item, "PickupDragStart"))
            {
                reason = "dragstart";
                return true;
            }

            if (!IsSafeBatchCarryItem(item, out reason))
                return true;

            return false;
        }

        internal static bool ShouldUseVanillaBuildCarry(CondOwner hauler, CondOwner item, out string reason)
        {
            if (CanUseDraggedDollyForBuild(hauler, item))
            {
                reason = "dolly";
                return false;
            }

            return ShouldUseVanillaCarry(hauler, item, out reason);
        }

        internal static bool CanUseDraggedDollyForBuild(CondOwner hauler, CondOwner item)
        {
            if (hauler == null || item == null)
                return false;

            if (!HasDragSlot(item))
                return false;

            return ItemDiagnostics.CanFitDraggedDolly(hauler, item)
                && ItemDiagnostics.CanTrigger(hauler, item, "PickupItemStack");
        }

        internal static bool IsSafeBatchCarryItem(CondOwner item, out string reason)
        {
            reason = "";
            if (item == null)
            {
                reason = "null";
                return false;
            }

            if (IsKnownBulkyCarryItem(item))
            {
                reason = "bulky";
                return false;
            }

            if (HasAnyCond(item, "IsCumbersome", "IsOversized", "IsContainer"))
            {
                reason = "bulkycond";
                return false;
            }

            if (HasDragSlot(item) && !IsStackableLooseFloor(item))
            {
                reason = "dragslot";
                return false;
            }

            reason = "safe";
            return true;
        }

        private static bool IsKnownBulkyCarryItem(CondOwner item)
        {
            var text = ((item.strNameFriendly ?? "") + " " + (item.strName ?? "") + " " + (item.strCODef ?? "")).ToLowerInvariant();
            return text.Contains("crate")
                || text.Contains("wall")
                || text.Contains("hull")
                || text.Contains("door")
                || text.Contains("pump")
                || text.Contains("regulator")
                || text.Contains("cooler")
                || text.Contains("canister")
                || text.Contains("bottle");
        }

        private static bool HasAnyCond(CondOwner item, params string[] conds)
        {
            if (item == null || conds == null)
                return false;

            foreach (var cond in conds)
            {
                try
                {
                    if (item.HasCond(cond))
                        return true;
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasDragSlot(CondOwner item)
        {
            try
            {
                return item?.mapSlotEffects != null && item.mapSlotEffects.ContainsKey("drag");
            }
            catch
            {
                return true;
            }
        }

        private static bool IsStackableLooseFloor(CondOwner item)
        {
            if (item == null || item.nStackLimit <= 1)
                return false;

            var text = ((item.strNameFriendly ?? "") + " " + (item.strName ?? "") + " " + (item.strCODef ?? "") + " " + (item.strItemDef ?? "")).ToLowerInvariant();
            return text.Contains("floor")
                && !text.Contains("wall")
                && !text.Contains("door")
                && !text.Contains("hull");
        }
    }

    internal static class SimpleHaulBatcher
    {
        private const int HardSafetyLimit = 200;
        private static readonly HashSet<string> CompactingHaulerIDs = new HashSet<string>();

        internal static void TryCompactVanillaHaulQueue(CondOwner hauler)
        {
            if (hauler?.aQueue == null || hauler.aQueue.Count < 3 || string.IsNullOrEmpty(hauler.strID))
                return;

            lock (CompactingHaulerIDs)
            {
                if (CompactingHaulerIDs.Contains(hauler.strID))
                    return;

                CompactingHaulerIDs.Add(hauler.strID);
            }

            try
            {
                if (!TryReadHaulChain(hauler.aQueue, 0, out var primary))
                    return;

                Plugin.Log.LogInfo("[DragProbeHaulPrimary] hauler=" + SafeCO(hauler)
                    + " item={" + ItemDiagnostics.DescribeCarryState(hauler, primary.Item) + "}");

                if (BatchSafety.ShouldUseVanillaCarry(hauler, primary.Item, out var primaryReason))
                {
                    Plugin.Log.LogInfo("[SimpleHaulVanillaCarry] hauler=" + SafeCO(hauler)
                        + " reason=" + primaryReason
                        + " item={" + ItemDiagnostics.DescribeCarryState(hauler, primary.Item) + "}");
                    return;
                }

                var workManager = CrewSim.objInstance?.workManager;
                if (workManager == null)
                    return;

                var destinationShipID = GetDestinationShipID(primary);
                if (string.IsNullOrEmpty(destinationShipID))
                    return;

                var chains = new List<HaulChain> { primary };
                var carryBudget = new PlannedCarryBudget(hauler);
                carryBudget.TryReserve(primary.Item);

                for (var i = 1; i < HardSafetyLimit; i++)
                {
                    var task = workManager.ClaimNextTask(hauler);
                    if (task == null)
                        break;

                    if (IsVanillaCarryHaulTask(hauler, task, out var item, out var reason))
                    {
                        Plugin.Log.LogInfo("[SimpleHaulSkipVanillaCarry] hauler=" + SafeCO(hauler)
                            + " reason=" + reason
                            + " item={" + ItemDiagnostics.DescribeCarryState(hauler, item) + "}");
                        workManager.UnclaimTask(task);
                        continue;
                    }

                    if (!TryBuildVanillaHaulChain(hauler, task, destinationShipID, carryBudget, out var chain, out var failReason))
                    {
                        if (failReason == "capacity")
                        {
                            Plugin.Log.LogInfo("[SimpleHaulCapacityStop] hauler=" + SafeCO(hauler)
                                + " item={" + ItemDiagnostics.DescribeCarryState(hauler, item) + "}");
                        }

                        workManager.UnclaimTask(task);
                        break;
                    }

                    chains.Add(chain);
                }

                if (chains.Count <= 1)
                    return;

                CompactLeadingHaulChains(hauler, chains);
                Plugin.Log.LogInfo("[SimpleHaulCompacted] hauler=" + SafeCO(hauler)
                    + " items=" + chains.Count
                    + " destinationShip=" + destinationShipID
                    + " queue={" + QueueSummary(hauler) + "}");
            }
            finally
            {
                lock (CompactingHaulerIDs)
                {
                    CompactingHaulerIDs.Remove(hauler.strID);
                }
            }
        }

        private static bool TryBuildVanillaHaulChain(CondOwner hauler, Task2 task, string requiredDestinationShipID, PlannedCarryBudget carryBudget, out HaulChain chain, out string failReason)
        {
            chain = null;
            failReason = "other";
            if (hauler == null || task == null || !IsHaulTask(task))
                return false;

            var item = task.GetIA()?.objThem;
            if (item == null || !WorkManager.CTHaul.Triggered(item))
                return false;

            Plugin.Log.LogInfo("[DragProbeHaulExtraCandidate] hauler=" + SafeCO(hauler)
                + " item={" + ItemDiagnostics.DescribeCarryState(hauler, item) + "}");

            if (BatchSafety.ShouldUseVanillaCarry(hauler, item, out var reason))
            {
                Plugin.Log.LogInfo("[SimpleHaulSkipVanillaCarry] hauler=" + SafeCO(hauler)
                    + " reason=" + reason
                    + " item={" + ItemDiagnostics.DescribeCarryState(hauler, item) + "}");
                return false;
            }

            if (carryBudget != null && !carryBudget.TryReserve(item))
            {
                failReason = "capacity";
                return false;
            }

            var tile = ResolveHaulDestination(hauler, task, item);
            if (tile?.coProps == null || string.IsNullOrEmpty(task.strTileShip) || task.strTileShip != requiredDestinationShipID)
                return false;

            var pickup = DataHandler.GetInteraction("PickupItemStack");
            var walk = DataHandler.GetInteraction("Walk");
            var drop = DataHandler.GetInteraction("DropItemStack");
            if (pickup == null || walk == null || drop == null)
                return false;

            hauler.QueueInteraction(item, pickup);

            task.strInteraction = pickup.strName;

            walk.strTargetPoint = "use";
            walk.fTargetPointRange = 0f;
            hauler.QueueInteraction(tile.coProps, walk);

            hauler.QueueInteraction(item, drop);
            task.SetIA(drop);

            chain = new HaulChain(item, pickup, walk, drop);
            failReason = "";
            return true;
        }

        private static bool TryReadHaulTaskCandidate(CondOwner hauler, Task2 task, out CondOwner item, out string destinationShipID, out string skipReason)
        {
            item = null;
            destinationShipID = null;
            skipReason = "other";

            if (hauler == null || task == null || !IsHaulTask(task))
                return false;

            item = task.GetIA()?.objThem;
            if (item == null || !WorkManager.CTHaul.Triggered(item))
            {
                skipReason = "nothaulable";
                return false;
            }

            var tile = ResolveHaulDestination(hauler, task, item);
            if (tile?.coProps == null || string.IsNullOrEmpty(task.strTileShip))
            {
                skipReason = "destination";
                return false;
            }

            destinationShipID = task.strTileShip;
            skipReason = "";
            return true;
        }

        private static bool IsVanillaCarryHaulTask(CondOwner hauler, Task2 task, out CondOwner item, out string reason)
        {
            item = null;
            reason = "";
            if (!TryReadHaulTaskCandidate(hauler, task, out item, out _, out _))
                return false;

            return BatchSafety.ShouldUseVanillaCarry(hauler, item, out reason);
        }

        private static Tile ResolveHaulDestination(CondOwner hauler, Task2 task, CondOwner item)
        {
            Tile tile = null;
            Ship targetShip = null;
            CrewSim.system.dictShips.TryGetValue(task.strTileShip, out targetShip);

            if (targetShip != null)
            {
                if (targetShip.aTiles.Count > task.nTile)
                {
                    tile = targetShip.aTiles[task.nTile];
                }
                else if (CrewSim.objInstance.workManager.HaulZone(hauler, task, item) != null)
                {
                    tile = hauler.ship.aTiles[task.nTile];
                    CrewSim.system.dictShips.TryGetValue(task.strTileShip, out targetShip);
                }
            }

            return tile;
        }

        private static void CompactLeadingHaulChains(CondOwner hauler, List<HaulChain> chains)
        {
            var queue = hauler.aQueue;
            var consumed = chains.Count * 3;
            var tail = new List<Interaction>();
            for (var i = consumed; i < queue.Count; i++)
                tail.Add(queue[i]);

            queue.Clear();
            foreach (var chain in chains)
                queue.Add(chain.Pickup);

            foreach (var chain in chains)
            {
                if (queue.Count == 0 || queue[queue.Count - 1].objThem != chain.Walk.objThem)
                    queue.Add(chain.Walk);

                queue.Add(chain.Drop);
            }

            queue.AddRange(tail);
        }

        private static void RemoveLeadingHaulChain(CondOwner hauler, HaulChain chain)
        {
            var queue = hauler?.aQueue;
            if (queue == null || chain == null)
                return;

            if (queue.Count >= 3
                && queue[0] == chain.Pickup
                && queue[1] == chain.Walk
                && queue[2] == chain.Drop)
            {
                queue.RemoveRange(0, 3);
            }
        }

        private static bool IsHaulTask(Task2 task)
        {
            return task != null
                && string.Equals(task.strDuty, "Haul", StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.strInteraction, "ACTHaulItem", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNamed(Interaction interaction, string name)
        {
            return interaction != null && string.Equals(interaction.strName, name, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeCO(CondOwner co)
        {
            if (co == null)
                return "<null>";

            var name = string.IsNullOrEmpty(co.strNameFriendly) ? co.strName : co.strNameFriendly;
            return (co.strID ?? "?") + "/" + (name ?? "?");
        }

        private static string QueueSummary(CondOwner hauler)
        {
            if (hauler?.aQueue == null)
                return "<null>";

            var sb = new StringBuilder();
            sb.Append("count=").Append(hauler.aQueue.Count).Append(" [");
            var max = Math.Min(hauler.aQueue.Count, 16);
            for (var i = 0; i < max; i++)
            {
                if (i > 0)
                    sb.Append("; ");

                var interaction = hauler.aQueue[i];
                sb.Append(i).Append(":");
                sb.Append(interaction?.strName ?? "?");
                sb.Append("->");
                sb.Append(SafeCO(interaction?.objThem));
            }

            if (hauler.aQueue.Count > max)
                sb.Append("; ...");

            sb.Append("]");
            return sb.ToString();
        }

        private static string GetDestinationShipID(HaulChain chain)
        {
            return chain?.Walk?.objThem?.ship?.strRegID;
        }

        private static bool TryReadHaulChain(List<Interaction> queue, int index, out HaulChain chain)
        {
            chain = null;
            if (queue == null || queue.Count <= index + 2)
                return false;

            var pickup = queue[index];
            var walk = queue[index + 1];
            var drop = queue[index + 2];

            if (!IsNamed(pickup, "PickupItemStack")
                || !IsNamed(walk, "Walk")
                || !IsNamed(drop, "DropItemStack")
                || pickup.objThem == null
                || walk.objThem == null
                || drop.objThem != pickup.objThem)
            {
                return false;
            }

            chain = new HaulChain(pickup.objThem, pickup, walk, drop);
            return true;
        }

        private sealed class PlannedCarryBudget
        {
            private readonly HashSet<string> _stackKeys = new HashSet<string>();
            private int _freeSlots;

            internal PlannedCarryBudget(CondOwner hauler)
            {
                _freeSlots = CountFreeSlots(hauler);
                if (_freeSlots <= 0)
                    _freeSlots = 1;
            }

            internal bool TryReserve(CondOwner item)
            {
                if (item == null)
                    return false;

                if (item.nStackLimit > 1)
                {
                    var key = StackKey(item);
                    if (_stackKeys.Contains(key))
                        return true;

                    if (_freeSlots <= 0)
                        return false;

                    _stackKeys.Add(key);
                    _freeSlots--;
                    return true;
                }

                if (_freeSlots <= 0)
                    return false;

                _freeSlots--;
                return true;
            }

            private static int CountFreeSlots(CondOwner hauler)
            {
                if (hauler?.compSlots == null)
                    return 0;

                try
                {
                    var count = 0;
                    foreach (var slot in hauler.compSlots.GetSlotsHeldFirst(bDeep: true))
                    {
                        if (slot != null && slot.GetOutermostCO() == null)
                            count++;
                    }

                    return count;
                }
                catch
                {
                    return 0;
                }
            }

            private static string StackKey(CondOwner item)
            {
                return item.strCODef
                    ?? item.strItemDef
                    ?? item.strName
                    ?? item.strNameFriendly
                    ?? item.strID
                    ?? "";
            }
        }

        private sealed class HaulChain
        {
            internal HaulChain(CondOwner item, Interaction pickup, Interaction walk, Interaction drop)
            {
                Item = item;
                Pickup = pickup;
                Walk = walk;
                Drop = drop;
            }

            internal CondOwner Item { get; }
            internal Interaction Pickup { get; }
            internal Interaction Walk { get; }
            internal Interaction Drop { get; }
        }
    }

    internal static class BuildMaterialFetchBatcher
    {
        private const int FetchSafetyLimit = 200;
        private static readonly HashSet<string> BatchingHaulerIDs = new HashSet<string>();

        internal static void TryBatchQueuedMaterialFetches(CondOwner hauler, Interaction insertedInteraction, bool insertedAtFront)
        {
            if (hauler?.aQueue == null || insertedInteraction == null || !insertedAtFront || string.IsNullOrEmpty(hauler.strID))
            {
                return;
            }

            if (!IsPickup(insertedInteraction))
                return;

            if (hauler.aQueue.Count < 2)
                return;

            if (hauler.aQueue[0] != insertedInteraction)
                return;

            var buildInteraction = hauler.aQueue[1];
            if (!IsBuildOrInstallInteraction(buildInteraction)
                || buildInteraction.aSeekItemsForContract == null
                || buildInteraction.aSeekItemsForContract.Count == 0)
            {
                return;
            }

            lock (BatchingHaulerIDs)
            {
                if (BatchingHaulerIDs.Contains(hauler.strID))
                    return;

                BatchingHaulerIDs.Add(hauler.strID);
            }

            try
            {
            if (!TryGetRequiredMaterialTrigger(buildInteraction, out var requiredMaterial))
                return;

                Plugin.Log.LogInfo("[DragProbeBuildPrimary] hauler=" + SafeCO(hauler)
                    + " build=" + buildInteraction.strName
                    + " material=" + requiredMaterial.strName
                    + " item={" + ItemDiagnostics.DescribeCarryState(hauler, insertedInteraction.objThem) + "}");

                if (BatchSafety.ShouldUseVanillaBuildCarry(hauler, insertedInteraction.objThem, out var primaryReason))
                {
                    Plugin.Log.LogInfo("[BuildFetchVanillaCarry] hauler=" + SafeCO(hauler)
                        + " build=" + buildInteraction.strName
                        + " reason=" + primaryReason
                        + " item={" + ItemDiagnostics.DescribeCarryState(hauler, insertedInteraction.objThem) + "}");
                    return;
                }

                var extras = FindExtraBuildMaterials(hauler, requiredMaterial, insertedInteraction.objThem);
                if (extras.Count == 0)
                    return;

                var insertIndex = 1;
                foreach (var item in extras)
                {
                    var pickup = NewMaterialPickup(buildInteraction);
                    if (pickup == null)
                        break;

                    pickup.objUs = hauler;
                    pickup.objThem = item;
                    pickup.bManual = buildInteraction.bManual;
                    pickup.strChainOwner = hauler.strID;
                    hauler.aQueue.Insert(insertIndex, pickup);
                    insertIndex++;
                }

                buildInteraction.bRetestItems = true;
                Plugin.Log.LogInfo("[BuildFetchBatched] hauler=" + SafeCO(hauler)
                    + " build=" + buildInteraction.strName
                    + " material=" + requiredMaterial.strName
                    + " extraPickups=" + extras.Count
                    + " queue={" + QueueSummary(hauler) + "}");
            }
            finally
            {
                lock (BatchingHaulerIDs)
                    BatchingHaulerIDs.Remove(hauler.strID);
            }
        }

        private static bool TryGetRequiredMaterialTrigger(Interaction buildInteraction, out CondTrigger requiredMaterial)
        {
            requiredMaterial = null;
            if (buildInteraction == null || string.IsNullOrEmpty(buildInteraction.strName))
                return false;

            const string feedPrefix = "ACTFeedItem";
            if (!buildInteraction.strName.StartsWith(feedPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var triggerName = buildInteraction.strName.Substring(feedPrefix.Length);
            if (string.IsNullOrEmpty(triggerName))
                return false;

            requiredMaterial = DataHandler.GetCondTrigger(triggerName);
            return requiredMaterial != null;
        }

        private static Interaction NewMaterialPickup(Interaction buildInteraction)
        {
            var pickupName = buildInteraction.bEquip
                ? "EquipItem"
                : ((!buildInteraction.bLot && !buildInteraction.bGiveWholeStack) ? "PickupItem" : "PickupItemStack");

            return DataHandler.GetInteraction(pickupName);
        }

        private static CondOwner TakeNextDistinctSeekItem(List<CondOwner> seekItems, CondOwner alreadyQueued)
        {
            if (seekItems == null || seekItems.Count == 0)
                return null;

            for (var i = 0; i < seekItems.Count; i++)
            {
                var item = seekItems[i];
                if (item == null)
                {
                    seekItems.RemoveAt(i);
                    i--;
                    continue;
                }

                if (item == alreadyQueued || (!string.IsNullOrEmpty(item.strID) && item.strID == alreadyQueued?.strID))
                    continue;

                seekItems.RemoveAt(i);
                return item;
            }

            return null;
        }

        private static List<CondOwner> FindExtraBuildMaterials(CondOwner hauler, CondTrigger requiredMaterial, CondOwner alreadyQueued)
        {
            var extras = new List<CondOwner>();
            if (hauler?.ship == null || requiredMaterial == null)
                return extras;

            List<CondOwner> candidates;
            try
            {
                candidates = hauler.ship.GetCOs(requiredMaterial, bSubObjects: true, bAllowDocked: true, bAllowLocked: false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[BuildFetchFindFailed] trigger=" + requiredMaterial.strName
                    + " error=" + ex.GetType().Name);
                return extras;
            }

            if (candidates == null || candidates.Count == 0)
                return extras;

            var effectiveLimit = FetchSafetyLimit;
            var dollyLimit = 0;
            if (BatchSafety.CanUseDraggedDollyForBuild(hauler, alreadyQueued))
            {
                dollyLimit = ItemDiagnostics.GetDraggedDollyBatchLimit(hauler);
                effectiveLimit = Math.Max(0, dollyLimit - 1);
            }

            if (effectiveLimit <= 0)
                return extras;

            candidates.Sort((a, b) => DistanceSq(hauler, a).CompareTo(DistanceSq(hauler, b)));
            var seen = new HashSet<string>();
            if (!string.IsNullOrEmpty(alreadyQueued?.strID))
                seen.Add(alreadyQueued.strID);

            var carryBudget = new PlannedMaterialBudget();
            carryBudget.TryReserve(alreadyQueued);

            var skippedNoFit = 0;
            var skippedNoPickup = 0;
            var skippedDragOnly = 0;
            var skippedVanillaCarry = 0;
            var skippedCapacity = 0;
            var skippedOther = 0;
            foreach (var item in candidates)
            {
                if (extras.Count >= effectiveLimit)
                    break;

                if (!IsCandidateBuildMaterial(hauler, item, requiredMaterial, seen, out var skipReason))
                {
                    if (skipReason == "nofit")
                        skippedNoFit++;
                    else if (skipReason == "nopickup")
                        skippedNoPickup++;
                    else if (skipReason == "dragonly")
                        skippedDragOnly++;
                    else if (skipReason == "vanillacarry")
                        skippedVanillaCarry++;
                    else if (skipReason == "capacity")
                        skippedCapacity++;
                    else
                        skippedOther++;
                    continue;
                }

                if (!carryBudget.TryReserve(item))
                {
                    skippedCapacity++;
                    continue;
                }

                seen.Add(item.strID);
                extras.Add(item);
            }

            Plugin.Log.LogInfo("[DragProbeBuildFind] hauler=" + SafeCO(hauler)
                + " material=" + requiredMaterial.strName
                + " candidates=" + candidates.Count
                + " selected=" + extras.Count
                + " limit=" + effectiveLimit
                + " dollyLimit=" + dollyLimit
                + " skippedNoFit=" + skippedNoFit
                + " skippedNoPickup=" + skippedNoPickup
                + " skippedDragOnly=" + skippedDragOnly
                + " skippedVanillaCarry=" + skippedVanillaCarry
                + " skippedCapacity=" + skippedCapacity
                + " skippedOther=" + skippedOther
                + " firstSelected={" + DescribeSelectedItems(hauler, extras, 6) + "}");

            return extras;
        }

        private static bool IsCandidateBuildMaterial(CondOwner hauler, CondOwner item, CondTrigger requiredMaterial, HashSet<string> seen, out string skipReason)
        {
            skipReason = "other";
            if (hauler == null || item == null || requiredMaterial == null || string.IsNullOrEmpty(item.strID))
                return false;

            if (seen != null && seen.Contains(item.strID))
            {
                skipReason = "seen";
                return false;
            }

            if (item.bDestroyed || item.Item == null || item.objCOParent != null || item.HasCond("IsCarried") || item.HasCond("IsInstalled"))
            {
                skipReason = "state";
                return false;
            }

            if (item.GetComponent<Placeholder>() != null)
            {
                skipReason = "placeholder";
                return false;
            }

            if (IsAlreadyQueuedForPickup(hauler, item))
            {
                skipReason = "queued";
                return false;
            }

            if (BatchSafety.ShouldUseVanillaBuildCarry(hauler, item, out _))
            {
                skipReason = "vanillacarry";
                return false;
            }

            try
            {
                if (!requiredMaterial.Triggered(item))
                {
                    skipReason = "trigger";
                    return false;
                }
            }
            catch
            {
                skipReason = "trigger";
                return false;
            }

            var fits = CanCurrentlyFit(hauler, item);
            var pickup = CanPickupNow(hauler, item);
            var drag = ItemDiagnostics.CanTrigger(hauler, item, "PickupDragStart");

            if (!fits)
            {
                skipReason = drag ? "dragonly" : "nofit";
                return false;
            }

            if (!pickup)
            {
                skipReason = "nopickup";
                return false;
            }

            skipReason = "";
            return true;
        }

        private static bool IsAlreadyQueuedForPickup(CondOwner hauler, CondOwner item)
        {
            if (hauler?.aQueue == null || item == null)
                return false;

            foreach (var queued in hauler.aQueue)
            {
                if (IsPickup(queued) && queued.objThem == item)
                    return true;
            }

            return false;
        }

        private static bool CanCurrentlyFit(CondOwner hauler, CondOwner item)
        {
            if (hauler == null || item == null)
                return false;

            try
            {
                if (ItemDiagnostics.CanFitDraggedDolly(hauler, item))
                    return true;

                if (hauler.objContainer != null && hauler.objContainer.CanFit(item, bAuto: false, bSub: true))
                    return true;

                if (hauler.compSlots != null)
                {
                    foreach (var slot in hauler.compSlots.GetSlotsHeldFirst(bDeep: true))
                    {
                        if (slot != null && slot.CanFit(item, bAuto: false, bSub: true))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[BuildFetchFitCheckFailed] hauler=" + SafeCO(hauler)
                    + " item=" + SafeCO(item)
                    + " error=" + ex.GetType().Name);
            }

            return false;
        }

        private static bool CanPickupNow(CondOwner hauler, CondOwner item)
        {
            var pickup = DataHandler.GetInteraction("PickupItemStack");
            if (pickup == null)
                return false;

            try
            {
                pickup.bVerboseTrigger = false;
                pickup.bHumanOnly = false;
                return pickup.Triggered(hauler, item, bStats: false, bIgnoreItems: false, bCheckPath: true, bFetchItems: false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[BuildFetchPickupCheckFailed] hauler=" + SafeCO(hauler)
                    + " item=" + SafeCO(item)
                    + " error=" + ex.GetType().Name);
                return false;
            }
        }

        private sealed class PlannedMaterialBudget
        {
            private readonly Dictionary<string, int> _stackCounts = new Dictionary<string, int>();

            internal bool TryReserve(CondOwner item)
            {
                if (item == null)
                    return false;

                if (item.nStackLimit <= 1)
                    return true;

                var key = StackKey(item);
                _stackCounts.TryGetValue(key, out var count);
                var newCount = count + Math.Max(1, item.StackCount);
                if (newCount > item.nStackLimit)
                    return false;

                _stackCounts[key] = newCount;
                return true;
            }

            private static string StackKey(CondOwner item)
            {
                return item.strCODef
                    ?? item.strItemDef
                    ?? item.strName
                    ?? item.strNameFriendly
                    ?? item.strID
                    ?? "";
            }
        }

        private static bool IsBuildOrInstallInteraction(Interaction interaction)
        {
            if (interaction == null || string.IsNullOrEmpty(interaction.strName))
                return false;

            if (DataHandler.dictInstallables2 != null && DataHandler.dictInstallables2.ContainsKey(interaction.strName))
                return true;

            return !string.IsNullOrEmpty(interaction.strStartInstall)
                || (interaction.objThem != null
                    && (!string.IsNullOrEmpty(interaction.objThem.strPlaceholderInstallReq)
                        || !string.IsNullOrEmpty(interaction.objThem.strPlaceholderInstallFinish)));
        }

        private static bool IsPickup(Interaction interaction)
        {
            return interaction != null
                && (string.Equals(interaction.strName, "PickupItemStack", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(interaction.strName, "PickupItem", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(interaction.strName, "EquipItem", StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeCO(CondOwner co)
        {
            if (co == null)
                return "<null>";

            var name = string.IsNullOrEmpty(co.strNameFriendly) ? co.strName : co.strNameFriendly;
            return (co.strID ?? "?") + "/" + (name ?? "?");
        }

        private static string QueueSummary(CondOwner hauler)
        {
            if (hauler?.aQueue == null)
                return "<null>";

            var sb = new StringBuilder();
            sb.Append("count=").Append(hauler.aQueue.Count).Append(" [");
            var max = Math.Min(hauler.aQueue.Count, 16);
            for (var i = 0; i < max; i++)
            {
                if (i > 0)
                    sb.Append("; ");

                var interaction = hauler.aQueue[i];
                sb.Append(i).Append(":");
                sb.Append(interaction?.strName ?? "?");
                sb.Append("->");
                sb.Append(SafeCO(interaction?.objThem));
            }

            if (hauler.aQueue.Count > max)
                sb.Append("; ...");

            sb.Append("]");
            return sb.ToString();
        }

        private static string DescribeSelectedItems(CondOwner hauler, List<CondOwner> items, int max)
        {
            if (items == null || items.Count == 0)
                return "<none>";

            var sb = new StringBuilder();
            var count = Math.Min(items.Count, max);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(" | ");

                sb.Append(ItemDiagnostics.DescribeCarryState(hauler, items[i]));
            }

            if (items.Count > count)
                sb.Append(" | ...");

            return sb.ToString();
        }

        private static float DistanceSq(CondOwner a, CondOwner b)
        {
            try
            {
                var pa = a.GetPos();
                var pb = b.GetPos();
                var dx = pa.x - pb.x;
                var dy = pa.y - pb.y;
                return dx * dx + dy * dy;
            }
            catch
            {
                return float.MaxValue;
            }
        }
    }

    [HarmonyPatch(typeof(CondOwner), "GetWork")]
    internal static class GetWorkPatch
    {
        private static void Postfix(CondOwner __instance)
        {
            try
            {
                SimpleHaulBatcher.TryCompactVanillaHaulQueue(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SimpleHaulBatch] failed: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(CondOwner), "QueueInteraction")]
    internal static class QueueInteractionPatch
    {
        private static void Postfix(CondOwner __instance, Interaction objInteraction, bool bInsert, bool __result)
        {
            if (!__result)
                return;

            try
            {
                if (IsDragInteraction(objInteraction))
                {
                    Plugin.Log.LogInfo("[DragProbeQueued] hauler=" + ItemDiagnostics.SafeCO(__instance)
                        + " interaction=" + (objInteraction?.strName ?? "?")
                        + " insert=" + bInsert
                        + " item={" + ItemDiagnostics.DescribeCarryState(__instance, objInteraction?.objThem) + "}");
                }

                BuildMaterialFetchBatcher.TryBatchQueuedMaterialFetches(__instance, objInteraction, bInsert);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[BuildFetchBatch] failed: " + ex);
            }
        }

        private static bool IsDragInteraction(Interaction interaction)
        {
            return interaction != null
                && (string.Equals(interaction.strName, "PickupDragStart", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(interaction.strName, "PickupDragStop", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(interaction.strName, "DropCorpse", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(interaction.strName, "JettisonCorpse", StringComparison.OrdinalIgnoreCase));
        }
    }
}
