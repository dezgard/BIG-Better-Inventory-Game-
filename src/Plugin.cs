using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Ostranauts.Inventory;

namespace OstranautsHaulingV2
{
    internal static class BigSupportLog
    {
        private static readonly object Sync = new object();
        private static string _logDir;
        private static string _sessionLogPath;
        private static bool _initialized;

        internal static string ZipPath { get; private set; }

        internal static void Init(string pluginVersion)
        {
            lock (Sync)
            {
                if (_initialized)
                    return;

                _initialized = true;
                _logDir = Path.Combine(GetBepInExRoot(), "BIGSupportLogs");
                Directory.CreateDirectory(_logDir);
                ZipPreviousLooseLogs();

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                _sessionLogPath = Path.Combine(_logDir, "BIG-" + stamp + ".log");
                ZipPath = Path.Combine(_logDir, "BIG-" + stamp + ".zip");

                WriteRaw("=== BIG support log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                WriteRaw("Plugin version: " + pluginVersion);
                WriteRaw("BepInEx root: " + GetBepInExRoot());
                WriteRaw("Game root: " + SafePath(() => Paths.GameRootPath));
                WriteRaw("Plugin path: " + Assembly.GetExecutingAssembly().Location);
                WriteRaw("");
                WritePluginSnapshot();
                WriteRaw("");
            }
        }

        internal static void ModInfo(string message)
        {
            Write("INFO", message);
        }

        internal static void ModWarn(string message)
        {
            Write("WARN", message);
        }

        internal static void ModError(string message)
        {
            Write("ERROR", message);
        }

        internal static void Shutdown()
        {
            lock (Sync)
            {
                if (!_initialized || string.IsNullOrEmpty(_sessionLogPath))
                    return;

                WriteRaw("");
                WriteRaw("=== BIG support log ended " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                TryZipFile(_sessionLogPath, ZipPath);
            }
        }

        private static void Write(string level, string message)
        {
            lock (Sync)
            {
                if (!_initialized || string.IsNullOrEmpty(_sessionLogPath))
                    return;

                WriteRaw(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + (message ?? ""));
            }
        }

        private static void WriteRaw(string line)
        {
            try
            {
                File.AppendAllText(_sessionLogPath, line + Environment.NewLine);
            }
            catch
            {
                // Support logging must never affect hauling behavior.
            }
        }

        private static void ZipPreviousLooseLogs()
        {
            try
            {
                foreach (var logPath in Directory.GetFiles(_logDir, "BIG-*.log"))
                {
                    var zipPath = Path.ChangeExtension(logPath, ".zip");
                    if (File.Exists(zipPath))
                        continue;

                    TryZipFile(logPath, zipPath);
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        private static void WritePluginSnapshot()
        {
            WriteRaw("=== Installed/loaded plugin snapshot ===");

            try
            {
                var infos = Chainloader.PluginInfos;
                WriteRaw("Loaded BepInEx plugins: " + (infos?.Count ?? 0));
                if (infos != null)
                {
                    foreach (var entry in infos.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var info = entry.Value;
                        var metadata = info?.Metadata;
                        WriteRaw("- " + (metadata?.GUID ?? entry.Key ?? "<unknown>")
                            + " | " + (metadata?.Name ?? "<unknown>")
                            + " | " + (metadata?.Version?.ToString() ?? "<unknown>")
                            + " | " + (info?.Location ?? "<unknown>"));
                    }
                }
            }
            catch (Exception ex)
            {
                WriteRaw("Loaded BepInEx plugins: failed to read: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var errors = Chainloader.DependencyErrors;
                WriteRaw("BepInEx dependency errors: " + (errors?.Count ?? 0));
                if (errors != null)
                {
                    foreach (var error in errors)
                        WriteRaw("- " + error);
                }
            }
            catch (Exception ex)
            {
                WriteRaw("BepInEx dependency errors: failed to read: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var pluginPath = Paths.PluginPath;
                WriteRaw("Plugin folder files: " + pluginPath);
                if (Directory.Exists(pluginPath))
                {
                    foreach (var file in Directory.GetFiles(pluginPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        var info = new FileInfo(file);
                        WriteRaw("- " + info.Name
                            + " | " + info.Length + " bytes"
                            + " | modified " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                            + " | " + PluginFileState(info));
                    }
                }
                else
                {
                    WriteRaw("- plugin folder not found");
                }
            }
            catch (Exception ex)
            {
                WriteRaw("Plugin folder files: failed to read: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string PluginFileState(FileInfo info)
        {
            if (info == null)
                return "unknown";

            if (string.Equals(info.Extension, ".dll", StringComparison.OrdinalIgnoreCase))
                return "dll-load-candidate";

            if (info.Name.IndexOf(".dll.", StringComparison.OrdinalIgnoreCase) >= 0
                || info.Name.EndsWith(".dll.txt", StringComparison.OrdinalIgnoreCase))
                return "disabled-or-renamed-dll";

            return "non-dll-file";
        }

        private static void TryZipFile(string logPath, string zipPath)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                    return;

                zipPath = GetAvailableZipPath(zipPath);
                using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry(Path.GetFileName(logPath), CompressionLevel.Optimal);
                    using (var entryStream = entry.Open())
                    using (var logStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        logStream.CopyTo(entryStream);
                    }
                }
            }
            catch
            {
                // Leave the loose log in place if zipping fails.
            }
        }

        private static string GetAvailableZipPath(string requestedPath)
        {
            if (!File.Exists(requestedPath))
                return requestedPath;

            var folder = Path.GetDirectoryName(requestedPath);
            var name = Path.GetFileNameWithoutExtension(requestedPath);
            var ext = Path.GetExtension(requestedPath);

            for (var i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(folder, name + "-" + i + ext);
                if (!File.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(folder, name + "-" + DateTime.Now.Ticks + ext);
        }

        private static string GetBepInExRoot()
        {
            try
            {
                if (!string.IsNullOrEmpty(Paths.BepInExRootPath))
                    return Paths.BepInExRootPath;
            }
            catch
            {
            }

            var pluginPath = Assembly.GetExecutingAssembly().Location;
            return Path.GetDirectoryName(Path.GetDirectoryName(pluginPath)) ?? Path.GetDirectoryName(pluginPath) ?? ".";
        }

        private static string SafePath(Func<string> getter)
        {
            try
            {
                return getter() ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    [BepInPlugin("com.dezgard.ostranauts.haulingv2", "Ostranauts Hauling V2", "0.8.7")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string PluginVersion = "0.8.7";
        internal static ManualLogSource Log { get; private set; }
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            BigSupportLog.Init(PluginVersion);
            _harmony = new Harmony("com.dezgard.ostranauts.haulingv2");
            _harmony.PatchAll();
            ModInfo("Ostranauts Hauling V2 " + PluginVersion + " loaded. Utility dragged-container planning continues loose pickup. Support zip will be written to " + BigSupportLog.ZipPath);
        }

        private void OnDestroy()
        {
            try
            {
                ModInfo("Ostranauts Hauling V2 " + PluginVersion + " unloading.");
                _harmony?.UnpatchSelf();
            }
            finally
            {
                BigSupportLog.Shutdown();
            }
        }

        internal static void ModInfo(string message)
        {
            Log?.LogInfo(message);
            BigSupportLog.ModInfo(message);
        }

        internal static void ModWarn(string message)
        {
            Log?.LogWarning(message);
            BigSupportLog.ModWarn(message);
        }

        internal static void ModError(string message)
        {
            Log?.LogError(message);
            BigSupportLog.ModError(message);
        }
    }

    internal static class VanillaHaulBatcher
    {
        private const int HardSafetyLimit = 200;
        private static readonly HashSet<string> CompactingHaulerIDs = new HashSet<string>();
        private static readonly System.Reflection.MethodInfo GetPathToWalkableOriginMethod = AccessTools.Method(typeof(Pathfinder), "GetPathToWalkableOrigin", new[] { typeof(Tile), typeof(Tile) });

        internal static void TryCompact(CondOwner hauler)
        {
            if (hauler?.aQueue == null || hauler.aQueue.Count < 3 || string.IsNullOrEmpty(hauler.strID))
                return;

            HaulBatchRegistry.CleanupHauler(hauler);

            lock (CompactingHaulerIDs)
            {
                if (CompactingHaulerIDs.Contains(hauler.strID))
                    return;

                CompactingHaulerIDs.Add(hauler.strID);
            }

            var skippedTasks = new List<Task2>();

            try
            {
                if (!TryReadHaulChain(hauler.aQueue, 0, out var primary))
                    return;

                if (!CarriedContainerPlan.TryCreate(hauler, out var carryPlan, out var planReason))
                {
                    Plugin.ModInfo("[V2HaulNoCarryPlan] hauler=" + SafeCO(hauler)
                        + " reason=" + planReason
                        + " item={" + DescribeItem(primary.Item) + "}");
                    return;
                }

                var workManager = CrewSim.objInstance?.workManager;
                if (workManager == null)
                    return;

                var destinationShipID = GetDestinationShipID(primary);
                if (string.IsNullOrEmpty(destinationShipID))
                    return;

                var looseChains = new List<HaulChain>();
                HaulChain utilityDragChain = null;
                HaulChain dragChain = null;
                var allowExtraDrag = !carryPlan.DragSlotOccupied;
                var allowUtilityDrag = !carryPlan.DragSlotOccupied;
                var seenItemIDs = new HashSet<string>();
                if (!string.IsNullOrEmpty(primary.Item?.strID))
                    seenItemIDs.Add(primary.Item.strID);

                var inventoryClosed = false;
                if (TryClassifyPrimary(hauler, primary, carryPlan, looseChains, ref utilityDragChain, ref dragChain, allowExtraDrag, allowUtilityDrag, out var primaryStopReason))
                    inventoryClosed = primaryStopReason == "inventory-full";
                else
                    return;

                for (var i = 1; i < HardSafetyLimit; i++)
                {
                    var task = workManager.ClaimNextTask(hauler);
                    if (task == null)
                        break;

                    if (!TryReadClaimedHaulTask(hauler, task, out var item, out var tile, out var readReason))
                    {
                        workManager.UnclaimTask(task);
                        Plugin.ModInfo("[V2HaulClaimStop] hauler=" + SafeCO(hauler)
                            + " reason=" + readReason);
                        break;
                    }

                    if (!string.IsNullOrEmpty(item?.strID) && seenItemIDs.Contains(item.strID))
                    {
                        skippedTasks.Add(task);
                        continue;
                    }

                    if (!string.Equals(task.strTileShip, destinationShipID, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedTasks.Add(task);
                        continue;
                    }

                    if (!IsLooseInventoryPickup(hauler, item, out var itemReason))
                    {
                        if (allowUtilityDrag && utilityDragChain == null && TryPlanUtilityDragContainer(hauler, task, item, tile, carryPlan, out utilityDragChain, out var utilityReason))
                        {
                            Plugin.ModInfo("[V2UtilityDragAdded] hauler=" + SafeCO(hauler)
                                + " reason=" + utilityReason
                                + " item={" + DescribeItem(item) + "}");
                            if (!string.IsNullOrEmpty(item.strID))
                                seenItemIDs.Add(item.strID);
                            continue;
                        }

                        if (allowExtraDrag && utilityDragChain == null && dragChain == null && IsOneTripDragCandidate(hauler, item, out var dragReason))
                        {
                            if (!TryQueueVanillaHaulChain(hauler, task, item, tile, out dragChain, out var dragQueueReason))
                            {
                                workManager.UnclaimTask(task);
                                Plugin.ModInfo("[V2HaulDragQueueStop] hauler=" + SafeCO(hauler)
                                    + " reason=" + dragQueueReason
                                    + " item={" + DescribeItem(item) + "}");
                                break;
                            }

                            Plugin.ModInfo("[V2HaulDragAdded] hauler=" + SafeCO(hauler)
                                + " reason=" + dragReason
                                + " item={" + DescribeItem(item) + "}");
                            if (inventoryClosed)
                                break;
                        }
                        else
                        {
                            skippedTasks.Add(task);
                            Plugin.ModInfo("[V2HaulSkipForLater] hauler=" + SafeCO(hauler)
                                + " reason=" + itemReason
                                + " item={" + DescribeItem(item) + "}");
                        }
                        continue;
                    }

                    if (inventoryClosed)
                    {
                        if (allowUtilityDrag && utilityDragChain == null && TryPlanUtilityDragContainer(hauler, task, item, tile, carryPlan, out utilityDragChain, out var fullUtilityReason))
                        {
                            inventoryClosed = false;
                            Plugin.ModInfo("[V2UtilityDragAdded] hauler=" + SafeCO(hauler)
                                + " reason=" + fullUtilityReason + "/after-capacity"
                                + " item={" + DescribeItem(item) + "}");
                            if (!string.IsNullOrEmpty(item.strID))
                                seenItemIDs.Add(item.strID);
                            continue;
                        }

                        if (allowExtraDrag && utilityDragChain == null && dragChain == null && IsOneTripDragCandidate(hauler, item, out var fullDragReason))
                        {
                            if (!TryQueueVanillaHaulChain(hauler, task, item, tile, out dragChain, out var fullDragQueueReason))
                            {
                                workManager.UnclaimTask(task);
                                Plugin.ModInfo("[V2HaulDragQueueStop] hauler=" + SafeCO(hauler)
                                    + " reason=" + fullDragQueueReason
                                    + " item={" + DescribeItem(item) + "}");
                                break;
                            }

                            Plugin.ModInfo("[V2HaulDragAdded] hauler=" + SafeCO(hauler)
                                + " reason=" + fullDragReason
                                + " item={" + DescribeItem(item) + "}");
                            break;
                        }

                        skippedTasks.Add(task);
                        continue;
                    }

                    if (!carryPlan.TryReserve(item, out var reserveReason))
                    {
                        if (allowUtilityDrag && utilityDragChain == null && IsDragReserveStop(reserveReason) && TryPlanUtilityDragContainer(hauler, task, item, tile, carryPlan, out utilityDragChain, out var reserveUtilityReason))
                        {
                            Plugin.ModInfo("[V2UtilityDragAdded] hauler=" + SafeCO(hauler)
                                + " reason=" + reserveUtilityReason + "/" + reserveReason
                                + " item={" + DescribeItem(item) + "}");
                            if (!string.IsNullOrEmpty(item.strID))
                                seenItemIDs.Add(item.strID);
                            continue;
                        }

                        if (allowExtraDrag && utilityDragChain == null && IsDragReserveStop(reserveReason) && dragChain == null && IsOneTripDragCandidate(hauler, item, out var reserveDragReason))
                        {
                            if (!TryQueueVanillaHaulChain(hauler, task, item, tile, out dragChain, out var dragQueueReason))
                            {
                                workManager.UnclaimTask(task);
                                Plugin.ModInfo("[V2HaulDragQueueStop] hauler=" + SafeCO(hauler)
                                    + " reason=" + dragQueueReason
                                    + " item={" + DescribeItem(item) + "}");
                                break;
                            }

                            Plugin.ModInfo("[V2HaulDragAdded] hauler=" + SafeCO(hauler)
                                + " reason=" + reserveDragReason + "/" + reserveReason
                                + " item={" + DescribeItem(item) + "}");
                            continue;
                        }

                        if (reserveReason == "no-grid-room")
                        {
                            inventoryClosed = true;
                            workManager.UnclaimTask(task);
                            Plugin.ModInfo("[V2HaulCapacityStop] hauler=" + SafeCO(hauler)
                                + " reason=" + reserveReason
                                + " plannedItems=" + looseChains.Count
                                + " dragAdded=" + (dragChain != null)
                                + " carry={" + carryPlan.Description + "}"
                                + " item={" + DescribeItem(item) + "}");
                            if (dragChain != null)
                                break;

                            continue;
                        }

                        skippedTasks.Add(task);
                        continue;
                    }

                    if (!TryQueueVanillaHaulChain(hauler, task, item, tile, out var chain, out var queueReason))
                    {
                        workManager.UnclaimTask(task);
                        Plugin.ModInfo("[V2HaulQueueStop] hauler=" + SafeCO(hauler)
                            + " reason=" + queueReason
                            + " item={" + DescribeItem(item) + "}");
                        break;
                    }

                    looseChains.Add(chain);
                    if (!string.IsNullOrEmpty(item.strID))
                        seenItemIDs.Add(item.strID);
                }

                foreach (var skipped in skippedTasks)
                    workManager.UnclaimTask(skipped);

                skippedTasks.Clear();

                SortPickupChainsNearestNeighbor(hauler, looseChains);

                var chains = new List<HaulChain>(looseChains);
                if (utilityDragChain == null && dragChain != null)
                    chains.Add(dragChain);

                if (chains.Count <= 1 && utilityDragChain == null)
                    return;

                CompactLeadingHaulChains(hauler, utilityDragChain, chains);

                var registryChains = new List<HaulChain>(chains);
                if (utilityDragChain != null)
                    registryChains.Add(utilityDragChain);

                HaulBatchRegistry.Register(hauler, registryChains);

                Plugin.ModInfo("[V2HaulCompacted] hauler=" + SafeCO(hauler)
                    + " items=" + chains.Count
                    + " loose=" + looseChains.Count
                    + " drag=" + (utilityDragChain == null && dragChain != null)
                    + " utilityDrag=" + (utilityDragChain != null)
                    + " destinationShip=" + destinationShipID
                    + " carry={" + carryPlan.Description + "}"
                    + " queue={" + QueueSummary(hauler) + "}");
            }
            finally
            {
                var workManager = CrewSim.objInstance?.workManager;
                if (workManager != null)
                {
                    foreach (var skipped in skippedTasks)
                        workManager.UnclaimTask(skipped);
                }

                lock (CompactingHaulerIDs)
                {
                    CompactingHaulerIDs.Remove(hauler.strID);
                }
            }
        }

        private static bool TryClassifyPrimary(CondOwner hauler, HaulChain primary, CarriedContainerPlan carryPlan, List<HaulChain> looseChains, ref HaulChain utilityDragChain, ref HaulChain dragChain, bool allowExtraDrag, bool allowUtilityDrag, out string stopReason)
        {
            stopReason = "";
            if (primary?.Item == null)
                return false;

            if (!IsLooseInventoryPickup(hauler, primary.Item, out var primaryReason))
            {
                if (allowUtilityDrag && utilityDragChain == null && TryPlanPrimaryUtilityDragContainer(hauler, primary, carryPlan, out utilityDragChain, out var utilityReason))
                {
                    Plugin.ModInfo("[V2UtilityDragAdded] hauler=" + SafeCO(hauler)
                        + " reason=" + utilityReason + "/primary-" + primaryReason
                        + " item={" + DescribeItem(primary.Item) + "}");
                    return true;
                }

                if (allowExtraDrag && utilityDragChain == null && dragChain == null && IsOneTripDragCandidate(hauler, primary.Item, out var dragReason))
                {
                    dragChain = primary;
                    Plugin.ModInfo("[V2HaulPrimaryDrag] hauler=" + SafeCO(hauler)
                        + " reason=" + dragReason
                        + " item={" + DescribeItem(primary.Item) + "}");
                    return true;
                }

                Plugin.ModInfo("[V2HaulVanillaPrimary] hauler=" + SafeCO(hauler)
                    + " reason=" + primaryReason
                    + " item={" + DescribeItem(primary.Item) + "}");
                return false;
            }

            if (carryPlan.TryReserve(primary.Item, out var primaryReserveReason))
            {
                looseChains.Add(primary);
                return true;
            }

            if (allowUtilityDrag && utilityDragChain == null && IsDragReserveStop(primaryReserveReason) && TryPlanPrimaryUtilityDragContainer(hauler, primary, carryPlan, out utilityDragChain, out var reserveUtilityReason))
            {
                Plugin.ModInfo("[V2UtilityDragAdded] hauler=" + SafeCO(hauler)
                    + " reason=" + reserveUtilityReason + "/primary-" + primaryReserveReason
                    + " item={" + DescribeItem(primary.Item) + "}");
                return true;
            }

            if (allowExtraDrag && utilityDragChain == null && IsDragReserveStop(primaryReserveReason) && dragChain == null && IsOneTripDragCandidate(hauler, primary.Item, out var reserveDragReason))
            {
                dragChain = primary;
                Plugin.ModInfo("[V2HaulPrimaryDrag] hauler=" + SafeCO(hauler)
                    + " reason=" + reserveDragReason + "/" + primaryReserveReason
                    + " item={" + DescribeItem(primary.Item) + "}");
                return true;
            }

            Plugin.ModInfo("[V2HaulPrimaryNoRoom] hauler=" + SafeCO(hauler)
                + " reason=" + primaryReserveReason
                + " carry={" + carryPlan.Description + "}"
                + " item={" + DescribeItem(primary.Item) + "}");
            stopReason = primaryReserveReason;
            return false;
        }

        private static bool TryPlanPrimaryUtilityDragContainer(CondOwner hauler, HaulChain primary, CarriedContainerPlan carryPlan, out HaulChain chain, out string reason)
        {
            chain = null;
            reason = "other";

            if (primary?.Item == null)
            {
                reason = "null";
                return false;
            }

            if (!IsUtilityDragContainerCandidate(hauler, primary.Item, out reason))
                return false;

            if (!carryPlan.TryAddPlannedContainer(primary.Item, "planned-primary-drag", out var addReason))
            {
                reason = "container-plan-" + addReason;
                return false;
            }

            chain = primary;
            reason = "planned-primary-drag-container";
            return true;
        }

        private static bool TryReadClaimedHaulTask(CondOwner hauler, Task2 task, out CondOwner item, out Tile tile, out string reason)
        {
            item = null;
            tile = null;
            reason = "other";

            if (hauler == null || task == null)
            {
                reason = "null";
                return false;
            }

            if (!IsHaulTask(task))
            {
                reason = "not-haul-task";
                return false;
            }

            item = task.GetIA()?.objThem;
            if (item == null)
            {
                reason = "no-item";
                return false;
            }

            if (!WorkManager.CTHaul.Triggered(item))
            {
                reason = "not-haulable";
                return false;
            }

            tile = ResolveHaulDestination(hauler, task, item);
            if (tile?.coProps == null || string.IsNullOrEmpty(task.strTileShip))
            {
                reason = "no-destination";
                return false;
            }

            reason = "";
            return true;
        }

        private static bool TryQueueVanillaHaulChain(CondOwner hauler, Task2 task, CondOwner item, Tile tile, out HaulChain chain, out string reason)
        {
            chain = null;
            reason = "other";

            var pickup = DataHandler.GetInteraction("PickupItemStack");
            var walk = DataHandler.GetInteraction("Walk");
            var drop = DataHandler.GetInteraction("DropItemStack");
            if (pickup == null || walk == null || drop == null)
            {
                reason = "missing-interaction";
                return false;
            }

            if (!hauler.QueueInteraction(item, pickup))
            {
                reason = "pickup-queue-failed";
                return false;
            }

            task.strInteraction = pickup.strName;

            walk.strTargetPoint = "use";
            walk.fTargetPointRange = 0f;
            if (!hauler.QueueInteraction(tile.coProps, walk))
            {
                reason = "walk-queue-failed";
                return false;
            }

            if (!hauler.QueueInteraction(item, drop))
            {
                reason = "drop-queue-failed";
                return false;
            }

            task.SetIA(drop);
            chain = new HaulChain(item, pickup, walk, drop);
            reason = "";
            return true;
        }

        private static bool TryPlanUtilityDragContainer(CondOwner hauler, Task2 task, CondOwner item, Tile tile, CarriedContainerPlan carryPlan, out HaulChain chain, out string reason)
        {
            chain = null;
            reason = "other";

            if (!IsUtilityDragContainerCandidate(hauler, item, out reason))
                return false;

            var queueCount = hauler?.aQueue?.Count ?? 0;
            if (!TryQueueUtilityDragChain(hauler, task, item, tile, out chain, out var queueReason))
            {
                RemoveQueuedTail(hauler, queueCount);
                reason = "queue-" + queueReason;
                return false;
            }

            if (!carryPlan.TryAddPlannedContainer(item, "planned-drag", out var addReason))
            {
                RemoveQueuedTail(hauler, queueCount);
                chain = null;
                reason = "container-plan-" + addReason;
                return false;
            }

            reason = "planned-drag-container";
            return true;
        }

        private static bool TryQueueUtilityDragChain(CondOwner hauler, Task2 task, CondOwner item, Tile tile, out HaulChain chain, out string reason)
        {
            return TryQueueVanillaHaulChain(hauler, task, item, tile, out chain, out reason);
        }

        private static void RemoveQueuedTail(CondOwner hauler, int queueCount)
        {
            var queue = hauler?.aQueue;
            if (queue == null)
                return;

            while (queue.Count > queueCount)
                queue.RemoveAt(queue.Count - 1);
        }

        private static bool IsLooseInventoryPickup(CondOwner hauler, CondOwner item, out string reason)
        {
            reason = "other";
            if (hauler == null || item == null)
            {
                reason = "null";
                return false;
            }

            if (item.bDestroyed)
            {
                reason = "destroyed";
                return false;
            }

            if (item.coStackHead != null)
            {
                reason = "stack-child";
                return false;
            }

            if (item.objCOParent != null || item.slotNow != null || item.HasCond("IsCarried") || item.HasCond("IsInstalled"))
            {
                reason = "not-loose";
                return false;
            }

            if (item.Item == null)
            {
                reason = "not-item";
                return false;
            }

            if (!CanTrigger(hauler, item, "PickupItemStack"))
            {
                reason = CanTrigger(hauler, item, "PickupDragStart") ? "drag-only" : "no-pickup-stack";
                return false;
            }

            reason = "";
            return true;
        }

        private static bool IsUtilityDragContainerCandidate(CondOwner hauler, CondOwner item, out string reason)
        {
            reason = "other";
            if (hauler == null || item == null)
            {
                reason = "null";
                return false;
            }

            if (GetDragSlotItem(hauler) != null || hauler.HasCond("IsDragging"))
            {
                reason = "drag-slot-occupied";
                return false;
            }

            if (item.bDestroyed)
            {
                reason = "destroyed";
                return false;
            }

            if (item.coStackHead != null)
            {
                reason = "stack-child";
                return false;
            }

            if (item.objCOParent != null || item.slotNow != null || item.HasCond("IsCarried") || item.HasCond("IsInstalled"))
            {
                reason = "not-loose";
                return false;
            }

            if (item.Item == null)
            {
                reason = "not-item";
                return false;
            }

            if (item.objContainer == null)
            {
                reason = "no-container";
                return false;
            }

            if (item.objContainer.gridLayout == null && !item.HasCond("IsInfiniteContainer"))
            {
                reason = "no-container-grid";
                return false;
            }

            if (!CanTrigger(hauler, item, "PickupItemStack") && !HasDragSlot(item) && !CanTrigger(hauler, item, "PickupDragStartNPCPledge") && !CanTrigger(hauler, item, "PickupDragStart"))
            {
                reason = "not-draggable";
                return false;
            }

            reason = "";
            return true;
        }

        private static bool IsOneTripDragCandidate(CondOwner hauler, CondOwner item, out string reason)
        {
            reason = "other";
            if (hauler == null || item == null)
            {
                reason = "null";
                return false;
            }

            if (GetDragSlotItem(hauler) != null || hauler.HasCond("IsDragging"))
            {
                reason = "drag-slot-occupied";
                return false;
            }

            if (item.bDestroyed)
            {
                reason = "destroyed";
                return false;
            }

            if (item.coStackHead != null)
            {
                reason = "stack-child";
                return false;
            }

            if (item.objCOParent != null || item.slotNow != null || item.HasCond("IsCarried") || item.HasCond("IsInstalled"))
            {
                reason = "not-loose";
                return false;
            }

            if (item.Item == null)
            {
                reason = "not-item";
                return false;
            }

            if (!CanTrigger(hauler, item, "PickupItemStack"))
            {
                reason = "no-pickup-stack";
                return false;
            }

            if (CanTrigger(hauler, item, "PickupDragStart"))
            {
                reason = "dragstart";
                return true;
            }

            if (HasDragSlot(item))
            {
                reason = "dragslot";
                return true;
            }

            reason = "no-drag-marker";
            return false;
        }

        private static bool IsDragReserveStop(string reason)
        {
            return reason == "container-reject" || reason == "too-large";
        }

        private static bool HasDragSlot(CondOwner item)
        {
            try
            {
                return item?.mapSlotEffects != null && item.mapSlotEffects.ContainsKey("drag");
            }
            catch
            {
                return false;
            }
        }

        internal static CondOwner GetDragSlotItem(CondOwner hauler)
        {
            try
            {
                return hauler?.compSlots?.GetSlot("drag")?.GetOutermostCO();
            }
            catch
            {
                return null;
            }
        }

        private static bool CanTrigger(CondOwner hauler, CondOwner item, string interactionName)
        {
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

        private static Tile ResolveHaulDestination(CondOwner hauler, Task2 task, CondOwner item)
        {
            if (hauler == null || task == null || item == null)
                return null;

            Tile tile = null;
            Ship targetShip = null;
            CrewSim.system.dictShips.TryGetValue(task.strTileShip, out targetShip);

            if (targetShip != null)
            {
                if (targetShip.aTiles.Count > task.nTile)
                {
                    tile = targetShip.aTiles[task.nTile];
                }
                else if (CrewSim.objInstance?.workManager?.HaulZone(hauler, task, item) != null)
                {
                    CrewSim.system.dictShips.TryGetValue(task.strTileShip, out targetShip);
                    if (targetShip != null && targetShip.aTiles.Count > task.nTile)
                        tile = targetShip.aTiles[task.nTile];
                }
            }

            return tile;
        }

        private static void SortPickupChainsNearestNeighbor(CondOwner hauler, List<HaulChain> chains)
        {
            if (hauler == null || chains == null || chains.Count <= 1)
                return;

            var currentTile = GetCurrentTile(hauler);
            var remaining = new List<PickupSortTarget>();
            for (var i = 0; i < chains.Count; i++)
            {
                remaining.Add(new PickupSortTarget
                {
                    Chain = chains[i],
                    Tile = GetPickupTile(hauler, chains[i]),
                    OriginalIndex = i
                });
            }

            var orderedTargets = new List<PickupSortTarget>(chains.Count);
            var usedPathScores = 0;
            var usedFallbackScores = 0;

            while (remaining.Count > 0)
            {
                var bestIndex = 0;
                PickupSortScore bestScore = null;

                for (var i = 0; i < remaining.Count; i++)
                {
                    var score = ScorePickupTarget(hauler, currentTile, remaining[i]);
                    if (score.UsedPath)
                        usedPathScores++;
                    else
                        usedFallbackScores++;

                    if (bestScore == null || IsBetterPickupScore(score, bestScore))
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                var selected = remaining[bestIndex];
                remaining.RemoveAt(bestIndex);
                orderedTargets.Add(selected);

                if (selected.Tile != null)
                    currentTile = selected.Tile;
            }

            var changed = false;
            for (var i = 0; i < orderedTargets.Count; i++)
            {
                if (orderedTargets[i].Chain != chains[i])
                {
                    changed = true;
                    break;
                }
            }

            chains.Clear();
            foreach (var target in orderedTargets)
                chains.Add(target.Chain);

            Plugin.ModInfo("[V2PickupSort] hauler=" + SafeCO(hauler)
                + " count=" + chains.Count
                + " changed=" + changed
                + " pathScores=" + usedPathScores
                + " fallbackScores=" + usedFallbackScores
                + " order={" + PickupOrderSummary(orderedTargets, 10) + "}");
        }

        private static PickupSortScore ScorePickupTarget(CondOwner hauler, Tile currentTile, PickupSortTarget target)
        {
            var score = new PickupSortScore
            {
                OriginalIndex = target?.OriginalIndex ?? int.MaxValue,
                SameRoom = SameRoom(currentTile, target?.Tile),
                TileRange = TileRangeSafe(currentTile, target?.Tile),
                Cost = float.MaxValue,
                UsedPath = false
            };

            if (target == null)
                return score;

            if (TryGetPathCost(hauler, currentTile, target.Tile, target.Chain?.Item, target.Chain?.Pickup, out var pathCost))
            {
                score.Cost = pathCost;
                score.UsedPath = true;
                return score;
            }

            score.Cost = FallbackDistanceSq(hauler, currentTile, target);
            return score;
        }

        private static bool IsBetterPickupScore(PickupSortScore candidate, PickupSortScore currentBest)
        {
            if (candidate.UsedPath != currentBest.UsedPath)
                return candidate.UsedPath;

            if (candidate.Cost < currentBest.Cost)
                return true;

            if (candidate.Cost > currentBest.Cost)
                return false;

            if (candidate.SameRoom != currentBest.SameRoom)
                return candidate.SameRoom;

            if (candidate.TileRange != currentBest.TileRange)
                return candidate.TileRange < currentBest.TileRange;

            return candidate.OriginalIndex < currentBest.OriginalIndex;
        }

        private static bool TryGetPathCost(CondOwner hauler, Tile currentTile, Tile targetTile, CondOwner item, Interaction pickup, out float cost)
        {
            cost = float.MaxValue;
            if (hauler == null || currentTile == null || targetTile == null)
                return false;

            if (currentTile == targetTile)
            {
                cost = 0f;
                return true;
            }

            var pathfinder = hauler.Pathfinder;
            if (pathfinder == null)
                return false;

            try
            {
                var allowAirlocks = hauler.HasAirlockPermission(false);
                if (pathfinder.tilCurrent == currentTile)
                {
                    var range = pickup == null ? 1f : Math.Max(0f, pickup.fTargetPointRange);
                    var result = pathfinder.CheckGoal(targetTile, range, item, allowAirlocks);
                    if (result != null && result.HasPath && result.PathLength >= 0f)
                    {
                        cost = result.PathLength;
                        return true;
                    }
                }

                var path = GetPathToWalkableOriginMethod?.Invoke(pathfinder, new object[] { currentTile, targetTile }) as List<Tile>;
                if (path != null && path.Count > 0)
                {
                    cost = path.Count;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static Tile GetCurrentTile(CondOwner hauler)
        {
            try
            {
                if (hauler?.Pathfinder?.tilCurrent != null)
                    return hauler.Pathfinder.tilCurrent;

                if (hauler?.ship != null && hauler.tf != null)
                    return hauler.ship.GetTileAtWorldCoords1(hauler.tf.position.x, hauler.tf.position.y, true, true);
            }
            catch
            {
            }

            return null;
        }

        private static Tile GetPickupTile(CondOwner hauler, HaulChain chain)
        {
            if (hauler?.ship == null || chain?.Item == null)
                return null;

            try
            {
                var targetPoint = chain.Pickup?.strTargetPoint;
                if (string.IsNullOrEmpty(targetPoint) || string.Equals(targetPoint, "remote", StringComparison.OrdinalIgnoreCase))
                    targetPoint = "use";

                var pos = chain.Item.GetPos(targetPoint);
                return hauler.ship.GetTileAtWorldCoords1(pos.x, pos.y, true, true);
            }
            catch
            {
                return null;
            }
        }

        private static bool SameRoom(Tile a, Tile b)
        {
            return a?.room != null && b?.room != null && a.room == b.room;
        }

        private static int TileRangeSafe(Tile a, Tile b)
        {
            try
            {
                if (a != null && b != null)
                    return Math.Max(0, TileUtils.TileRange(a, b));
            }
            catch
            {
            }

            return int.MaxValue;
        }

        private static float FallbackDistanceSq(CondOwner hauler, Tile currentTile, PickupSortTarget target)
        {
            try
            {
                var pa = currentTile?.tf?.position ?? hauler.tf.position;
                var pb = target?.Tile?.tf?.position ?? target?.Chain?.Item?.tf?.position ?? pa;
                var dx = pa.x - pb.x;
                var dy = pa.y - pb.y;
                return dx * dx + dy * dy;
            }
            catch
            {
                return float.MaxValue;
            }
        }

        private static string PickupOrderSummary(List<PickupSortTarget> targets, int max)
        {
            if (targets == null || targets.Count == 0)
                return "<none>";

            var sb = new StringBuilder();
            var count = Math.Min(targets.Count, max);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(" | ");

                var target = targets[i];
                sb.Append(i)
                    .Append(":")
                    .Append(SafeCO(target?.Chain?.Item))
                    .Append("@tile=")
                    .Append(target?.Tile?.Index.ToString() ?? "?")
                    .Append("/orig=")
                    .Append(target?.OriginalIndex.ToString() ?? "?");
            }

            if (targets.Count > count)
                sb.Append(" | ...");

            return sb.ToString();
        }

        private static void CompactLeadingHaulChains(CondOwner hauler, HaulChain utilityDragChain, List<HaulChain> chains)
        {
            var queue = hauler.aQueue;
            var consumed = ((chains?.Count ?? 0) + (utilityDragChain == null ? 0 : 1)) * 3;
            var tail = new List<Interaction>();
            for (var i = consumed; i < queue.Count; i++)
                tail.Add(queue[i]);

            queue.Clear();

            if (utilityDragChain != null)
                queue.Add(utilityDragChain.Pickup);

            foreach (var chain in chains)
                queue.Add(chain.Pickup);

            foreach (var chain in chains)
            {
                if (queue.Count == 0 || queue[queue.Count - 1].objThem != chain.Walk.objThem)
                    queue.Add(chain.Walk);

                queue.Add(chain.Drop);
            }

            if (utilityDragChain != null)
            {
                if (queue.Count == 0 || queue[queue.Count - 1].objThem != utilityDragChain.Walk.objThem)
                    queue.Add(utilityDragChain.Walk);

                queue.Add(utilityDragChain.Drop);
            }

            queue.AddRange(tail);
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

        private static string GetDestinationShipID(HaulChain chain)
        {
            return chain?.Walk?.objThem?.ship?.strRegID;
        }

        private static string QueueSummary(CondOwner hauler)
        {
            if (hauler?.aQueue == null)
                return "<null>";

            var sb = new StringBuilder();
            sb.Append("count=").Append(hauler.aQueue.Count).Append(" [");
            var max = Math.Min(hauler.aQueue.Count, 20);
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

        private static string DescribeItem(CondOwner item)
        {
            if (item == null)
                return "<null>";

            return SafeCO(item)
                + " def=" + (item.strCODef ?? "?")
                + " stack=" + item.StackCount + "/" + item.nStackLimit
                + " parent=" + SafeCO(item.objCOParent)
                + " slot=" + (item.slotNow?.strName ?? "<none>")
                + " carried=" + item.HasCond("IsCarried")
                + " installed=" + item.HasCond("IsInstalled");
        }

        private static string SafeCO(CondOwner co)
        {
            if (co == null)
                return "<null>";

            var name = string.IsNullOrEmpty(co.strNameFriendly) ? co.strName : co.strNameFriendly;
            return (co.strID ?? "?") + "/" + (name ?? "?");
        }

        internal sealed class HaulChain
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

        private sealed class PickupSortTarget
        {
            internal HaulChain Chain;
            internal Tile Tile;
            internal int OriginalIndex;
        }

        private sealed class PickupSortScore
        {
            internal bool UsedPath;
            internal bool SameRoom;
            internal float Cost;
            internal int TileRange;
            internal int OriginalIndex;
        }
    }

    internal static class HaulBatchRegistry
    {
        private static readonly List<Entry> Entries = new List<Entry>();
        private static int CompletingTaskDepth;

        internal static void BeginCompleteTask()
        {
            CompletingTaskDepth++;
        }

        internal static void EndCompleteTask()
        {
            if (CompletingTaskDepth > 0)
                CompletingTaskDepth--;
        }

        internal static void Register(CondOwner hauler, List<VanillaHaulBatcher.HaulChain> chains)
        {
            if (hauler?.aQueue == null || chains == null || string.IsNullOrEmpty(hauler.strID))
                return;

            CleanupHauler(hauler);

            foreach (var chain in chains)
            {
                if (chain?.Item == null || string.IsNullOrEmpty(chain.Item.strID))
                    continue;

                Entries.RemoveAll(e => e.HaulerID == hauler.strID && e.ItemID == chain.Item.strID);
                Entries.Add(new Entry
                {
                    HaulerID = hauler.strID,
                    ItemID = chain.Item.strID,
                    Pickup = chain.Pickup,
                    Walk = chain.Walk,
                    Drop = chain.Drop
                });
            }
        }

        internal static void CleanupHauler(CondOwner hauler)
        {
            if (hauler == null || string.IsNullOrEmpty(hauler.strID))
                return;

            var queue = hauler.aQueue;
            Entries.RemoveAll(e =>
                e.HaulerID == hauler.strID
                && (queue == null
                    || (!queue.Contains(e.Pickup) && !queue.Contains(e.Walk) && !queue.Contains(e.Drop))));
        }

        internal static void HandleRemovedTask(Task2 task)
        {
            if (task == null || CompletingTaskDepth > 0 || string.IsNullOrEmpty(task.strTargetCOID))
                return;

            var removed = 0;
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                if (entry.ItemID != task.strTargetCOID)
                    continue;

                if (!TryGetHauler(entry.HaulerID, out var hauler) || hauler.aQueue == null)
                {
                    Entries.RemoveAt(i);
                    continue;
                }

                var queue = hauler.aQueue;
                var pickupQueued = queue.Contains(entry.Pickup);
                if (pickupQueued)
                {
                    queue.Remove(entry.Pickup);
                    queue.Remove(entry.Drop);
                    queue.Remove(entry.Walk);
                    removed++;
                    Plugin.ModInfo("[V2HaulCancelCleanup] hauler=" + SafeCO(hauler)
                        + " itemID=" + entry.ItemID
                        + " removedQueuedPickupDrop=True");
                }

                Entries.RemoveAt(i);
            }

            if (removed == 0 && Entries.Count > 512)
                Entries.RemoveAll(e => !TryGetHauler(e.HaulerID, out var hauler) || hauler.aQueue == null);
        }

        private static bool TryGetHauler(string haulerID, out CondOwner hauler)
        {
            hauler = null;
            if (string.IsNullOrEmpty(haulerID))
                return false;

            try
            {
                return DataHandler.mapCOs.TryGetValue(haulerID, out hauler) && hauler != null;
            }
            catch
            {
                return false;
            }
        }

        private static string SafeCO(CondOwner co)
        {
            if (co == null)
                return "<null>";

            var name = string.IsNullOrEmpty(co.strNameFriendly) ? co.strName : co.strNameFriendly;
            return (co.strID ?? "?") + "/" + (name ?? "?");
        }

        private sealed class Entry
        {
            internal string HaulerID;
            internal string ItemID;
            internal Interaction Pickup;
            internal Interaction Walk;
            internal Interaction Drop;
        }
    }

    internal sealed class CarriedContainerPlan
    {
        private readonly List<ContainerState> _containers;
        private readonly HashSet<string> _containerIDs;
        private int _reservedItems;

        private CarriedContainerPlan(List<ContainerCandidate> candidates, bool dragSlotOccupied)
        {
            DragSlotOccupied = dragSlotOccupied;
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            _containers = new List<ContainerState>();
            _containerIDs = new HashSet<string>();
            foreach (var candidate in candidates)
            {
                if (candidate?.Owner == null || string.IsNullOrEmpty(candidate.Owner.strID) || !_containerIDs.Add(candidate.Owner.strID))
                    continue;

                _containers.Add(new ContainerState(candidate.Owner.objContainer, candidate.Source));
            }
        }

        internal bool DragSlotOccupied { get; }

        internal string Description
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("containers=").Append(_containers.Count)
                    .Append(" dragOccupied=").Append(DragSlotOccupied)
                    .Append(" reserved=").Append(_reservedItems)
                    .Append(" [");

                for (var i = 0; i < _containers.Count; i++)
                {
                    if (i > 0)
                        sb.Append("; ");

                    sb.Append(_containers[i].Description);
                }

                sb.Append("]");
                return sb.ToString();
            }
        }

        internal static bool TryCreate(CondOwner hauler, out CarriedContainerPlan plan, out string reason)
        {
            plan = null;
            reason = "other";

            var dragSlotOccupied = VanillaHaulBatcher.GetDragSlotItem(hauler) != null || (hauler?.HasCond("IsDragging") ?? false);
            var candidates = FindCarriedContainers(hauler);
            if (candidates.Count == 0)
            {
                reason = "no-carried-container";
                return false;
            }

            plan = new CarriedContainerPlan(candidates, dragSlotOccupied);
            reason = "";
            Plugin.ModInfo("[V2CarryContainers] hauler=" + VanillaHaulBatcherSafeCO(hauler)
                + " " + plan.Description);
            return true;
        }

        internal bool TryAddPlannedContainer(CondOwner item, string source, out string reason)
        {
            reason = "other";
            if (item == null)
            {
                reason = "null";
                return false;
            }

            if (item.objContainer == null)
            {
                reason = "no-container";
                return false;
            }

            if (string.IsNullOrEmpty(item.strID))
            {
                reason = "no-id";
                return false;
            }

            if (_containerIDs.Contains(item.strID))
            {
                reason = "already-planned";
                return false;
            }

            var container = item.objContainer;
            if (container.gridLayout == null && !item.HasCond("IsInfiniteContainer"))
            {
                reason = "no-grid";
                return false;
            }

            _containerIDs.Add(item.strID);
            _containers.Add(new ContainerState(container, source ?? "planned"));
            reason = "";
            return true;
        }

        internal bool TryReserve(CondOwner item, out string reason)
        {
            reason = "other";
            if (item == null)
            {
                reason = "null";
                return false;
            }

            var reasons = new List<string>();
            foreach (var container in _containers)
            {
                if (container.TryReserve(item, out var containerReason))
                {
                    _reservedItems++;
                    reason = "";
                    return true;
                }

                if (!string.IsNullOrEmpty(containerReason))
                    reasons.Add(containerReason);
            }

            reason = ChooseFailureReason(reasons);
            return false;
        }

        private static List<ContainerCandidate> FindCarriedContainers(CondOwner hauler)
        {
            var candidates = new List<ContainerCandidate>();
            var seen = new HashSet<string>();

            AddSlotContainers(hauler, candidates, seen);
            AddContainedContainers(hauler, candidates, seen);

            return candidates;
        }

        private static void AddSlotContainers(CondOwner hauler, List<ContainerCandidate> candidates, HashSet<string> seen)
        {
            try
            {
                if (hauler?.compSlots == null)
                    return;

                foreach (var slot in hauler.compSlots.GetSlotsHeldFirst(true))
                {
                    var item = slot?.GetOutermostCO();
                    AddCandidate(item, "slot:" + (slot?.strName ?? "?"), candidates, seen);
                }

                AddCandidate(VanillaHaulBatcher.GetDragSlotItem(hauler), "slot:drag", candidates, seen);
            }
            catch
            {
            }
        }

        private static void AddContainedContainers(CondOwner hauler, List<ContainerCandidate> candidates, HashSet<string> seen)
        {
            try
            {
                if (hauler == null)
                    return;

                foreach (var item in hauler.GetCOsSafe(true, null))
                    AddCandidate(item, "carried", candidates, seen);
            }
            catch
            {
            }
        }

        private static void AddCandidate(CondOwner item, string source, List<ContainerCandidate> candidates, HashSet<string> seen)
        {
            if (item?.objContainer == null || item.bDestroyed || string.IsNullOrEmpty(item.strID))
                return;

            if (item.HasCond("IsInstalled"))
                return;

            var container = item.objContainer;
            var hasGrid = container.gridLayout != null;
            if (!hasGrid && !item.HasCond("IsInfiniteContainer"))
                return;

            if (!seen.Add(item.strID))
                return;

            candidates.Add(new ContainerCandidate
            {
                Owner = item,
                Source = source,
                Score = ContainerScore(item, source)
            });
        }

        private static int ContainerScore(CondOwner item, string source)
        {
            if (item == null)
                return int.MinValue;

            var score = 1000;
            source = source ?? "";

            if (source == "slot:back" || item.HasCond("IsBackpack"))
                score += 5000;

            if (source == "slot:heldL" || source == "slot:heldR" || source == "slot:handL" || source == "slot:handR")
                score += 4000;

            if (source == "slot:drag")
                score += 3000;

            var text = ((item.strNameFriendly ?? "") + " " + (item.strName ?? "") + " " + (item.strCODef ?? "")).ToLowerInvariant();
            if (text.Contains("backpack") || text.Contains("kompart"))
                score += 2000;

            if (text.Contains("crate") || text.Contains("dolly") || text.Contains("cart"))
                score += 1000;

            if (item.objContainer?.gridLayout != null)
                score += Math.Max(0, item.objContainer.gridLayout.gridMaxX * item.objContainer.gridLayout.gridMaxY);

            return score;
        }

        private static string ChooseFailureReason(List<string> reasons)
        {
            if (reasons == null || reasons.Count == 0)
                return "no-container-fit";

            if (reasons.Contains("no-grid-room"))
                return "no-grid-room";

            if (reasons.Contains("too-large"))
                return "too-large";

            if (reasons.Contains("container-reject"))
                return "container-reject";

            return reasons[0];
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

        private static Dictionary<string, List<int>> CloneStacks(Dictionary<string, List<int>> source)
        {
            var clone = new Dictionary<string, List<int>>();
            foreach (var kvp in source)
                clone[kvp.Key] = new List<int>(kvp.Value);

            return clone;
        }

        private static bool CanPlace(bool[,] grid, int x, int y, int width, int height)
        {
            for (var yy = y; yy < y + height; yy++)
            {
                for (var xx = x; xx < x + width; xx++)
                {
                    if (grid[xx, yy])
                        return false;
                }
            }

            return true;
        }

        private static void MarkPlaced(bool[,] grid, int x, int y, int width, int height)
        {
            for (var yy = y; yy < y + height; yy++)
            {
                for (var xx = x; xx < x + width; xx++)
                    grid[xx, yy] = true;
            }
        }

        private static string VanillaHaulBatcherSafeCO(CondOwner co)
        {
            if (co == null)
                return "<null>";

            var name = string.IsNullOrEmpty(co.strNameFriendly) ? co.strName : co.strNameFriendly;
            return (co.strID ?? "?") + "/" + (name ?? "?");
        }

        private sealed class ContainerCandidate
        {
            internal CondOwner Owner;
            internal string Source;
            internal int Score;
        }

        private sealed class ContainerState
        {
            private readonly Container _container;
            private readonly string _source;
            private bool[,] _occupied;
            private Dictionary<string, List<int>> _stackCounts;
            private readonly int _width;
            private readonly int _height;
            private int _reservedItems;

            internal ContainerState(Container container, string source)
            {
                _container = container;
                _source = source ?? "?";

                var grid = container.gridLayout;
                _width = Math.Max(0, grid?.gridMaxX ?? 0);
                _height = Math.Max(0, grid?.gridMaxY ?? 0);
                _occupied = new bool[_width, _height];
                _stackCounts = new Dictionary<string, List<int>>();

                if (grid?.gridID != null)
                {
                    for (var x = 0; x < _width; x++)
                    {
                        for (var y = 0; y < _height; y++)
                        {
                            _occupied[x, y] = !string.IsNullOrEmpty(grid.gridID[x, y]);
                        }
                    }
                }

                SeedDirectStacks();
            }

            internal string Description
            {
                get
                {
                    return _source + ":" + VanillaHaulBatcherSafeCO(_container?.CO)
                        + " grid=" + _width + "x" + _height
                        + " freeCells=" + CountFreeCells()
                        + " reserved=" + _reservedItems;
                }
            }

            internal bool TryReserve(CondOwner item, out string reason)
            {
                reason = "other";
                if (item == null)
                {
                    reason = "null";
                    return false;
                }

                if (_container?.CO == null)
                {
                    reason = "no-container";
                    return false;
                }

                try
                {
                    if (_container.ctAllowed != null && !_container.ctAllowed.Triggered(item))
                    {
                        reason = "container-reject";
                        return false;
                    }
                }
                catch
                {
                    reason = "container-test-failed";
                    return false;
                }

                if (_container.CO.HasCond("IsInfiniteContainer"))
                {
                    _reservedItems++;
                    reason = "";
                    return true;
                }

                var testGrid = (bool[,])_occupied.Clone();
                var testStacks = CloneStacks(_stackCounts);
                var remaining = Math.Max(1, item.StackCount);
                var stackLimit = Math.Max(1, item.nStackLimit);

                if (_container.bAllowStacking && stackLimit > 1)
                {
                    var key = StackKey(item);
                    if (!testStacks.TryGetValue(key, out var counts))
                    {
                        counts = new List<int>();
                        testStacks[key] = counts;
                    }

                    for (var i = 0; i < counts.Count && remaining > 0; i++)
                    {
                        var free = Math.Max(0, stackLimit - counts[i]);
                        var take = Math.Min(free, remaining);
                        counts[i] += take;
                        remaining -= take;
                    }

                    while (remaining > 0)
                    {
                        if (!TryPlaceItemShape(item, testGrid, out reason))
                            return false;

                        var take = Math.Min(stackLimit, remaining);
                        counts.Add(take);
                        remaining -= take;
                    }
                }
                else
                {
                    for (var i = 0; i < remaining; i++)
                    {
                        if (!TryPlaceItemShape(item, testGrid, out reason))
                            return false;
                    }
                }

                _occupied = testGrid;
                _stackCounts = testStacks;
                _reservedItems++;
                reason = "";
                return true;
            }

            private void SeedDirectStacks()
            {
                var owner = _container?.CO;
                if (owner == null)
                    return;

                List<CondOwner> contents;
                try
                {
                    contents = owner.GetCOsSafe(true, null);
                }
                catch
                {
                    return;
                }

                foreach (var item in contents)
                {
                    if (item == null
                        || item.bDestroyed
                        || item.coStackHead != null
                        || item.objCOParent != owner
                        || item.nStackLimit <= 1)
                    {
                        continue;
                    }

                    var key = StackKey(item);
                    if (!_stackCounts.TryGetValue(key, out var counts))
                    {
                        counts = new List<int>();
                        _stackCounts[key] = counts;
                    }

                    counts.Add(Math.Max(1, item.StackCount));
                }
            }

            private bool TryPlaceItemShape(CondOwner item, bool[,] grid, out string reason)
            {
                reason = "shape";
                if (item == null || _width <= 0 || _height <= 0)
                    return false;

                var size = GUIInventoryItem.GetWidthHeightForCO(item);
                var itemWidth = Math.Max(1, size.x);
                var itemHeight = Math.Max(1, size.y);

                if (itemWidth > _width || itemHeight > _height)
                {
                    reason = "too-large";
                    return false;
                }

                for (var y = 0; y <= _height - itemHeight; y++)
                {
                    for (var x = 0; x <= _width - itemWidth; x++)
                    {
                        if (!CanPlace(grid, x, y, itemWidth, itemHeight))
                            continue;

                        MarkPlaced(grid, x, y, itemWidth, itemHeight);
                        reason = "";
                        return true;
                    }
                }

                reason = "no-grid-room";
                return false;
            }

            private int CountFreeCells()
            {
                var free = 0;
                for (var x = 0; x < _width; x++)
                {
                    for (var y = 0; y < _height; y++)
                    {
                        if (!_occupied[x, y])
                            free++;
                    }
                }

                return free;
            }
        }
    }

    [HarmonyPatch(typeof(WorkManager), nameof(WorkManager.CompleteTask), new Type[] { typeof(string), typeof(string), typeof(string) })]
    internal static class CompleteTaskPatch
    {
        private static void Prefix()
        {
            HaulBatchRegistry.BeginCompleteTask();
        }

        private static void Finalizer()
        {
            HaulBatchRegistry.EndCompleteTask();
        }
    }

    [HarmonyPatch(typeof(WorkManager), nameof(WorkManager.RemoveTask), new Type[] { typeof(Task2) })]
    internal static class RemoveTaskPatch
    {
        private static void Prefix(Task2 task)
        {
            try
            {
                HaulBatchRegistry.HandleRemovedTask(task);
            }
            catch (Exception ex)
            {
                Plugin.ModError("[V2HaulCancelCleanup] failed: " + ex);
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
                VanillaHaulBatcher.TryCompact(__instance);
            }
            catch (Exception ex)
            {
                Plugin.ModError("[V2HaulBatch] failed: " + ex);
            }
        }
    }
}
