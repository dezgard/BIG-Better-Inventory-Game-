using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Ostranauts.UI.PDA;
using UnityEngine;

namespace OstranautsHaulingV2
{
    internal enum BigHaulPaintMode
    {
        None,
        Haul,
        Drag
    }

    internal enum BigHaulHelperSlot
    {
        None,
        Hand,
        Drag
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "com.dezgard.ostranauts.haulingv2";
        internal const string PluginName = "BIG Better Inventory Game";
        internal const string PluginVersion = "0.9.5";
        internal const string BigHaulInteraction = "BIGHaulItems";
        internal const string BigHaulMarkerIcon = "BIGIcoHaulMarked";
        private const string LooseButtonIcon = "BIGGUIHaulCart";
        private const string DragButtonIcon = "BIGGUIHaulDrag";
        private const string LooseButtonObjectName = "BIGLooseHaulPrototype_BIG_HAUL";
        private const string LegacyCartButtonObjectName = "BIGLooseHaulPrototype_BIG_CART";
        private const string DragButtonObjectName = "BIGLooseHaulPrototype_BIG_DRAG";

        private static Harmony _harmony;
        private static bool _interactionInstalled;
        private static bool _assetPathRegistered;
        private static BigHaulPaintMode _bigHaulPaintMode = BigHaulPaintMode.None;
        private static bool _bigSelectionDragging;
        private static Vector2 _bigSelectionStart;
        private static Vector2 _bigSelectionEnd;
        private static bool _bigSelectionWorldReady;
        private static Vector3 _bigSelectionWorldStart;
        private static Vector3 _bigSelectionWorldEnd;
        private static Texture2D _cursorTexture;
        private static BigHaulPaintMode _cursorTextureMode = BigHaulPaintMode.None;
        private static float _lastCancelBoundsScanTime = -100f;
        private static string _lastCancelBoundsScanKey = "";
        private float _nextLiveScanTime;
        private float _nextIconRefreshTime;

        internal static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Log = Logger;
            BigLog.Init(PluginVersion);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            ModInfo(PluginName + " " + PluginVersion + " loaded.");
        }

        private void Update()
        {
            if (!_interactionInstalled && IsDataReady())
                InstallCustomInteraction();

            if (_interactionInstalled && Time.realtimeSinceStartup >= _nextIconRefreshTime)
            {
                _nextIconRefreshTime = Time.realtimeSinceStartup + 0.5f;
                BigHaulRegistry.RefreshOwnedIcons();
                BigHaulPlanner.PumpSelectedCrew();
            }

            if (_interactionInstalled && BigHaulPaintActive)
                HandleBigSelectionInput();

            if (!_interactionInstalled || Time.realtimeSinceStartup < _nextLiveScanTime)
                return;

            _nextLiveScanTime = Time.realtimeSinceStartup + 5f;
            var added = AddInteractionToLoadedObjects();
            if (added > 0)
                ModInfo("Added BIG Haul Items to " + added + " loaded loose-item candidates.");
        }

        private void OnGUI()
        {
            DrawBigSelectionBox();
        }

        private void OnDestroy()
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch
            {
                // Shutdown must not throw during game exit.
            }

            BigLog.Close();
        }

        internal static void InstallCustomInteraction()
        {
            if (_interactionInstalled)
                return;

            if (!IsDataReady())
                return;

            try
            {
                EnsureAssetPathRegistered();

                if (!DataHandler.dictInteractions.ContainsKey(BigHaulInteraction))
                {
                    JsonInteraction source = null;
                    DataHandler.dictInteractions.TryGetValue("PickupItemStack", out source);
                    if (source == null)
                    {
                        Warn("Could not create BIG Haul Items: PickupItemStack was missing.");
                        return;
                    }

                    var custom = source.Clone();
                    custom.strName = BigHaulInteraction;
                    custom.strTitle = "BIG Haul Items";
                    custom.strDesc = "[us] marks [them] for BIG loose-item hauling.";
                    custom.strTooltip = "Adds this loose item to BIG's own haul registry.";
                    custom.strActionGroup = "Work";
                    custom.strDuty = null;
                    custom.strMapIcon = BigHaulMarkerIcon;
                    custom.strBubble = "BblTouch";
                    custom.aInverse = Array.Empty<string>();
                    custom.bApplyChain = false;
                    custom.bOpener = true;
                    custom.bHumanOnly = true;
                    custom.bIgnoreFeelings = true;
                    custom.nLogging = 1;

                    DataHandler.dictInteractions[BigHaulInteraction] = custom;
                    ModInfo("BIG custom interaction registered: interaction=" + BigHaulInteraction + " mapIcon=" + BigHaulMarkerIcon + ".");
                }

                _interactionInstalled = true;
                var added = AddInteractionToLoadedObjects();
                ModInfo("Installed BIG Haul Items interaction. Loaded candidates updated: " + added + ".");
            }
            catch (Exception ex)
            {
                Error("Failed to install BIG Haul Items interaction: " + ex);
            }
        }

        internal static int AddInteractionToLoadedObjects()
        {
            var count = 0;

            try
            {
                if (DataHandler.mapCOs == null)
                    return 0;

                foreach (var co in DataHandler.mapCOs.Values)
                {
                    if (TryAddInteractionToCandidate(co))
                        count++;
                }
            }
            catch (Exception ex)
            {
                Warn("Live candidate scan failed: " + ex.Message);
            }

            return count;
        }

        internal static bool TryAddInteractionToCandidate(CondOwner co)
        {
            string rejectReason;
            if (!IsLooseWorldHaulCandidate(co, out rejectReason))
                return false;

            if (co.aInteractions.Contains(BigHaulInteraction))
                return false;

            co.aInteractions.Add(BigHaulInteraction);
            return true;
        }

        internal static bool TryRegisterLooseItem(CondOwner hauler, CondOwner item, out string message)
        {
            message = null;

            if (hauler == null)
            {
                message = "Rejected BIG haul registration: hauler was null.";
                return false;
            }

            if (item == null)
            {
                message = "Rejected BIG haul registration for " + SafeName(hauler) + ": item was null.";
                return false;
            }

            if (!IsCandidateForMode(hauler, item, BigHaulPaintMode.Haul, out var rejectReason))
            {
                message = "Rejected BIG haul registration: " + Describe(item) + " reason=" + rejectReason;
                return false;
            }

            var record = BigHaulRegistry.Register(hauler, item, BigHaulPaintMode.Haul);
            BigHaulRegistry.EnsureIcon(record, item);
            message = "Registered BIG haul item: " + record;
            return true;
        }

        internal static bool BigHaulPaintActive
        {
            get { return _bigHaulPaintMode != BigHaulPaintMode.None; }
        }

        internal static BigHaulPaintMode ActiveBigHaulMode
        {
            get { return _bigHaulPaintMode; }
        }

        internal static void AddBigHaulPanelButton(GUIPDA pda)
        {
            try
            {
                EnsureAssetPathRegistered();

                if (pda == null || pda.goJobTypes == null)
                    return;

                var prefabField = AccessTools.Field(typeof(GUIPDA), "prefabGUIJobItem");
                var prefab = prefabField?.GetValue(pda) as GUIJobItem;
                if (prefab == null)
                {
                    Warn("Could not add BIG HAUL panel button: prefabGUIJobItem was missing.");
                    return;
                }

                var added = 0;
                var refreshed = 0;
                var removed = RemoveLegacyBigPanelButton(pda.goJobTypes.transform, LegacyCartButtonObjectName);

                if (AddOrRefreshBigPanelButton(pda.goJobTypes.transform, prefab, LooseButtonObjectName, "", LooseButtonIcon, StartBigLooseHaulPainting))
                    added++;
                else
                    refreshed++;

                if (AddOrRefreshBigPanelButton(pda.goJobTypes.transform, prefab, DragButtonObjectName, "", DragButtonIcon, StartBigDragHaulPainting))
                    added++;
                else
                    refreshed++;

                ModInfo("BIG panel buttons ready: added=" + added + " refreshed=" + refreshed + " removedLegacy=" + removed + " icons: haul=" + LooseButtonIcon + " drag=" + DragButtonIcon + ".");
            }
            catch (Exception ex)
            {
                Warn("Could not add BIG HAUL panel button: " + ex.Message);
            }
        }

        private static bool AddOrRefreshBigPanelButton(Transform parent, GUIJobItem prefab, string objectName, string label, string iconKey, UnityEngine.Events.UnityAction action)
        {
            var existing = parent.Find(objectName);
            if (existing != null)
            {
                var existingButton = existing.GetComponent<GUIJobItem>();
                if (existingButton != null)
                    existingButton.SetData(label, iconKey, action);

                existing.SetAsLastSibling();
                return false;
            }

            var button = UnityEngine.Object.Instantiate(prefab, parent);
            button.gameObject.name = objectName;
            button.SetData(label, iconKey, action);
            button.transform.SetAsLastSibling();
            return true;
        }

        private static int RemoveLegacyBigPanelButton(Transform parent, string objectName)
        {
            try
            {
                var existing = parent != null ? parent.Find(objectName) : null;
                if (existing == null)
                    return 0;

                UnityEngine.Object.Destroy(existing.gameObject);
                return 1;
            }
            catch (Exception ex)
            {
                Warn("Could not remove legacy BIG panel button " + objectName + ": " + ex.Message);
                return 0;
            }
        }

        internal static void StartBigLooseHaulPainting()
        {
            StartBigHaulPainting(BigHaulPaintMode.Haul);
        }

        internal static void StartBigDragHaulPainting()
        {
            StartBigHaulPainting(BigHaulPaintMode.Drag);
        }

        private static void StartBigHaulPainting(BigHaulPaintMode mode)
        {
            try
            {
                if (mode == BigHaulPaintMode.None)
                    return;

                var sim = CrewSim.objInstance;
                if (sim == null)
                {
                    Warn("Could not start BIG haul panel mode: CrewSim.objInstance was null.");
                    return;
                }

                var oldMode = _bigHaulPaintMode;
                if (oldMode != BigHaulPaintMode.None || _bigSelectionDragging)
                    StopBigHaulPainting("mode-switch-to-" + mode);

                var hauler = CrewSim.GetSelectedCrew();
                ClearVanillaPaintingForBigMode(mode);
                CancelSelectedCrewVanillaHaulTasks(hauler, "big-mode-start:" + mode);

                if (hauler != null)
                    BigHaulPlanner.CancelAllForHauler(hauler, "mode-switch:" + mode, true);

                _bigHaulPaintMode = mode;
                _bigSelectionDragging = false;
                _bigSelectionStart = Vector2.zero;
                _bigSelectionEnd = Vector2.zero;
                _bigSelectionWorldReady = false;
                _bigSelectionWorldStart = Vector3.zero;
                _bigSelectionWorldEnd = Vector3.zero;
                if (oldMode != BigHaulPaintMode.None && oldMode != mode)
                    ModInfo("[BIGModeSwitch] old=" + oldMode + " new=" + mode + ".");
                ModInfo("BIG pure selection mode armed: " + mode + ". Drag a box in-world to register items.");
            }
            catch (Exception ex)
            {
                _bigHaulPaintMode = BigHaulPaintMode.None;
                Error("Could not start BIG haul panel mode: " + ex);
            }
        }

        private static void ClearVanillaPaintingForBigMode(BigHaulPaintMode mode)
        {
            try
            {
                var sim = CrewSim.objInstance;
                if (sim == null)
                    return;

                var previousJob = InstallableSummary(CrewSim.jiLast);
                var hadPaintJob = sim.goPaintJob != null;
                var hadSelectedPart = sim.goSelPart != null;
                sim.FinishPaintingJob();
                CrewSim.jiLast = null;
                CrewSim.bContinuePaintingJob = false;
                sim.goPaintJob = null;
                sim.goSelPart = null;
                var cursorReset = InvokeCrewSimSetCursor(sim, 0);
                var highlightsCleared = InvokeCrewSimNoArg(sim, "ClearHighlightedCos");
                ModInfo("[BIGVanillaPaintCancel] mode=" + mode
                    + " previous=" + previousJob
                    + " hadPaintJob=" + hadPaintJob
                    + " hadSelectedPart=" + hadSelectedPart
                    + " resetJiLast=True cursorReset=" + cursorReset
                    + " highlightsCleared=" + highlightsCleared + ".");
            }
            catch (Exception ex)
            {
                Warn("[BIGVanillaPaintCancel] failed: mode=" + mode + " error=" + ex.Message);
            }
        }

        private static bool InvokeCrewSimSetCursor(CrewSim sim, int cursor)
        {
            try
            {
                if (sim == null)
                    return false;

                var method = AccessTools.Method(typeof(CrewSim), "SetCursor", new[] { typeof(int) });
                if (method == null)
                    return false;

                method.Invoke(sim, new object[] { cursor });
                return true;
            }
            catch (Exception ex)
            {
                Warn("[BIGVanillaPaintCancel] cursor reset failed: " + ex.Message);
                return false;
            }
        }

        private static bool InvokeCrewSimNoArg(CrewSim sim, string methodName)
        {
            try
            {
                if (sim == null || string.IsNullOrEmpty(methodName))
                    return false;

                var method = AccessTools.Method(typeof(CrewSim), methodName);
                if (method == null)
                    return false;

                method.Invoke(sim, null);
                return true;
            }
            catch (Exception ex)
            {
                Warn("[BIGVanillaPaintCancel] " + methodName + " failed: " + ex.Message);
                return false;
            }
        }

        internal static void EnsureAssetPathRegistered()
        {
            if (_assetPathRegistered)
                return;

            try
            {
                if (DataHandler.aModPaths == null)
                    return;

                var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (string.IsNullOrEmpty(pluginDir))
                    return;

                var assetRoot = Path.Combine(pluginDir, "BIGAssets") + Path.DirectorySeparatorChar;
                var imagesDir = Path.Combine(assetRoot, "images");
                if (!Directory.Exists(imagesDir))
                {
                    Warn("BIG icon asset path missing: " + imagesDir);
                    return;
                }

                var normalized = Path.GetFullPath(assetRoot);
                if (!DataHandler.aModPaths.Any(p => string.Equals(Path.GetFullPath(p), normalized, StringComparison.OrdinalIgnoreCase)))
                    DataHandler.aModPaths.Insert(0, normalized);

                _assetPathRegistered = true;
                ModInfo("Registered BIG icon asset path: " + normalized);
                LogIconAssetStatus(imagesDir);
            }
            catch (Exception ex)
            {
                Warn("Could not register BIG icon asset path: " + ex.Message);
            }
        }

        private static void LogIconAssetStatus(string imagesDir)
        {
            LogIconAsset(imagesDir, LooseButtonIcon);
            LogIconAsset(imagesDir, DragButtonIcon);
            LogIconAsset(imagesDir, BigHaulMarkerIcon);
        }

        private static void LogIconAsset(string imagesDir, string iconKey)
        {
            try
            {
                var path = Path.Combine(imagesDir, iconKey + ".png");
                var file = new FileInfo(path);
                if (file.Exists)
                    ModInfo("BIG icon asset found: key=" + iconKey + " file=" + path + " bytes=" + file.Length + ".");
                else
                    Warn("BIG icon asset missing: key=" + iconKey + " file=" + path + ".");
            }
            catch (Exception ex)
            {
                Warn("BIG icon asset check failed: key=" + iconKey + " error=" + ex.Message);
            }
        }

        internal static void StopBigHaulPainting(string reason = "stopped")
        {
            if (_bigHaulPaintMode == BigHaulPaintMode.None && !_bigSelectionDragging)
                return;

            var mode = _bigHaulPaintMode;
            _bigHaulPaintMode = BigHaulPaintMode.None;
            _bigSelectionDragging = false;
            _bigSelectionStart = Vector2.zero;
            _bigSelectionEnd = Vector2.zero;
            _bigSelectionWorldReady = false;
            _bigSelectionWorldStart = Vector3.zero;
            _bigSelectionWorldEnd = Vector3.zero;
            ModInfo("[BIGModeCancel] mode=" + mode + " reason=" + reason + ".");
        }

        private static void HandleBigSelectionInput()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    StopBigHaulPainting("escape");
                    return;
                }

                if (Input.GetMouseButtonDown(1))
                {
                    StopBigHaulPainting("right-click");
                    return;
                }

                if (Input.GetMouseButtonDown(0))
                {
                    _bigSelectionDragging = true;
                    _bigSelectionStart = Input.mousePosition;
                    _bigSelectionEnd = _bigSelectionStart;
                    _bigSelectionWorldReady = TryGetSelectionWorldPoint(_bigSelectionStart, out _bigSelectionWorldStart);
                    _bigSelectionWorldEnd = _bigSelectionWorldStart;
                    ModInfo("BIG pure selection drag started: mode=" + _bigHaulPaintMode + " start=" + ScreenPoint(_bigSelectionStart) + " worldAnchored=" + _bigSelectionWorldReady + ".");
                    return;
                }

                if (_bigSelectionDragging && Input.GetMouseButton(0))
                {
                    _bigSelectionEnd = Input.mousePosition;
                    UpdateSelectionWorldEnd();
                    return;
                }

                if (_bigSelectionDragging && Input.GetMouseButtonUp(0))
                {
                    _bigSelectionEnd = Input.mousePosition;
                    UpdateSelectionWorldEnd();
                    var mode = _bigHaulPaintMode;
                    var hauler = CrewSim.GetSelectedCrew();
                    var summary = RegisterSelectionBox(hauler, mode, _bigSelectionStart, _bigSelectionEnd);
                    _bigSelectionDragging = false;
                    _bigSelectionStart = Vector2.zero;
                    _bigSelectionEnd = Vector2.zero;
                    _bigSelectionWorldReady = false;
                    _bigSelectionWorldStart = Vector3.zero;
                    _bigSelectionWorldEnd = Vector3.zero;
                    ModInfo("[BIGModeHold] mode=" + mode + " reason=selection-complete.");
                    ModInfo(summary);
                }
            }
            catch (Exception ex)
            {
                Error("BIG pure selection input failed: " + ex);
                StopBigHaulPainting("input-error");
            }
        }

        private static void DrawBigSelectionBox()
        {
            if (!BigHaulPaintActive)
                return;

            DrawBigCursorIcon();

            if (_bigSelectionDragging)
            {
                Rect rect;
                if (!TryGetSelectionGuiRect(out rect))
                    rect = ToGuiRect(_bigSelectionStart, _bigSelectionEnd);

                if (rect.width < 2f || rect.height < 2f)
                    return;

                var modeColor = ModeColor(_bigHaulPaintMode);
                var old = GUI.color;
                GUI.color = new Color(modeColor.r, modeColor.g, modeColor.b, 0.25f);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = new Color(modeColor.r, modeColor.g, modeColor.b, 0.95f);
                DrawRectBorder(rect, 2f);
                GUI.color = old;
            }
        }

        private static void DrawBigCursorIcon()
        {
            try
            {
                var texture = GetCursorTextureForMode(_bigHaulPaintMode);
                var mouse = Event.current.mousePosition;
                var modeColor = ModeColor(_bigHaulPaintMode);
                var iconRect = new Rect(mouse.x + 10f, mouse.y + 10f, 36f, 36f);
                var labelRect = new Rect(mouse.x - 2f, mouse.y + 48f, 72f, 18f);
                var old = GUI.color;

                GUI.color = new Color(0f, 0f, 0f, 0.68f);
                GUI.DrawTexture(iconRect, Texture2D.whiteTexture);
                GUI.color = new Color(modeColor.r, modeColor.g, modeColor.b, 0.95f);
                DrawRectBorder(iconRect, 1f);

                if (texture != null)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(iconRect.x + 2f, iconRect.y + 2f, 32f, 32f), texture);
                }

                GUI.color = new Color(0f, 0f, 0f, 0.68f);
                GUI.DrawTexture(labelRect, Texture2D.whiteTexture);
                var style = new GUIStyle(GUI.skin.label);
                style.normal.textColor = new Color(modeColor.r, modeColor.g, modeColor.b, 1f);
                style.fontStyle = FontStyle.Bold;
                style.alignment = TextAnchor.MiddleCenter;
                GUI.Label(labelRect, ModeLabel(_bigHaulPaintMode).ToUpperInvariant(), style);
                GUI.color = old;
            }
            catch
            {
                // Cursor feedback must not affect hauling.
            }
        }

        private static Texture2D GetCursorTextureForMode(BigHaulPaintMode mode)
        {
            if (_cursorTexture != null && _cursorTextureMode == mode)
                return _cursorTexture;

            _cursorTexture = null;
            _cursorTextureMode = mode;

            var iconKey = IconKeyForMode(mode);
            if (string.IsNullOrEmpty(iconKey))
                return null;

            try
            {
                Texture2D texture;
                if (DataHandler.dictImages == null || !DataHandler.dictImages.TryGetValue(iconKey, out texture))
                    return null;

                _cursorTexture = texture;
                return _cursorTexture;
            }
            catch (Exception ex)
            {
                Warn("BIG cursor icon load failed: mode=" + mode + " error=" + ex.Message);
                return null;
            }
        }

        private static string IconKeyForMode(BigHaulPaintMode mode)
        {
            switch (mode)
            {
                case BigHaulPaintMode.Haul:
                    return LooseButtonIcon;
                case BigHaulPaintMode.Drag:
                    return DragButtonIcon;
                default:
                    return null;
            }
        }

        private static Color ModeColor(BigHaulPaintMode mode)
        {
            switch (mode)
            {
                case BigHaulPaintMode.Haul:
                    return new Color(0.75f, 1f, 0.15f, 1f);
                case BigHaulPaintMode.Drag:
                    return new Color(1f, 0.55f, 0.15f, 1f);
                default:
                    return Color.white;
            }
        }

        private static void DrawRectBorder(Rect rect, float thickness)
        {
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), Texture2D.whiteTexture);
        }

        private static Rect ToGuiRect(Vector2 start, Vector2 end)
        {
            var xMin = Math.Min(start.x, end.x);
            var xMax = Math.Max(start.x, end.x);
            var yMin = Math.Min(Screen.height - start.y, Screen.height - end.y);
            var yMax = Math.Max(Screen.height - start.y, Screen.height - end.y);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static Rect ToScreenRect(Vector2 start, Vector2 end)
        {
            var xMin = Math.Min(start.x, end.x);
            var xMax = Math.Max(start.x, end.x);
            var yMin = Math.Min(start.y, end.y);
            var yMax = Math.Max(start.y, end.y);

            if (xMax - xMin < 8f)
            {
                xMin -= 4f;
                xMax += 4f;
            }

            if (yMax - yMin < 8f)
            {
                yMin -= 4f;
                yMax += 4f;
            }

            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static bool TryGetSelectionWorldPoint(Vector2 screen, out Vector3 world)
        {
            world = Vector3.zero;

            try
            {
                var sim = CrewSim.objInstance;
                if (sim == null)
                    return false;

                var camera = GetCrewSimCamera(sim);
                if (camera == null)
                    return false;

                world = ScreenToWorldOnPlane(camera, screen, new Plane(Vector3.forward, Vector3.zero));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void UpdateSelectionWorldEnd()
        {
            if (!_bigSelectionWorldReady)
                return;

            Vector3 worldEnd;
            if (TryGetSelectionWorldPoint(_bigSelectionEnd, out worldEnd))
                _bigSelectionWorldEnd = worldEnd;
        }

        private static bool TryGetSelectionGuiRect(out Rect rect)
        {
            rect = default(Rect);

            if (!_bigSelectionWorldReady)
                return false;

            Vector2 start;
            Vector2 end;
            if (!TryWorldToSelectionScreenPoint(_bigSelectionWorldStart, out start) || !TryWorldToSelectionScreenPoint(_bigSelectionWorldEnd, out end))
                return false;

            rect = ToGuiRect(start, end);
            return true;
        }

        private static bool TryWorldToSelectionScreenPoint(Vector3 world, out Vector2 screen)
        {
            screen = Vector2.zero;

            try
            {
                var sim = CrewSim.objInstance;
                if (sim == null)
                    return false;

                var camera = GetCrewSimCamera(sim);
                if (camera == null)
                    return false;

                var point = camera.WorldToScreenPoint(world);
                if (point.z < 0f)
                    return false;

                screen = new Vector2(point.x, point.y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Bounds WorldSelectionBounds(Vector3 start, Vector3 end)
        {
            var min = Vector3.Min(start, end);
            var max = Vector3.Max(start, end);
            return FlattenFor2D(new Bounds((min + max) * 0.5f, max - min));
        }

        private static string RegisterSelectionBox(CondOwner hauler, BigHaulPaintMode mode, Vector2 start, Vector2 end)
        {
            if (hauler == null)
                return "BIG pure selection failed: no selected crew.";

            if (DataHandler.mapCOs == null)
                return "BIG pure selection failed: DataHandler.mapCOs was null.";

            Bounds selectionBounds;
            string cameraInfo;
            if (_bigSelectionWorldReady)
            {
                selectionBounds = WorldSelectionBounds(_bigSelectionWorldStart, _bigSelectionWorldEnd);
                var camera = GetCrewSimCamera(CrewSim.objInstance);
                cameraInfo = (camera != null ? CameraSummary(camera) : "<none>") + " conversion=world-anchored-selection";
            }
            else if (!TryGetSelectionWorldBounds(start, end, out selectionBounds, out cameraInfo))
            {
                return "BIG pure selection failed: could not calculate world bounds.";
            }

            var seen = new HashSet<string>();
            var accepted = 0;
            var rejected = 0;
            var outside = 0;
            var noBounds = 0;
            var containerBlocked = 0;
            var queued = 0;
            var firstRejects = new List<string>();
            var selectedItems = new List<CondOwner>();

            foreach (var item in DataHandler.mapCOs.Values.ToList())
            {
                if (item == null || item == hauler)
                    continue;

                var id = SafeValue(() => item.strID);
                if (!string.IsNullOrEmpty(id) && id != "<null>" && !seen.Add(id))
                    continue;

                if (HasContainerOwner(item))
                {
                    containerBlocked++;
                    continue;
                }

                Bounds itemBounds;
                if (!TryGetItemWorldBounds(item, out itemBounds))
                {
                    noBounds++;
                    continue;
                }

                if (!Overlaps2D(selectionBounds, itemBounds))
                {
                    outside++;
                    continue;
                }

                string dragRejectReason;
                if (!IsDragSelectableWorldObject(item, out dragRejectReason))
                {
                    rejected++;
                    if (firstRejects.Count < 8)
                        firstRejects.Add("Rejected BIG " + ModeLabel(mode) + " registration: " + Describe(item) + " reason=" + dragRejectReason);
                    continue;
                }

                string message;
                if (TryRegisterItemForMode(hauler, item, mode, out message))
                {
                    accepted++;
                    selectedItems.Add(item);
                    ModInfo("BIG pure selection captured: " + message);
                }
                else
                {
                    rejected++;
                    if (firstRejects.Count < 8)
                        firstRejects.Add(message);
                }
            }

            foreach (var reject in firstRejects)
                Warn("BIG pure selection rejected: " + reject);

            if (accepted == 0)
                LogSelectionMissSamples(selectionBounds);

            if (mode == BigHaulPaintMode.Haul && selectedItems.Count > 0)
                queued = BigHaulPlanner.StartLooseSession(hauler, selectedItems);
            else if (mode == BigHaulPaintMode.Drag && selectedItems.Count > 0)
                queued = BigHaulPlanner.StartDragSession(hauler, selectedItems);
            else if (selectedItems.Count > 0)
                Warn("BIG " + mode + " selection is marker-only in this prototype. No vanilla haul tasks were queued.");

            return "BIG pure selection complete: mode=" + mode
                + " hauler=" + SafeName(hauler)
                + " rect=" + ScreenPoint(start) + "->" + ScreenPoint(end)
                + " camera=" + cameraInfo
                + " bounds=" + BoundsSummary(selectionBounds)
                + " accepted=" + accepted
                + " queued=" + queued
                + " rejected=" + rejected
                + " containerBlocked=" + containerBlocked
                + " outside=" + outside
                + " noBounds=" + noBounds
                + " queue=" + QueueSummary(hauler) + ".";
        }

        internal static int CancelBigItemsInBounds(CondOwner hauler, Bounds selectionBounds, string reason)
        {
            if (DataHandler.mapCOs == null)
                return 0;

            selectionBounds = FlattenFor2D(selectionBounds);
            var seen = new HashSet<string>();
            var tracked = 0;
            var matched = 0;
            var cancelled = 0;
            var noBounds = 0;

            foreach (var item in DataHandler.mapCOs.Values.ToList())
            {
                if (item == null)
                    continue;

                var id = SafeValue(() => item.strID);
                if (string.IsNullOrEmpty(id) || id == "<null>" || !seen.Add(id))
                    continue;

                if (!BigHaulRegistry.IsTracked(item))
                    continue;

                tracked++;
                Bounds itemBounds;
                if (!TryGetItemWorldBounds(item, out itemBounds))
                {
                    noBounds++;
                    continue;
                }

                if (!Overlaps2D(selectionBounds, itemBounds))
                    continue;

                matched++;
                if (BigHaulPlanner.CancelPendingItemById(id, reason))
                    cancelled++;
            }

            ModInfo("[BIGCancelBounds] hauler=" + SafeName(hauler)
                + " reason=" + reason
                + " bounds=" + BoundsSummary(selectionBounds)
                + " tracked=" + tracked
                + " matched=" + matched
                + " cancelled=" + cancelled
                + " noBounds=" + noBounds + ".");
            return cancelled;
        }

        internal static bool TryCancelBigItemsFromCurrentVanillaSelection(string reason)
        {
            try
            {
                var sim = CrewSim.objInstance;
                if (sim == null)
                    return false;

                var start = new Vector2(sim.vDragStartScreen.x, sim.vDragStartScreen.y);
                var end = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                var key = ScreenPoint(start) + "->" + ScreenPoint(end);
                var now = Time.realtimeSinceStartup;
                if (key == _lastCancelBoundsScanKey && now - _lastCancelBoundsScanTime < 0.25f)
                    return false;

                Bounds bounds;
                string cameraInfo;
                if (!TryGetSelectionWorldBounds(start, end, out bounds, out cameraInfo))
                {
                    Warn("[BIGCancelBounds] failed to calculate vanilla cancel bounds from screen selection " + key + ".");
                    return false;
                }

                _lastCancelBoundsScanKey = key;
                _lastCancelBoundsScanTime = now;
                var hauler = CrewSim.GetSelectedCrew();
                var cancelled = CancelBigItemsInBounds(hauler, bounds, reason);
                ModInfo("[BIGCancelBounds] source=vanilla-current-selection rect=" + key + " camera=" + cameraInfo + " cancelled=" + cancelled + ".");
                return cancelled > 0;
            }
            catch (Exception ex)
            {
                Warn("[BIGCancelBounds] vanilla current selection failed: " + ex.Message);
                return false;
            }
        }

        internal static bool IsVanillaCancelPaintJob(CrewSim sim)
        {
            try
            {
                var ji = CrewSim.jiLast;
                if (sim == null || ji == null)
                    return false;

                return ContainsCancelToken(ji.strName)
                    || ContainsCancelToken(ji.strJobType)
                    || ContainsCancelToken(ji.strInteractionName)
                    || ContainsCancelToken(ji.strInteractionTemplate)
                    || ContainsCancelToken(ji.strActionGroup)
                    || ContainsCancelToken(ji.strActionCO)
                    || ContainsCancelToken(ji.strBuildType);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsVanillaHaulPaintJob(JsonInstallable ji)
        {
            try
            {
                if (ji == null)
                    return false;

                return ContainsHaulToken(ji.strName)
                    || ContainsHaulToken(ji.strJobType)
                    || ContainsHaulToken(ji.strInteractionName)
                    || ContainsHaulToken(ji.strInteractionTemplate)
                    || ContainsHaulToken(ji.strActionGroup)
                    || ContainsHaulToken(ji.strActionCO)
                    || ContainsHaulToken(ji.strBuildType);
            }
            catch
            {
                return false;
            }
        }

        internal static int CancelSelectedCrewVanillaHaulTasks(CondOwner hauler, string reason)
        {
            try
            {
                var sim = CrewSim.objInstance;
                var workManager = sim != null ? sim.workManager : null;
                if (workManager == null)
                    return 0;

                var tasks = workManager.GetAllTasks();
                if (tasks == null || tasks.Count == 0)
                    return 0;

                var considered = 0;
                var cancelled = 0;

                foreach (var task in tasks.ToList())
                {
                    if (!IsVanillaHaulTask(task))
                        continue;

                    considered++;
                    workManager.RemoveTask(task);
                    cancelled++;

                    if (cancelled <= 6)
                        ModInfo("[BIGVanillaHaulCancelItem] reason=" + reason + " task=" + DescribeVanillaTask(task) + ".");
                }

                if (considered > 0 || cancelled > 0)
                    ModInfo("[BIGVanillaHaulCancel] hauler=" + SafeName(hauler)
                        + " reason=" + reason
                        + " considered=" + considered
                        + " cancelled=" + cancelled
                        + " skipped=0.");

                return cancelled;
            }
            catch (Exception ex)
            {
                Warn("[BIGVanillaHaulCancel] failed: reason=" + reason + " error=" + ex.Message);
                return 0;
            }
        }

        private static bool IsVanillaHaulTask(Task2 task)
        {
            return task != null
                && string.Equals(task.strDuty, "Haul", StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.strInteraction, "ACTHaulItem", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeVanillaTask(Task2 task)
        {
            if (task == null)
                return "<null>";

            return "name=" + (task.strName ?? "<unnamed>")
                + " duty=" + (task.strDuty ?? "<null>")
                + " interaction=" + (task.strInteraction ?? "<null>")
                + " targetId=" + (task.strTargetCOID ?? "<null>")
                + " manual=" + task.bManual;
        }

        internal static string InstallableSummary(JsonInstallable ji)
        {
            if (ji == null)
                return "<null>";

            return "name=" + (ji.strName ?? "<null>")
                + " job=" + (ji.strJobType ?? "<null>")
                + " ia=" + (ji.strInteractionName ?? "<null>")
                + " template=" + (ji.strInteractionTemplate ?? "<null>")
                + " actionCO=" + (ji.strActionCO ?? "<null>");
        }

        private static bool ContainsCancelToken(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsHaulToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return value.IndexOf("haul", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("ACTHaulItem", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryGetSelectionWorldBounds(Vector2 start, Vector2 end, out Bounds bounds, out string cameraInfo)
        {
            bounds = default(Bounds);
            cameraInfo = "<none>";

            try
            {
                var sim = CrewSim.objInstance;
                if (sim == null)
                    return false;

                var camera = GetCrewSimCamera(sim);
                if (camera == null)
                    return false;

                bounds = ManualScreenToWorldBounds(camera, start, end);
                bounds = FlattenFor2D(bounds);
                cameraInfo = CameraSummary(camera) + " conversion=manual-screen-to-world";
                return true;
            }
            catch (Exception ex)
            {
                Warn("BIG selection world bounds failed: " + ex.Message);
                return false;
            }
        }

        private static Camera GetCrewSimCamera(CrewSim sim)
        {
            try
            {
                var active = AccessTools.Field(typeof(CrewSim), "ActiveCam")?.GetValue(sim) as Camera;
                if (active != null && active.isActiveAndEnabled)
                    return active;

                var main = AccessTools.Field(typeof(CrewSim), "camMain")?.GetValue(sim) as Camera;
                if (main != null && main.isActiveAndEnabled)
                    return main;

                if (Camera.main != null && Camera.main.isActiveAndEnabled)
                    return Camera.main;

                return UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
                    .FirstOrDefault(camera => camera != null && camera.isActiveAndEnabled);
            }
            catch
            {
                return null;
            }
        }

        private static Bounds ManualScreenToWorldBounds(Camera camera, Vector2 start, Vector2 end)
        {
            var plane = new Plane(Vector3.forward, Vector3.zero);
            var p1 = ScreenToWorldOnPlane(camera, start, plane);
            var p2 = ScreenToWorldOnPlane(camera, end, plane);
            var min = Vector3.Min(p1, p2);
            var max = Vector3.Max(p1, p2);
            return FlattenFor2D(new Bounds((min + max) * 0.5f, max - min));
        }

        private static Vector3 ScreenToWorldOnPlane(Camera camera, Vector2 screen, Plane plane)
        {
            var ray = camera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            float enter;
            if (plane.Raycast(ray, out enter))
                return ray.GetPoint(enter);

            var fallbackDistance = camera.orthographic
                ? Mathf.Abs(camera.transform.position.z)
                : Mathf.Max(1f, Mathf.Abs(camera.transform.position.z));

            return camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, fallbackDistance));
        }

        private static Bounds FlattenFor2D(Bounds bounds)
        {
            var size = bounds.size;
            if (Math.Abs(size.x) < 0.25f)
                size.x = 0.25f;
            if (Math.Abs(size.y) < 0.25f)
                size.y = 0.25f;
            size.z = 10000f;

            return new Bounds(bounds.center, size);
        }

        private static bool TryGetItemWorldBounds(CondOwner item, out Bounds bounds)
        {
            bounds = default(Bounds);

            try
            {
                if (item == null || item.gameObject == null)
                    return false;

                var renderers = item.gameObject.GetComponentsInChildren<Renderer>();
                var hasBounds = false;

                foreach (var renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled)
                        continue;

                    if (!hasBounds)
                        bounds = renderer.bounds;
                    else
                        bounds.Encapsulate(renderer.bounds);

                    hasBounds = true;
                }

                if (!hasBounds)
                {
                    var tf = item.tf != null ? item.tf : item.gameObject.transform;
                    if (tf == null)
                        return false;

                    bounds = new Bounds(tf.position, new Vector3(0.5f, 0.5f, 10000f));
                }

                bounds = FlattenFor2D(bounds);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool Overlaps2D(Bounds a, Bounds b)
        {
            return a.min.x <= b.max.x
                && a.max.x >= b.min.x
                && a.min.y <= b.max.y
                && a.max.y >= b.min.y;
        }

        private static string BoundsSummary(Bounds bounds)
        {
            return "center=(" + bounds.center.x.ToString("0.0") + "," + bounds.center.y.ToString("0.0") + ")"
                + " size=(" + bounds.size.x.ToString("0.0") + "," + bounds.size.y.ToString("0.0") + ")";
        }

        private static string CameraSummary(Camera camera)
        {
            if (camera == null)
                return "<none>";

            return camera.name
                + " pos=(" + camera.transform.position.x.ToString("0.0") + "," + camera.transform.position.y.ToString("0.0") + "," + camera.transform.position.z.ToString("0.0") + ")"
                + " ortho=" + camera.orthographic
                + " size=" + camera.orthographicSize.ToString("0.0");
        }

        private static void LogSelectionMissSamples(Bounds selectionBounds)
        {
            try
            {
                if (DataHandler.mapCOs == null)
                    return;

                var samples = new List<string>();
                foreach (var item in DataHandler.mapCOs.Values)
                {
                    if (item == null)
                        continue;

                    Bounds itemBounds;
                    if (!TryGetItemWorldBounds(item, out itemBounds))
                        continue;

                    var center = itemBounds.center;
                    var dx = center.x - selectionBounds.center.x;
                    var dy = center.y - selectionBounds.center.y;
                    var dist = Mathf.Sqrt(dx * dx + dy * dy);
                    samples.Add(dist.ToString("0.0") + " " + Describe(item) + " pos=(" + center.x.ToString("0.0") + "," + center.y.ToString("0.0") + ") size=(" + itemBounds.size.x.ToString("0.0") + "," + itemBounds.size.y.ToString("0.0") + ")");
                }

                foreach (var sample in samples.OrderBy(s => float.Parse(s.Split(' ')[0])).Take(8))
                    ModInfo("BIG selection miss nearest: " + sample);
            }
            catch (Exception ex)
            {
                Warn("BIG selection miss sample logging failed: " + ex.Message);
            }
        }

        private static string ScreenPoint(Vector2 point)
        {
            return "(" + Mathf.RoundToInt(point.x) + "," + Mathf.RoundToInt(point.y) + ")";
        }

        internal static bool TryRegisterPanelHaulTask(Task2 task, out string message)
        {
            message = null;

            if (task == null)
            {
                message = "Rejected BIG panel haul registration: task was null.";
                return false;
            }

            if (string.IsNullOrEmpty(task.strTargetCOID))
            {
                message = "Rejected BIG panel haul registration: task target was missing.";
                return false;
            }

            CondOwner item;
            if (DataHandler.mapCOs == null || !DataHandler.mapCOs.TryGetValue(task.strTargetCOID, out item))
            {
                message = "Rejected BIG panel haul registration: target " + task.strTargetCOID + " was not loaded.";
                return false;
            }

            var hauler = CrewSim.GetSelectedCrew();
            return TryRegisterItemForMode(hauler, item, ActiveBigHaulMode, out message);
        }

        private static bool TryRegisterItemForMode(CondOwner hauler, CondOwner item, BigHaulPaintMode mode, out string message)
        {
            message = null;

            if (hauler == null)
            {
                message = "Rejected BIG " + ModeLabel(mode) + " registration: hauler was null.";
                return false;
            }

            if (item == null)
            {
                message = "Rejected BIG " + ModeLabel(mode) + " registration for " + SafeName(hauler) + ": item was null.";
                return false;
            }

            if (!IsCandidateForMode(hauler, item, mode, out var rejectReason))
            {
                message = "Rejected BIG " + ModeLabel(mode) + " registration: " + Describe(item) + " reason=" + rejectReason;
                return false;
            }

            var record = BigHaulRegistry.Register(hauler, item, mode);
            BigHaulRegistry.EnsureIcon(record, item);
            message = "Registered BIG " + ModeLabel(mode) + " item: " + record;
            return true;
        }

        internal static void QueuePickupOnlyIfLoose(CondOwner hauler, CondOwner item, string source)
        {
            try
            {
                if (hauler == null || item == null)
                {
                    Warn("Pickup-only queue skipped: hauler or item was null. source=" + source);
                    return;
                }

                var pickup = GetPickupInteractionForLooseItem(item);
                if (pickup == null)
                {
                    Warn("Pickup-only queue skipped: no pickup interaction was available. item=" + Describe(item));
                    return;
                }

                pickup.bManual = true;
                var queued = hauler.QueueInteraction(item, pickup);
                if (queued)
                    ModInfo("Pickup-only queued: source=" + source + " hauler=" + SafeName(hauler) + " item=" + Describe(item) + " queue=" + QueueSummary(hauler));
                else
                    Warn("Pickup-only queue failed: source=" + source + " hauler=" + SafeName(hauler) + " item=" + Describe(item) + " queue=" + QueueSummary(hauler));
            }
            catch (Exception ex)
            {
                Error("Pickup-only queue crashed: source=" + source + " item=" + Describe(item) + " error=" + ex);
            }
        }

        internal static string QueueSummary(CondOwner co)
        {
            try
            {
                if (co?.aQueue == null)
                    return "<no queue>";

                var count = co.aQueue.Count;
                var max = Math.Min(count, 8);
                var parts = new List<string>();
                for (var i = 0; i < max; i++)
                {
                    var interaction = co.aQueue[i];
                    parts.Add(i + ":" + (interaction?.strName ?? "<null>")
                        + "->" + SafeName(interaction?.objThem));
                }

                if (count > max)
                    parts.Add("+" + (count - max) + " more");

                return "count=" + count + " [" + string.Join(", ", parts.ToArray()) + "]";
            }
            catch (Exception ex)
            {
                return "<queue summary failed: " + ex.Message + ">";
            }
        }

        internal static bool IsCandidateForMode(CondOwner hauler, CondOwner item, BigHaulPaintMode mode, out string reason)
        {
            reason = null;

            if (mode == BigHaulPaintMode.None)
            {
                reason = "no BIG panel mode active";
                return false;
            }

            if (HasAnyCond(item, "IsInstalled", "IsCarried", "IsSystem", "IsHuman", "IsRobot"))
            {
                reason = "installed/carried/system/person";
                return false;
            }

            if (!IsDirectWorldObject(item, out reason))
                return false;

            var canDrag = CanDragWorldObject(item);
            var looseHaulItem = IsLooseHaulItem(item, out var looseRejectReason);
            var helperHaulItem = IsHelperHaulItem(hauler, item, out var helperRejectReason);
            BigHaulHelperSlot helperSlot;
            string sessionHelperReason;
            var sessionHelper = IsSessionHelperCandidate(item, out helperSlot, out sessionHelperReason);
            var waitingForHelper = IsHaulCandidateIgnoringCurrentCapacity(item, out var waitingReason);

            switch (mode)
            {
                case BigHaulPaintMode.Haul:
                    if (looseHaulItem)
                        return true;

                    if (helperHaulItem)
                        return true;

                    if (sessionHelper)
                        return true;

                    if (waitingForHelper)
                        return true;

                    reason = sessionHelperReason ?? helperRejectReason ?? waitingReason ?? looseRejectReason;
                    return false;

                case BigHaulPaintMode.Drag:
                    if (helperSlot != BigHaulHelperSlot.None)
                    {
                        reason = "crate/dolly helper belongs to BIG HAUL";
                        return false;
                    }

                    if (looseHaulItem)
                    {
                        reason = "loose item belongs to BIG HAUL";
                        return false;
                    }

                    if (canDrag)
                        return true;

                    reason = "no drag interaction or drag slot";
                    return false;

                default:
                    reason = "unknown BIG panel mode";
                return false;
            }
        }

        internal static bool IsHaulCandidateIgnoringCurrentCapacity(CondOwner item, out string reason)
        {
            reason = null;

            if (item == null)
            {
                reason = "null item";
                return false;
            }

            if (item.bDestroyed)
            {
                reason = "destroyed";
                return false;
            }

            if (HasAnyCond(item, "IsInstalled", "IsCarried", "IsSystem", "IsHuman", "IsRobot"))
            {
                reason = "installed/carried/system/person";
                return false;
            }

            if (!IsDirectWorldObject(item, out reason))
                return false;

            if (IsLooseHaulItem(item, out reason))
                return true;

            if (IsLiquidItem(item))
            {
                reason = "liquid contents are not safe for BIG HAUL helper loading yet";
                return false;
            }

            if (!HasAnyPickupInteraction(item))
            {
                reason = "missing PickupItem/PickupItemStack for helper loading";
                return false;
            }

            if (HasAnyCond(item, "IsCrate", "IsDolly"))
            {
                reason = "crate/dolly helper item is not cargo";
                return false;
            }

            reason = "helper cargo waiting for crate/dolly space";
            return true;
        }

        internal static bool IsLooseWorldHaulCandidate(CondOwner item, out string reason)
        {
            reason = null;

            if (item == null)
            {
                reason = "null item";
                return false;
            }

            if (item.bDestroyed)
            {
                reason = "destroyed";
                return false;
            }

            if (HasAnyCond(item, "IsInstalled", "IsCarried", "IsSystem", "IsHuman", "IsRobot"))
            {
                reason = "installed/carried/system/person";
                return false;
            }

            if (!IsDirectWorldObject(item, out reason))
                return false;

            return IsLooseHaulItem(item, out reason);
        }

        internal static bool IsDragSelectableWorldObject(CondOwner item, out string reason)
        {
            return IsDirectWorldObject(item, out reason);
        }

        private static bool HasContainerOwner(CondOwner item)
        {
            try
            {
                return item != null && item.objCOParent != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDirectWorldObject(CondOwner item, out string reason)
        {
            reason = null;

            if (item == null)
            {
                reason = "null item";
                return false;
            }

            if (item.objCOParent != null)
            {
                reason = "inside container/owner " + SafeName(item.objCOParent);
                return false;
            }

            if (item.gameObject == null || !item.gameObject.activeInHierarchy)
            {
                reason = "inactive object";
                return false;
            }

            try
            {
                if (!item.Visible)
                {
                    reason = "hidden object";
                    return false;
                }
            }
            catch
            {
                reason = "visibility check failed";
                return false;
            }

            if (!HasValidWorldTile(item))
            {
                reason = "no valid world tile";
                return false;
            }

            return true;
        }

        private static bool HasValidWorldTile(CondOwner item)
        {
            try
            {
                if (item == null || item.ship == null || item.tf == null)
                    return false;

                return item.ship.GetTileAtWorldCoords1(item.tf.position.x, item.tf.position.y, true) != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLiquidItem(CondOwner item)
        {
            try
            {
                var itemDef = item != null ? item.strItemDef : null;
                var coDef = item != null ? item.strCODef : null;
                return StartsWithLiquidDef(itemDef) || StartsWithLiquidDef(coDef);
            }
            catch
            {
                return false;
            }
        }

        private static bool StartsWithLiquidDef(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.StartsWith("ItmLiquid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasInteraction(CondOwner item, string interaction)
        {
            return item != null
                && item.aInteractions != null
                && item.aInteractions.Contains(interaction);
        }

        private static bool HasAnyPickupInteraction(CondOwner item)
        {
            return HasInteraction(item, "PickupItemStack")
                || HasInteraction(item, "PickupItem");
        }

        private static bool IsLooseHaulItem(CondOwner item, out string reason)
        {
            reason = null;

            if (IsLiquidItem(item))
            {
                reason = "liquid contents are not safe for BIG HAUL yet";
                return false;
            }

            if (!HasAnyPickupInteraction(item))
            {
                reason = "missing PickupItem/PickupItemStack";
                return false;
            }

            if (HasAnyCond(item, "IsCumbersome", "IsOversized", "IsCrate", "IsDolly"))
            {
                reason = "bulky/helper item needs a carried haul helper or BIG DRAG";
                return false;
            }

            return true;
        }

        private static bool IsHelperHaulItem(CondOwner hauler, CondOwner item, out string reason)
        {
            reason = null;

            if (item == null)
            {
                reason = "null item";
                return false;
            }

            if (IsLiquidItem(item))
            {
                reason = "liquid contents are not safe for BIG HAUL helper loading yet";
                return false;
            }

            if (!HasAnyPickupInteraction(item))
            {
                reason = "missing PickupItem/PickupItemStack for helper loading";
                return false;
            }

            if (HasAnyCond(item, "IsCrate", "IsDolly"))
            {
                reason = "crate/dolly helper item is not cargo";
                return false;
            }

            string fitReason;
            if (!CanFitHaulHelper(hauler, item, out fitReason))
            {
                reason = fitReason;
                return false;
            }

            return true;
        }

        internal static BigHaulHelperSlot GetSessionHelperSlot(CondOwner item)
        {
            if (HasAnyCond(item, "IsDolly"))
                return BigHaulHelperSlot.Drag;

            if (HasAnyCond(item, "IsCrate"))
                return BigHaulHelperSlot.Hand;

            return BigHaulHelperSlot.None;
        }

        internal static bool IsSessionHelperCandidate(CondOwner item, out BigHaulHelperSlot slot, out string reason)
        {
            reason = null;
            slot = GetSessionHelperSlot(item);

            if (slot == BigHaulHelperSlot.None)
                return false;

            if (item == null)
            {
                reason = "null helper";
                return false;
            }

            if (item.bDestroyed)
            {
                reason = "destroyed helper";
                return false;
            }

            if (HasAnyCond(item, "IsInstalled", "IsCarried", "IsSystem", "IsHuman", "IsRobot"))
            {
                reason = "helper installed/carried/system/person";
                return false;
            }

            if (!IsDirectWorldObject(item, out reason))
                return false;

            return IsEmptySessionHelper(item, slot, out reason);
        }

        internal static bool IsEmptySessionHelper(CondOwner item, BigHaulHelperSlot slot, out string reason)
        {
            reason = null;

            if (!IsHaulHelper(item))
            {
                reason = "not a crate/dolly container helper";
                return false;
            }

            if (slot == BigHaulHelperSlot.Hand && !HasAnyCond(item, "IsCrate"))
            {
                reason = "helper is not a hand crate";
                return false;
            }

            if (slot == BigHaulHelperSlot.Drag && !HasAnyCond(item, "IsDolly"))
            {
                reason = "helper is not a drag dolly";
                return false;
            }

            if (slot == BigHaulHelperSlot.Hand && !HasAnyPickupInteraction(item))
            {
                reason = "hand helper missing pickup interaction";
                return false;
            }

            if (slot == BigHaulHelperSlot.Drag && !CanDragWorldObject(item))
            {
                reason = "drag helper missing drag interaction";
                return false;
            }

            int cargoCount;
            if (!IsHaulHelperEmpty(item, out cargoCount, out reason))
                return false;

            reason = "empty " + slot.ToString().ToLowerInvariant() + " helper";
            return true;
        }

        internal static bool IsHaulHelperEmpty(CondOwner helper, out int cargoCount, out string reason)
        {
            cargoCount = 0;
            reason = null;

            try
            {
                if (helper == null || helper.objContainer == null)
                {
                    reason = "helper has no container";
                    return false;
                }

                var seen = new HashSet<string>();
                foreach (var candidate in helper.GetCOsSafe(true))
                {
                    var head = candidate != null && candidate.coStackHead != null ? candidate.coStackHead : candidate;
                    if (head == null || head == helper || head.bDestroyed)
                        continue;

                    if (!IsDirectCargoOfHelper(head, helper))
                        continue;

                    var id = SafeValue(() => head.strID);
                    if (!string.IsNullOrEmpty(id) && id != "<null>" && !seen.Add(id))
                        continue;

                    cargoCount++;
                }

                if (cargoCount > 0)
                {
                    reason = "helper is not empty cargo=" + cargoCount;
                    return false;
                }

                reason = "helper empty";
                return true;
            }
            catch (Exception ex)
            {
                reason = "empty helper scan failed: " + ex.Message;
                return false;
            }
        }

        internal static bool CanSessionHelperFitItem(CondOwner helper, CondOwner item, out string reason)
        {
            reason = "helper cannot fit item";

            try
            {
                if (helper == null || item == null)
                {
                    reason = "missing helper or item";
                    return false;
                }

                if (!IsHaulHelper(helper))
                {
                    reason = "not a haul helper";
                    return false;
                }

                if (helper.objContainer != null && helper.objContainer.CanFit(item, false, false))
                {
                    reason = "fits helper " + SafeName(helper);
                    return true;
                }
            }
            catch (Exception ex)
            {
                reason = "helper fit check failed: " + ex.Message;
                return false;
            }

            reason = "helper cannot fit item";
            return false;
        }

        internal static bool CanAcquireSessionHelper(CondOwner hauler, CondOwner helper, BigHaulHelperSlot slot, out string reason)
        {
            reason = null;

            if (hauler == null || helper == null)
            {
                reason = "missing hauler or helper";
                return false;
            }

            if (slot == BigHaulHelperSlot.Hand)
                return CanFitHandHelper(hauler, helper, out reason);

            if (slot == BigHaulHelperSlot.Drag)
            {
                if (GetDragSlotItemForHauler(hauler) == null && !HasAnyCond(hauler, "IsDragging"))
                {
                    reason = "drag slot free";
                    return true;
                }

                reason = "drag slot occupied";
                return false;
            }

            reason = "unknown helper slot";
            return false;
        }

        internal static CondOwner GetDragSlotItemForHauler(CondOwner hauler)
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

        private static bool CanFitHandHelper(CondOwner hauler, CondOwner helper, out string reason)
        {
            reason = "no free hand slot";

            try
            {
                if (hauler == null || helper == null || hauler.compSlots == null)
                {
                    reason = "missing hand slots";
                    return false;
                }

                foreach (var slotName in new[] { "heldL", "heldR", "handL", "handR" })
                {
                    var slot = hauler.compSlots.GetSlot(slotName);
                    if (slot != null && slot.CanFit(helper, false, true))
                    {
                        reason = "fits " + slotName;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "hand helper fit failed: " + ex.Message;
                return false;
            }

            return false;
        }

        internal static Interaction GetPickupInteractionForLooseItem(CondOwner item)
        {
            var interactionName = HasInteraction(item, "PickupItemStack")
                ? "PickupItemStack"
                : (HasInteraction(item, "PickupItem") ? "PickupItem" : null);

            if (string.IsNullOrEmpty(interactionName))
                return null;

            return DataHandler.GetInteraction(interactionName);
        }

        internal static Interaction GetAcquireInteractionForMode(CondOwner hauler, CondOwner item, BigHaulPaintMode mode, out string reason)
        {
            reason = null;

            if (mode == BigHaulPaintMode.Drag)
                return GetDragStartInteraction(hauler, item, out reason);

            var pickup = GetPickupInteractionForLooseItem(item);
            if (pickup == null)
                reason = "missing PickupItem/PickupItemStack";

            return pickup;
        }

        internal static Interaction GetDragStartInteraction(CondOwner hauler, CondOwner item, out string reason)
        {
            reason = "no drag start interaction";

            if (CanTriggerInteraction(hauler, item, "PickupDragStartNPCPledge"))
            {
                reason = "PickupDragStartNPCPledge";
                return DataHandler.GetInteraction("PickupDragStartNPCPledge");
            }

            if (CanTriggerInteraction(hauler, item, "PickupDragStart"))
            {
                reason = "PickupDragStart";
                return DataHandler.GetInteraction("PickupDragStart");
            }

            if (CanDragWorldObject(item))
            {
                reason = "drag slot fallback";
                return DataHandler.GetInteraction("PickupDragStart");
            }

            return null;
        }

        private static bool CanTriggerInteraction(CondOwner hauler, CondOwner item, string interactionName)
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

        internal static Interaction GetStopDragInteraction()
        {
            return DataHandler.GetInteraction("PickupDragStop");
        }

        internal static bool CanFitHaulHelper(CondOwner hauler, CondOwner item, out string reason)
        {
            reason = "no carried haul helper can fit item";

            if (hauler == null || item == null)
            {
                reason = "missing hauler or item for helper fit";
                return false;
            }

            var carriers = GetHaulHelpers(hauler).ToList();
            if (carriers.Count == 0)
            {
                reason = "no carried crate/dolly helper";
                return false;
            }

            foreach (var carrier in carriers)
            {
                try
                {
                    if (carrier != null && carrier.objContainer != null && carrier.objContainer.CanFit(item, false, false))
                    {
                        reason = "fits carried helper " + SafeName(carrier);
                        return true;
                    }
                }
                catch
                {
                    // Try the next carried helper.
                }
            }

            reason = "carried haul helpers cannot fit item";
            return false;
        }

        internal static IEnumerable<CondOwner> GetHaulHelpersForHauler(CondOwner hauler)
        {
            try
            {
                return GetHaulHelpers(hauler).ToList();
            }
            catch
            {
                return Enumerable.Empty<CondOwner>();
            }
        }

        internal static IEnumerable<CondOwner> GetDirectHaulHelperCargo(CondOwner helper)
        {
            var cargo = new List<CondOwner>();

            try
            {
                if (!IsHaulHelper(helper))
                    return cargo;

                var seen = new HashSet<string>();
                foreach (var candidate in helper.GetCOsSafe(true))
                {
                    var head = candidate != null && candidate.coStackHead != null ? candidate.coStackHead : candidate;
                    if (head == null || head.bDestroyed || head == helper)
                        continue;

                    if (!IsDirectCargoOfHelper(head, helper))
                        continue;

                    string reason;
                    if (!IsDrainableHaulHelperCargo(head, out reason))
                        continue;

                    var id = SafeValue(() => head.strID);
                    if (!string.IsNullOrEmpty(id) && id != "<null>" && !seen.Add(id))
                        continue;

                    cargo.Add(head);
                }
            }
            catch (Exception ex)
            {
                Warn("[BIGHelperCargo] failed: helper=" + SafeName(helper) + " error=" + ex.Message);
            }

            return cargo;
        }

        internal static bool IsInventoryDeltaHaulCargo(CondOwner item, out string reason)
        {
            if (!IsDrainableHaulHelperCargo(item, out reason))
                return false;

            if (HasAnyCond(item, "IsContainer", "IsCrate", "IsDolly"))
            {
                reason = "container/helper cargo blocked";
                return false;
            }

            return true;
        }

        private static IEnumerable<CondOwner> GetHaulHelpers(CondOwner hauler)
        {
            var seen = new HashSet<string>();
            foreach (var candidate in GetPotentialCarriedContainers(hauler))
            {
                if (!IsHaulHelper(candidate))
                    continue;

                var id = SafeValue(() => candidate.strID);
                if (!string.IsNullOrEmpty(id) && id != "<null>" && !seen.Add(id))
                    continue;

                yield return candidate;
            }
        }

        private static IEnumerable<CondOwner> GetPotentialCarriedContainers(CondOwner hauler)
        {
            var result = new List<CondOwner>();
            if (hauler == null)
                return result;

            AddExplicitSlotItem(result, hauler, "drag");
            AddExplicitSlotItem(result, hauler, "heldL");
            AddExplicitSlotItem(result, hauler, "heldR");
            AddExplicitSlotItem(result, hauler, "handL");
            AddExplicitSlotItem(result, hauler, "handR");

            return result;
        }

        private static void AddExplicitSlotItem(List<CondOwner> result, CondOwner hauler, string slotName)
        {
            try
            {
                if (result == null || hauler?.compSlots == null || string.IsNullOrEmpty(slotName))
                    return;

                var item = hauler.compSlots.GetSlot(slotName)?.GetOutermostCO();
                if (item == null)
                    return;

                var id = SafeValue(() => item.strID);
                if (!string.IsNullOrEmpty(id) && id != "<null>" && result.Any(existing => SafeValue(() => existing.strID) == id))
                    return;

                result.Add(item);
            }
            catch
            {
            }
        }

        internal static string GetActiveHaulHelperSlotName(CondOwner hauler, CondOwner helper)
        {
            try
            {
                if (hauler?.compSlots == null || helper == null)
                    return "<none>";

                var helperId = SafeValue(() => helper.strID);
                foreach (var slotName in new[] { "drag", "heldL", "heldR", "handL", "handR" })
                {
                    var item = hauler.compSlots.GetSlot(slotName)?.GetOutermostCO();
                    if (item == null)
                        continue;

                    if (!string.IsNullOrEmpty(helperId) && helperId != "<null>" && SafeValue(() => item.strID) == helperId)
                        return slotName;
                }
            }
            catch
            {
            }

            return "<none>";
        }

        private static bool IsHaulHelper(CondOwner item)
        {
            try
            {
                if (item == null || item.objContainer == null || item.bDestroyed)
                    return false;

                if (HasAnyCond(item, "IsInstalled", "IsSystem"))
                    return false;

                if (item.objContainer.gridLayout == null && !HasAnyCond(item, "IsInfiniteContainer"))
                    return false;

                return HasAnyCond(item, "IsDolly", "IsCrate");
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDirectCargoOfHelper(CondOwner item, CondOwner helper)
        {
            try
            {
                if (item == null || helper == null)
                    return false;

                var parent = item.objCOParent;
                return parent == helper || SafeValue(() => parent != null ? parent.strID : null) == SafeValue(() => helper.strID);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDrainableHaulHelperCargo(CondOwner item, out string reason)
        {
            reason = null;

            if (item == null)
            {
                reason = "null cargo";
                return false;
            }

            if (item.bDestroyed)
            {
                reason = "destroyed cargo";
                return false;
            }

            if (IsLiquidItem(item))
            {
                reason = "liquid cargo blocked";
                return false;
            }

            if (HasAnyCond(item, "IsInstalled", "IsSystem", "IsHuman", "IsRobot"))
            {
                reason = "installed/system/person cargo blocked";
                return false;
            }

            if (HasAnyCond(item, "IsCrate", "IsDolly"))
            {
                reason = "nested haul helper blocked";
                return false;
            }

            return true;
        }

        private static bool CanDragWorldObject(CondOwner item)
        {
            return HasInteraction(item, "PickupDragStart")
                || HasInteraction(item, "PickupDragStartNPCPledge")
                || HasDragSlot(item);
        }

        private static bool HasDragSlot(CondOwner item)
        {
            try
            {
                return item != null
                    && item.mapSlotEffects != null
                    && item.mapSlotEffects.ContainsKey("drag");
            }
            catch
            {
                return false;
            }
        }

        private static bool TriggerCond(CondOwner item, string triggerName)
        {
            try
            {
                var trigger = DataHandler.GetCondTrigger(triggerName);
                return trigger != null && trigger.Triggered(item, null, false);
            }
            catch
            {
                return false;
            }
        }

        private static string ModeLabel(BigHaulPaintMode mode)
        {
            switch (mode)
            {
                case BigHaulPaintMode.Haul:
                    return "haul";
                case BigHaulPaintMode.Drag:
                    return "drag";
                default:
                    return "unknown";
            }
        }

        private static bool HasAnyCond(CondOwner co, params string[] conds)
        {
            foreach (var cond in conds)
            {
                try
                {
                    if (co.HasCond(cond))
                        return true;
                }
                catch
                {
                    // Some transient objects can fail condition checks during load/unload.
                }
            }

            return false;
        }

        internal static bool IsDataReady()
        {
            try
            {
                return DataHandler.bInitialised
                    && DataHandler.dictInteractions != null
                    && DataHandler.dictInteractions.ContainsKey("PickupItemStack")
                    && DataHandler.dictInteractions.ContainsKey("PickupItem");
            }
            catch
            {
                return false;
            }
        }

        internal static string Describe(CondOwner co)
        {
            if (co == null)
                return "<null>";

            return SafeName(co)
                + " id=" + SafeValue(() => co.strID)
                + " def=" + SafeValue(() => co.strItemDef)
                + " ship=" + SafeValue(() => co.ship != null ? co.ship.strRegID : "<no ship>")
                + " tile=" + SafeTile(co);
        }

        internal static string SafeName(CondOwner co)
        {
            if (co == null)
                return "<null>";

            return SafeValue(() =>
            {
                if (!string.IsNullOrEmpty(co.strNameFriendly))
                    return co.strNameFriendly;
                if (!string.IsNullOrEmpty(co.strNameShort))
                    return co.strNameShort;
                return co.strName ?? "<unnamed>";
            });
        }

        internal static string SafeTile(CondOwner co)
        {
            try
            {
                if (co?.ship == null || co.tf == null)
                    return "<none>";

                var tile = co.ship.GetTileAtWorldCoords1(co.tf.position.x, co.tf.position.y, true);
                return tile != null ? tile.Index.ToString() : "<none>";
            }
            catch
            {
                return "<error>";
            }
        }

        private static string SafeValue(Func<string> getter)
        {
            try
            {
                return getter() ?? "<null>";
            }
            catch
            {
                return "<error>";
            }
        }

        internal static void ModInfo(string message)
        {
            Log?.LogInfo(message);
            BigLog.Write("INFO", message);
        }

        internal static void Warn(string message)
        {
            Log?.LogWarning(message);
            BigLog.Write("WARN", message);
        }

        internal static void Error(string message)
        {
            Log?.LogError(message);
            BigLog.Write("ERROR", message);
        }
    }

    internal static class BigHaulPlanner
    {
        private const int MaxSessionItems = 200;
        private const int MaxDragDropSearchAttempts = 40;
        private const int AnyStockpileZonePenalty = 250000;
        private const double DragDropSearchRetrySeconds = 1.5;
        private const float OffShipPickupPenalty = 1000000f;
        private static readonly Dictionary<string, BigLooseHaulSession> ActiveSessions = new Dictionary<string, BigLooseHaulSession>();

        internal static void CancelAllForHauler(CondOwner hauler, string reason, bool clearQueue)
        {
            if (hauler == null)
                return;

            CancelSession(hauler, reason, clearQueue);
        }

        internal static int StartDragSession(CondOwner hauler, IEnumerable<CondOwner> items)
        {
            return StartModeSession(hauler, BigHaulPaintMode.Drag, items);
        }

        private static string ModeMarker(BigHaulPaintMode mode, string suffix)
        {
            switch (mode)
            {
                case BigHaulPaintMode.Haul:
                    return "BIG" + suffix;
                case BigHaulPaintMode.Drag:
                    return "BIGDrag" + suffix;
                default:
                    return "BIGMode" + suffix;
            }
        }

        internal static int StartLooseSession(CondOwner hauler, IEnumerable<CondOwner> items)
        {
            return StartModeSession(hauler, BigHaulPaintMode.Haul, items);
        }

        private static int StartModeSession(CondOwner hauler, BigHaulPaintMode mode, IEnumerable<CondOwner> items)
        {
            if (hauler == null)
            {
                Plugin.Warn("[" + ModeMarker(mode, "PlanStart") + "] skipped: hauler was null.");
                return 0;
            }

            var haulerId = SafeId(hauler);
            if (string.IsNullOrEmpty(haulerId))
            {
                Plugin.Warn("[" + ModeMarker(mode, "PlanStart") + "] skipped: hauler had no id.");
                return 0;
            }

            var rawSelection = DistinctSelection(items);
            var selected = new List<CondOwner>();
            var unplanned = new List<CondOwner>();
            var seen = new HashSet<string>();

            foreach (var item in rawSelection)
            {
                if (!IsPlanPendingItem(hauler, item, mode, rawSelection))
                {
                    BigHaulHelperSlot helperSlot;
                    string helperReason;
                    if (mode != BigHaulPaintMode.Haul || !Plugin.IsSessionHelperCandidate(item, out helperSlot, out helperReason))
                        unplanned.Add(item);

                    continue;
                }

                var id = SafeId(item);
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    continue;

                selected.Add(item);
            }

            var scored = new List<Tuple<CondOwner, float>>();
            var skippedNoPath = 0;
            foreach (var item in selected)
            {
                var pickup = Plugin.GetAcquireInteractionForMode(hauler, item, mode, out var pickupReason);
                if (pickup == null)
                {
                    skippedNoPath++;
                    BigHaulRegistry.MarkState(item, "SkippedNoPath", "acquire interaction missing before planning: " + pickupReason);
                    continue;
                }

                pickup.bManual = true;

                var score = PickupSortScore(hauler, item, pickup);
                if (score >= float.MaxValue * 0.5f)
                {
                    skippedNoPath++;
                    BigHaulRegistry.MarkState(item, "SkippedNoPath", "no reachable path before planning");
                    continue;
                }

                scored.Add(new Tuple<CondOwner, float>(item, score));
            }

            var plannedItems = scored
                .OrderBy(item => item.Item2)
                .Select(item => item.Item1)
                .ToList();

            selected = plannedItems
                .Take(MaxSessionItems)
                .ToList();

            var backlog = plannedItems
                .Skip(MaxSessionItems)
                .ToList();

            if (skippedNoPath > 0)
                Plugin.Warn("[" + ModeMarker(mode, "PlanFilter") + "] hauler=" + Plugin.SafeName(hauler) + " skippedNoPath=" + skippedNoPath + " planned=" + selected.Count + " backlog=" + backlog.Count + ".");

            foreach (var item in unplanned)
                BigHaulRegistry.MarkState(item, "SkippedNoRoom", "no suitable inventory/helper capacity before planning");

            if (selected.Count == 0)
            {
                foreach (var helper in rawSelection)
                {
                    BigHaulHelperSlot helperSlot;
                    string helperReason;
                    if (mode == BigHaulPaintMode.Haul && Plugin.IsSessionHelperCandidate(helper, out helperSlot, out helperReason))
                        BigHaulRegistry.MarkState(helper, "Cancelled", "no BIG haul cargo planned for helper");
                }

                Plugin.Warn("[" + ModeMarker(mode, "PlanStart") + "] skipped: no valid items survived filtering.");
                return 0;
            }

            BigLooseHaulSession existing;
            if (ActiveSessions.TryGetValue(haulerId, out existing) && existing != null)
                return MergeLooseSession(hauler, existing, selected.Concat(backlog).ToList(), mode, rawSelection);

            SafeClearQueue(hauler, "start " + mode + " BIG session");

            var session = new BigLooseHaulSession
            {
                HaulerId = haulerId,
                HaulerName = Plugin.SafeName(hauler),
                Mode = mode,
                Pending = selected,
                Backlog = backlog,
                StartedAt = DateTime.Now
            };

            TrackSessionCargoKeys(session, session.Pending);
            TrackSessionCargoKeys(session, session.Backlog);
            CaptureInventoryBaseline(hauler, session, "start");
            CapturePreExistingHelpers(hauler, session, "start");
            PlanSessionHelpers(hauler, session, rawSelection, selected.Concat(backlog).ToList(), "start");

            ActiveSessions[haulerId] = session;
            Plugin.ModInfo("[" + ModeMarker(mode, "PlanStart") + "] hauler=" + session.HaulerName + " pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + " queue=" + Plugin.QueueSummary(hauler) + ".");
            var planned = session.Pending.Count + session.Backlog.Count;

            if (!IsAutoTaskingEnabled(hauler))
            {
                session.AutoPausedLogged = true;
                Plugin.Warn("[" + ModeMarker(mode, "PlanPaused") + "] hauler=" + session.HaulerName + " reason=auto-tasking-disabled pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + ".");
                return planned;
            }

            TryPump(hauler);
            return planned;
        }

        private static int MergeLooseSession(CondOwner hauler, BigLooseHaulSession session, List<CondOwner> plannedItems, BigHaulPaintMode mode, List<CondOwner> rawSelection)
        {
            if (hauler == null || session == null || plannedItems == null || plannedItems.Count == 0)
                return 0;

            PruneSession(hauler, session);
            CapturePreExistingHelpers(hauler, session, "merge");

            var addedPending = 0;
            var addedBacklog = 0;
            var duplicates = 0;

            foreach (var item in plannedItems)
            {
                if (!IsPlanPendingItem(hauler, item, mode, rawSelection))
                    continue;

                if (SessionContainsItem(session, item))
                {
                    duplicates++;
                    continue;
                }

                if (!session.DropPhase && session.Pending.Count < MaxSessionItems)
                {
                    session.Pending.Add(item);
                    TrackSessionCargoKey(session, item);
                    addedPending++;
                }
                else
                {
                    session.Backlog.Add(item);
                    TrackSessionCargoKey(session, item);
                    addedBacklog++;
                }
            }

            var added = addedPending + addedBacklog;
            Plugin.ModInfo("[" + ModeMarker(mode, "PlanMerge") + "] hauler=" + session.HaulerName
                + " addedPending=" + addedPending
                + " addedBacklog=" + addedBacklog
                + " duplicates=" + duplicates
                + " pending=" + session.Pending.Count
                + " backlog=" + session.Backlog.Count
                + " carried=" + session.Carried.Count
                + " queue=" + Plugin.QueueSummary(hauler) + ".");

            if (added <= 0)
                return 0;

            PlanSessionHelpers(hauler, session, rawSelection, plannedItems, "merge");

            if (!IsAutoTaskingEnabled(hauler))
            {
                if (!session.AutoPausedLogged)
                {
                    session.AutoPausedLogged = true;
                    Plugin.Warn("[" + ModeMarker(mode, "PlanPaused") + "] hauler=" + session.HaulerName + " reason=auto-tasking-disabled pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + ".");
                }

                return added;
            }

            TryPump(hauler);
            return added;
        }

        private static bool SessionContainsItem(BigLooseHaulSession session, CondOwner item)
        {
            if (session == null || item == null)
                return false;

            var id = SafeId(item);
            if (string.IsNullOrEmpty(id))
                return false;

            if (ContainsSame(session.Pending, item) || ContainsSame(session.Backlog, item) || ContainsSame(session.Carried, item))
                return true;

            if (session.CurrentPickup != null && SafeId(session.CurrentPickup) == id)
                return true;

            return session.CurrentDrop != null && session.CurrentDrop.Item != null && SafeId(session.CurrentDrop.Item) == id;
        }

        private static List<CondOwner> DistinctSelection(IEnumerable<CondOwner> items)
        {
            var selected = new List<CondOwner>();
            var seen = new HashSet<string>();

            foreach (var item in items ?? Enumerable.Empty<CondOwner>())
            {
                if (item == null)
                    continue;

                var id = SafeId(item);
                if (!string.IsNullOrEmpty(id) && !seen.Add(id))
                    continue;

                selected.Add(item);
            }

            return selected;
        }

        private static bool IsPlanPendingItem(CondOwner hauler, CondOwner item, BigHaulPaintMode mode, List<CondOwner> rawSelection)
        {
            if (item == null)
                return false;

            BigHaulHelperSlot helperSlot;
            string helperReason;
            if (mode == BigHaulPaintMode.Haul && Plugin.IsSessionHelperCandidate(item, out helperSlot, out helperReason))
                return false;

            if (mode != BigHaulPaintMode.Haul)
                return IsValidPendingItem(hauler, item, mode);

            string looseReason;
            if (Plugin.IsLooseWorldHaulCandidate(item, out looseReason))
                return true;

            string fitReason;
            string waitingReason;
            if (Plugin.IsHaulCandidateIgnoringCurrentCapacity(item, out waitingReason)
                && (CanAcquireNext(hauler, item, mode, out fitReason) || HasPotentialHaulHelperForItem(hauler, item, rawSelection, out fitReason)))
            {
                return true;
            }

            return false;
        }

        private static bool HasPotentialHaulHelperForItem(CondOwner hauler, CondOwner item, List<CondOwner> rawSelection, out string reason)
        {
            reason = "no potential helper";

            string fitReason;
            if (Plugin.CanFitHaulHelper(hauler, item, out fitReason))
            {
                reason = fitReason;
                return true;
            }

            foreach (var helper in rawSelection ?? new List<CondOwner>())
            {
                BigHaulHelperSlot slot;
                string helperReason;
                if (!Plugin.IsSessionHelperCandidate(helper, out slot, out helperReason))
                    continue;

                if (Plugin.CanSessionHelperFitItem(helper, item, out fitReason))
                {
                    reason = "selected " + slot.ToString().ToLowerInvariant() + " helper can fit item";
                    return true;
                }
            }

            foreach (var helper in FindSameShipSessionHelpers(hauler, null))
            {
                if (Plugin.CanSessionHelperFitItem(helper.Item, item, out fitReason))
                {
                    reason = "same-ship " + helper.Slot.ToString().ToLowerInvariant() + " helper can fit item";
                    return true;
                }
            }

            return false;
        }

        private static void CapturePreExistingHelpers(CondOwner hauler, BigLooseHaulSession session, string reason)
        {
            try
            {
                if (hauler == null || session == null || session.Mode != BigHaulPaintMode.Haul || session.PreExistingHelpersCaptured)
                    return;

                var added = 0;
                foreach (var helper in Plugin.GetHaulHelpersForHauler(hauler))
                {
                    var id = SafeId(helper);
                    if (string.IsNullOrEmpty(id))
                        continue;

                    if (Plugin.GetSessionHelperSlot(helper) == BigHaulHelperSlot.None)
                        continue;

                    if (session.PreExistingHelperIds.Add(id))
                        added++;
                }

                session.PreExistingHelpersCaptured = true;
                if (added > 0)
                    Plugin.ModInfo("[BIGHelperBaseline] hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " preExisting=" + added + ".");
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGHelperBaseline] failed: hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " error=" + ex.Message);
            }
        }

        private static void PlanSessionHelpers(CondOwner hauler, BigLooseHaulSession session, List<CondOwner> rawSelection, List<CondOwner> plannedCargo, string reason)
        {
            try
            {
                if (hauler == null || session == null || session.Mode != BigHaulPaintMode.Haul)
                    return;

                var excludeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cargo in plannedCargo ?? new List<CondOwner>())
                {
                    var id = SafeId(cargo);
                    if (!string.IsNullOrEmpty(id))
                        excludeIds.Add(id);
                }

                var selectedAdded = 0;
                foreach (var helper in rawSelection ?? new List<CondOwner>())
                {
                    var id = SafeId(helper);
                    if (string.IsNullOrEmpty(id) || excludeIds.Contains(id))
                        continue;

                    BigHaulHelperSlot slot;
                    string helperReason;
                    if (!Plugin.IsSessionHelperCandidate(helper, out slot, out helperReason))
                        continue;

                    string fitReason;
                    if (!HelperCanFitAnyPlannedCargo(helper, plannedCargo, out fitReason))
                        continue;

                    if (TryAddHelperCandidate(hauler, session, helper, slot, "selected-" + reason + ":" + fitReason))
                        selectedAdded++;
                }

                var shipAdded = 0;
                foreach (var helper in FindSameShipSessionHelpers(hauler, excludeIds))
                {
                    string fitReason;
                    if (!HelperCanFitAnyPlannedCargo(helper.Item, plannedCargo, out fitReason))
                        continue;

                    if (TryAddHelperCandidate(hauler, session, helper.Item, helper.Slot, "same-ship-" + reason + ":" + fitReason))
                        shipAdded++;
                }

                if (selectedAdded > 0 || shipAdded > 0)
                {
                    Plugin.ModInfo("[BIGHelperPlan] hauler=" + Plugin.SafeName(hauler)
                        + " reason=" + reason
                        + " selected=" + selectedAdded
                        + " sameShip=" + shipAdded
                        + " handCandidates=" + session.HandHelperCandidates.Count
                        + " dragCandidates=" + session.DragHelperCandidates.Count + ".");
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGHelperPlan] failed: hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " error=" + ex.Message);
            }
        }

        private static bool HelperCanFitAnyPlannedCargo(CondOwner helper, List<CondOwner> plannedCargo, out string reason)
        {
            reason = "no planned cargo fits helper";

            foreach (var cargo in plannedCargo ?? new List<CondOwner>())
            {
                if (cargo == null || cargo.bDestroyed)
                    continue;

                if (Plugin.GetSessionHelperSlot(cargo) != BigHaulHelperSlot.None)
                    continue;

                string fitReason;
                if (Plugin.CanSessionHelperFitItem(helper, cargo, out fitReason))
                {
                    reason = "fits " + Plugin.SafeName(cargo);
                    return true;
                }
            }

            return false;
        }

        private static bool TryAddHelperCandidate(CondOwner hauler, BigLooseHaulSession session, CondOwner helper, BigHaulHelperSlot slot, string source)
        {
            if (hauler == null || session == null || helper == null || slot == BigHaulHelperSlot.None)
                return false;

            var id = SafeId(helper);
            if (string.IsNullOrEmpty(id))
                return false;

            if (session.PreExistingHelperIds.Contains(id) || session.SessionOwnedHelperIds.Contains(id) || session.HelperAcquireFailedIds.Contains(id))
                return false;

            var list = slot == BigHaulHelperSlot.Drag ? session.DragHelperCandidates : session.HandHelperCandidates;
            if (ContainsSame(list, helper))
                return false;

            list.Add(helper);
            BigHaulRegistry.Register(hauler, helper, BigHaulPaintMode.Haul);
            BigHaulRegistry.MarkState(helper, "HelperQueued", "BIG helper queued from " + source);
            return true;
        }

        private static List<SessionHelperCandidate> FindSameShipSessionHelpers(CondOwner hauler, HashSet<string> excludeIds)
        {
            var helpers = new List<SessionHelperCandidate>();

            try
            {
                if (hauler == null || DataHandler.mapCOs == null)
                    return helpers;

                foreach (var helper in DataHandler.mapCOs.Values.ToList())
                {
                    if (helper == null || helper == hauler)
                        continue;

                    var id = SafeId(helper);
                    if (!string.IsNullOrEmpty(id) && excludeIds != null && excludeIds.Contains(id))
                        continue;

                    if (!IsSameShip(hauler, helper))
                        continue;

                    BigHaulHelperSlot slot;
                    string helperReason;
                    if (!Plugin.IsSessionHelperCandidate(helper, out slot, out helperReason))
                        continue;

                    var score = HelperPickupScore(hauler, helper, slot);
                    if (score >= float.MaxValue * 0.5f)
                        continue;

                    helpers.Add(new SessionHelperCandidate
                    {
                        Item = helper,
                        Slot = slot,
                        Score = score
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGHelperScan] failed: hauler=" + Plugin.SafeName(hauler) + " error=" + ex.Message);
            }

            return helpers
                .GroupBy(candidate => SafeId(candidate.Item))
                .Select(group => group.OrderBy(candidate => candidate.Score).First())
                .OrderBy(candidate => candidate.Score)
                .ToList();
        }

        private static float HelperPickupScore(CondOwner hauler, CondOwner helper, BigHaulHelperSlot slot)
        {
            string reason;
            var pickup = slot == BigHaulHelperSlot.Drag
                ? Plugin.GetDragStartInteraction(hauler, helper, out reason)
                : Plugin.GetPickupInteractionForLooseItem(helper);

            if (pickup == null)
                return float.MaxValue;

            pickup.bManual = true;
            return PickupSortScore(hauler, helper, pickup);
        }

        internal static void PumpSelectedCrew()
        {
            try
            {
                TryPump(CrewSim.GetSelectedCrew());
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGPumpSelected] failed: " + ex.Message);
            }
        }

        internal static void TryPump(CondOwner hauler)
        {
            if (hauler == null)
                return;

            BigLooseHaulSession session;
            if (!ActiveSessions.TryGetValue(SafeId(hauler), out session))
                return;

                if (!IsAutoTaskingEnabled(hauler))
                {
                    if (!session.AutoPausedLogged)
                    {
                        session.AutoPausedLogged = true;
                        Plugin.Warn("[" + ModeMarker(session.Mode, "PlanPaused") + "] hauler=" + Plugin.SafeName(hauler) + " reason=auto-tasking-disabled pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + " carried=" + session.Carried.Count + ".");
                    }

                    return;
            }

            session.AutoPausedLogged = false;

            if (hauler.aQueue != null && hauler.aQueue.Count > 0)
                return;

            for (var guard = 0; guard < 16; guard++)
            {
                if (session.CurrentHelperAcquire != null)
                {
                    FinishHelperAcquire(hauler, session);
                    continue;
                }

                if (session.CurrentPickup != null)
                {
                    FinishPickup(hauler, session);
                    continue;
                }

                if (session.CurrentDrop != null)
                {
                    FinishDrop(hauler, session);
                    continue;
                }

                PruneSession(hauler, session);
                SyncCarriedFromHauler(hauler, session, "pump");
                SyncExistingDragSlotForDragSession(hauler, session, "pump");

                if (session.Pending.Count == 0)
                {
                    if (LoadNextBatch(hauler, session))
                        continue;

                    if (session.Carried.Count > 0)
                    {
                        session.DropPhase = true;
                        QueueNextDrop(hauler, session);
                        return;
                    }

                    if (TryQueueFinalHelperRelease(hauler, session))
                        return;

                    CompleteSession(hauler, session, "all items handled");
                    return;
                }

                if (session.Carried.Count == 0 && TryQueueNextHelperAcquire(hauler, session))
                    return;

                if (session.DropPhase && session.Carried.Count > 0)
                {
                    QueueNextDrop(hauler, session);
                    return;
                }

                if (session.Carried.Count > 0 && !HasAnyFittablePending(hauler, session))
                {
                    session.DropPhase = true;
                    QueueNextDrop(hauler, session);
                    return;
                }

                var next = FindNearestFittablePending(hauler, session);
                if (next != null)
                {
                    QueuePickup(hauler, session, next);
                    return;
                }

                if (session.Pending.Count == 0)
                {
                    if (LoadNextBatch(hauler, session))
                        continue;

                    if (session.Carried.Count > 0)
                    {
                        session.DropPhase = true;
                        QueueNextDrop(hauler, session);
                        return;
                    }

                    if (TryQueueFinalHelperRelease(hauler, session))
                        return;

                    CompleteSession(hauler, session, "no reachable pending items");
                    return;
                }

                if (session.Carried.Count > 0)
                {
                    session.DropPhase = true;
                    QueueNextDrop(hauler, session);
                    return;
                }

                var skipped = session.Pending[0];
                session.Pending.RemoveAt(0);
                BigHaulRegistry.MarkState(skipped, "SkippedNoRoom", "no inventory slot before pickup");
                Plugin.Warn("[BIGSkipNoRoom] hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(skipped) + " pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + ".");
            }

            Plugin.Warn("[BIGPumpGuard] stopped after guard limit. hauler=" + Plugin.SafeName(hauler) + " pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + " carried=" + session.Carried.Count + ".");
        }

        private static void SyncExistingDragSlotForDragSession(CondOwner hauler, BigLooseHaulSession session, string reason)
        {
            if (session == null || session.Mode != BigHaulPaintMode.Drag)
                return;

            var dragged = GetDragSlotItem(hauler);
            if (dragged == null || dragged.bDestroyed)
                return;

            var head = StackHead(dragged);
            if (head == null || head.bDestroyed || ContainsSame(session.Carried, head))
                return;

            if (Plugin.GetSessionHelperSlot(head) != BigHaulHelperSlot.None)
                return;

            if (IsDropFailed(session, head))
                return;

            session.Carried.Add(head);
            Plugin.ModInfo("[BIGDragCarrySync] hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " existingDrag=" + Plugin.Describe(head) + " carried=" + session.Carried.Count + ".");
        }

        private static bool TryQueueNextHelperAcquire(CondOwner hauler, BigLooseHaulSession session)
        {
            if (hauler == null || session == null || session.Mode != BigHaulPaintMode.Haul)
                return false;

            if (session.Pending.Count == 0 && session.Backlog.Count == 0)
                return false;

            PruneHelperCandidates(session);

            if (!HasActiveHelperForSlot(hauler, BigHaulHelperSlot.Hand)
                && TryQueueHelperCandidateFromList(hauler, session, session.HandHelperCandidates, BigHaulHelperSlot.Hand))
            {
                return true;
            }

            if (!HasActiveHelperForSlot(hauler, BigHaulHelperSlot.Drag)
                && TryQueueHelperCandidateFromList(hauler, session, session.DragHelperCandidates, BigHaulHelperSlot.Drag))
            {
                return true;
            }

            return false;
        }

        private static bool TryQueueHelperCandidateFromList(CondOwner hauler, BigLooseHaulSession session, List<CondOwner> helpers, BigHaulHelperSlot slot)
        {
            if (helpers == null || helpers.Count == 0)
                return false;

            while (helpers.Count > 0)
            {
                var helper = helpers[0];
                helpers.RemoveAt(0);

                var id = SafeId(helper);
                if (string.IsNullOrEmpty(id) || session.HelperAcquireFailedIds.Contains(id) || session.SessionOwnedHelperIds.Contains(id))
                    continue;

                string candidateReason;
                BigHaulHelperSlot currentSlot;
                if (!Plugin.IsSessionHelperCandidate(helper, out currentSlot, out candidateReason) || currentSlot != slot)
                {
                    MarkHelperAcquireFailed(session, helper, "helper candidate invalid: " + candidateReason, "SkippedNoRoom");
                    continue;
                }

                string fitReason;
                if (!Plugin.CanAcquireSessionHelper(hauler, helper, slot, out fitReason))
                {
                    MarkHelperAcquireFailed(session, helper, "cannot equip helper: " + fitReason, "SkippedNoRoom");
                    Plugin.Warn("[BIGHelperAcquireSkip] hauler=" + Plugin.SafeName(hauler) + " helper=" + Plugin.Describe(helper) + " slot=" + slot + " reason=" + fitReason + ".");
                    continue;
                }

                string pickupReason = null;
                var pickup = slot == BigHaulHelperSlot.Drag
                    ? Plugin.GetDragStartInteraction(hauler, helper, out pickupReason)
                    : Plugin.GetPickupInteractionForLooseItem(helper);

                if (pickup == null)
                {
                    MarkHelperAcquireFailed(session, helper, "helper acquire interaction missing: " + pickupReason, "SkippedNoPath");
                    Plugin.Warn("[BIGHelperAcquireSkip] hauler=" + Plugin.SafeName(hauler) + " helper=" + Plugin.Describe(helper) + " slot=" + slot + " reason=no-interaction.");
                    continue;
                }

                pickup.bManual = true;
                if (!hauler.QueueInteraction(helper, pickup))
                {
                    MarkHelperAcquireFailed(session, helper, "helper acquire queue refused", "SkippedNoPath");
                    Plugin.Warn("[BIGHelperQueue] failed: hauler=" + Plugin.SafeName(hauler) + " helper=" + Plugin.Describe(helper) + " slot=" + slot + " queue=" + Plugin.QueueSummary(hauler) + ".");
                    continue;
                }

                session.CurrentHelperAcquire = helper;
                session.CurrentHelperSlot = slot;
                Plugin.ModInfo("[BIGHelperQueue] hauler=" + Plugin.SafeName(hauler)
                    + " helper=" + Plugin.Describe(helper)
                    + " slot=" + slot
                    + " fit=" + fitReason
                    + " pending=" + session.Pending.Count
                    + " backlog=" + session.Backlog.Count
                    + " queue=" + Plugin.QueueSummary(hauler) + ".");
                return true;
            }

            return false;
        }

        private static void FinishHelperAcquire(CondOwner hauler, BigLooseHaulSession session)
        {
            var helper = session.CurrentHelperAcquire;
            var slot = session.CurrentHelperSlot;
            session.CurrentHelperAcquire = null;
            session.CurrentHelperSlot = BigHaulHelperSlot.None;

            if (helper == null || helper.bDestroyed)
            {
                Plugin.Warn("[BIGHelperAcquired] helper vanished. hauler=" + Plugin.SafeName(hauler) + ".");
                return;
            }

            var owned = ResolveOwnedCarried(hauler, helper);
            if (slot == BigHaulHelperSlot.Drag)
            {
                var dragged = GetDragSlotItem(hauler);
                if (SameItem(dragged, helper))
                    owned = dragged;
            }

            if (owned != null && (IsOwnedByHauler(hauler, owned) || (slot == BigHaulHelperSlot.Drag && SameItem(GetDragSlotItem(hauler), owned))))
            {
                var id = SafeId(owned);
                if (!string.IsNullOrEmpty(id))
                    session.SessionOwnedHelperIds.Add(id);

                BigHaulRegistry.MarkState(owned, "HelperAcquired", "BIG helper acquired for smart haul");
                Plugin.ModInfo("[BIGHelperAcquired] hauler=" + Plugin.SafeName(hauler)
                    + " helper=" + Plugin.Describe(owned)
                    + " slot=" + slot
                    + " ownedHelpers=" + session.SessionOwnedHelperIds.Count + ".");
                return;
            }

            MarkHelperAcquireFailed(session, helper, "helper not owned after acquire", "SkippedNoPath");
            Plugin.Warn("[BIGHelperAcquired] helper was not found on hauler. hauler=" + Plugin.SafeName(hauler) + " helper=" + Plugin.Describe(helper) + " slot=" + slot + ".");
        }

        private static bool HasActiveHelperForSlot(CondOwner hauler, BigHaulHelperSlot slot)
        {
            try
            {
                foreach (var helper in Plugin.GetHaulHelpersForHauler(hauler))
                {
                    if (Plugin.GetSessionHelperSlot(helper) == slot)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void PruneHelperCandidates(BigLooseHaulSession session)
        {
            if (session == null)
                return;

            session.HandHelperCandidates = PruneHelperCandidateList(session, session.HandHelperCandidates, BigHaulHelperSlot.Hand);
            session.DragHelperCandidates = PruneHelperCandidateList(session, session.DragHelperCandidates, BigHaulHelperSlot.Drag);
        }

        private static List<CondOwner> PruneHelperCandidateList(BigLooseHaulSession session, List<CondOwner> helpers, BigHaulHelperSlot slot)
        {
            var result = new List<CondOwner>();
            var seen = new HashSet<string>();

            foreach (var helper in helpers ?? new List<CondOwner>())
            {
                var id = SafeId(helper);
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    continue;

                if (session.HelperAcquireFailedIds.Contains(id) || session.SessionOwnedHelperIds.Contains(id) || session.PreExistingHelperIds.Contains(id))
                    continue;

                BigHaulHelperSlot currentSlot;
                string reason;
                if (!Plugin.IsSessionHelperCandidate(helper, out currentSlot, out reason) || currentSlot != slot)
                    continue;

                result.Add(helper);
            }

            return result;
        }

        private static void MarkHelperAcquireFailed(BigLooseHaulSession session, CondOwner helper, string reason, string state)
        {
            var id = SafeId(helper);
            if (!string.IsNullOrEmpty(id))
                session.HelperAcquireFailedIds.Add(id);

            if (helper != null)
                BigHaulRegistry.MarkState(helper, state, reason);
        }

        private static void QueuePickup(CondOwner hauler, BigLooseHaulSession session, CondOwner item)
        {
            try
            {
                var pickup = Plugin.GetAcquireInteractionForMode(hauler, item, session.Mode, out var pickupReason);
                if (pickup == null)
                {
                    Plugin.Warn("[" + ModeMarker(session.Mode, "QueuePickup") + "] failed: no acquire interaction for item=" + Plugin.Describe(item) + " reason=" + pickupReason + ".");
                    return;
                }

                pickup.bManual = true;
                session.Pending.Remove(item);
                session.CurrentPickup = item;

                var queued = hauler.QueueInteraction(item, pickup);
                if (!queued)
                {
                    session.CurrentPickup = null;
                    CountFailedAttempt(session, item);
                    BigHaulRegistry.MarkState(item, "SkippedNoPath", "pickup queue refused by vanilla pathing");
                    Plugin.Warn("[" + ModeMarker(session.Mode, "QueuePickup") + "] failed: hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " queue=" + Plugin.QueueSummary(hauler) + ".");
                    return;
                }

                string fitReason;
                CanAcquireNext(hauler, item, session.Mode, out fitReason);
                Plugin.ModInfo("[" + ModeMarker(session.Mode, "QueuePickup") + "] hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " interaction=" + pickup.strName + " fit=" + fitReason + " pending=" + session.Pending.Count + " carried=" + session.Carried.Count + " queue=" + Plugin.QueueSummary(hauler) + ".");
            }
            catch (Exception ex)
            {
                session.CurrentPickup = null;
                if (item != null)
                    BigHaulRegistry.MarkState(item, "SkippedNoPath", "pickup queue crashed");

                Plugin.Error("[" + ModeMarker(session.Mode, "QueuePickup") + "] crashed: item=" + Plugin.Describe(item) + " error=" + ex);
            }
        }

        private static bool LoadNextBatch(CondOwner hauler, BigLooseHaulSession session)
        {
            if (session == null || session.Pending.Count > 0 || session.Backlog.Count == 0)
                return false;

            session.Backlog = session.Backlog
                .Where(item => IsValidPendingItem(hauler, item, session.Mode))
                .GroupBy(SafeId)
                .Select(group => group.First())
                .ToList();

            if (session.Backlog.Count == 0)
                return false;

            var count = Math.Min(MaxSessionItems, session.Backlog.Count);
            session.Pending = session.Backlog.Take(count).ToList();
            session.Backlog.RemoveRange(0, count);
            session.DropPhase = false;
            session.DropAnchorShip = null;
            session.DropAnchorZone = null;
            session.DropAnchorPosition = Vector3.zero;
            Plugin.ModInfo("[BIGBatchStart] hauler=" + session.HaulerName + " pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + ".");
            return true;
        }

        private static void FinishPickup(CondOwner hauler, BigLooseHaulSession session)
        {
            var item = session.CurrentPickup;
            session.CurrentPickup = null;

            if (item == null || item.bDestroyed)
            {
                Plugin.Warn("[BIGPicked] pickup target vanished. hauler=" + Plugin.SafeName(hauler) + ".");
                return;
            }

            if (IsOwnedByHauler(hauler, item))
            {
                TrackSessionCargoKey(session, item);
                SyncCarriedFromHauler(hauler, session, "after-pickup");
                var carried = StackHead(item);
                if (!ContainsSame(session.Carried, carried) && CanAddDirectCarried(session, carried, "after-pickup"))
                    session.Carried.Add(carried);

                BigHaulRegistry.MarkState(item, "PickedUp", "BIG pickup complete");
                Plugin.ModInfo("[BIGPicked] hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " carried=" + session.Carried.Count + " pending=" + session.Pending.Count + ".");
                return;
            }

            CountFailedAttempt(session, item);
            var attempts = GetFailedAttempts(session, item);
            SyncCarriedFromHauler(hauler, session, "pickup-missed");

            if (IsRetainablePendingItem(hauler, item, session))
            {
                RequeuePending(session, item);

                if (session.Mode == BigHaulPaintMode.Haul && session.Carried.Count > 0)
                {
                    session.DropPhase = true;
                    string fitReason;
                    CanAcquireNext(hauler, item, session.Mode, out fitReason);
                    Plugin.Warn("[BIGPickupDeferred] hauler=" + Plugin.SafeName(hauler)
                        + " reason=not-owned-after-pickup"
                        + " attempts=" + attempts
                        + " item=" + Plugin.Describe(item)
                        + " fit=" + fitReason
                        + " pending=" + session.Pending.Count
                        + " carried=" + session.Carried.Count
                        + ". Forcing unload before retry.");
                    return;
                }
            }

            if (attempts >= 2)
            {
                BigHaulRegistry.MarkState(item, "PickupFailed", "not in inventory after pickup");
                Plugin.Warn("[BIGPicked] failed twice, skipping item. hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + ".");
                return;
            }

            if (IsRetainablePendingItem(hauler, item, session))
                RequeuePending(session, item);

            Plugin.Warn("[BIGPicked] item was not found in BIG hauler inventory; requeued once. hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " pending=" + session.Pending.Count + ".");
        }

        private static bool QueueNextDrop(CondOwner hauler, BigLooseHaulSession session)
        {
            session.DropPhase = true;
            SyncCarriedFromHauler(hauler, session, "before-drop");

            if (session.Carried.Count == 0)
                return false;

            var item = session.Carried[0];
            if (session.Mode == BigHaulPaintMode.Drag && IsDropSearchCoolingDown(session, item))
                return false;

            DropPlan plan;
            if (!TryFindDropPlan(hauler, item, out plan)
                && !(session.Mode == BigHaulPaintMode.Drag && TryFindDropPlan(hauler, item, out plan, true)))
            {
                if (TryDeferDragDropSearch(hauler, session, item, "no valid stockpile tile for item footprint"))
                    return false;

                MarkDropFailed(hauler, session, item, "no valid stockpile tile for item footprint");
                return false;
            }

            ClearDropSearchState(session, item);
            plan.Item = item;
            session.CurrentDrop = plan;

            if (!QueueWalkToDrop(hauler, plan))
            {
                session.CurrentDrop = null;
                Plugin.Warn("[BIGQueueDropWalk] failed: hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " target=" + plan.Summary + ".");
                return false;
            }

            if (session.Mode == BigHaulPaintMode.Drag && !QueueStopDrag(hauler))
            {
                session.CurrentDrop = null;
                Plugin.Warn("[BIGDragQueueStop] failed: hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " target=" + plan.Summary + ".");
                return false;
            }

            Plugin.ModInfo("[" + ModeMarker(session.Mode, "QueueDropWalk") + "] hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " target=" + plan.Summary + " pending=" + session.Pending.Count + " carried=" + session.Carried.Count + " queue=" + Plugin.QueueSummary(hauler) + ".");
            return true;
        }

        private static void MarkDropFailed(CondOwner hauler, BigLooseHaulSession session, CondOwner item, string reason)
        {
            if (session == null || item == null)
                return;

            var failed = StackHead(item) ?? item;
            var id = SafeId(failed);
            if (!string.IsNullOrEmpty(id))
                session.FailedDropIds.Add(id);

            RemoveSame(session.Carried, failed);
            RemoveSame(session.Carried, item);
            RemoveSame(session.Pending, failed);
            RemoveSame(session.Pending, item);
            RemoveSame(session.Backlog, failed);
            RemoveSame(session.Backlog, item);

            BigHaulRegistry.MarkState(failed, "SkippedNoPath", "drop target unavailable: " + reason);
            Plugin.Warn("[BIGDropNoTarget] hauler=" + Plugin.SafeName(hauler)
                + " item=" + Plugin.Describe(failed)
                + " reason=" + reason
                + " action=marked-failed"
                + " pending=" + session.Pending.Count
                + " backlog=" + session.Backlog.Count
                + " carried=" + session.Carried.Count + ".");

            if (session.Mode == BigHaulPaintMode.Drag && SameItem(GetDragSlotItem(hauler), failed))
            {
                foreach (var pending in session.Pending.ToList())
                    BigHaulRegistry.MarkState(pending, "Cancelled", "drag session blocked by undroppable carried item");

                foreach (var backlog in session.Backlog.ToList())
                    BigHaulRegistry.MarkState(backlog, "Cancelled", "drag session blocked by undroppable carried item");

                session.Pending.Clear();
                session.Backlog.Clear();
                session.DropPhase = false;
                CompleteSession(hauler, session, "drag item has no valid drop target");
                return;
            }

            ClearDropPhaseIfEmpty(hauler, session);
        }

        private static bool TryDeferDragDropSearch(CondOwner hauler, BigLooseHaulSession session, CondOwner item, string reason)
        {
            if (hauler == null || session == null || session.Mode != BigHaulPaintMode.Drag || item == null)
                return false;

            var dragged = GetDragSlotItem(hauler);
            var failed = StackHead(item) ?? item;
            if (!SameItem(dragged, failed))
                return false;

            var id = SafeId(failed);
            if (string.IsNullOrEmpty(id))
                return false;

            int attempts;
            session.DropSearchAttempts.TryGetValue(id, out attempts);

            if (attempts >= MaxDragDropSearchAttempts)
            {
                if (QueueStopDrag(hauler))
                {
                    session.FailedDropIds.Add(id);
                    RemoveSame(session.Carried, failed);
                    RemoveSame(session.Pending, failed);
                    RemoveSame(session.Backlog, failed);
                    session.DropPhase = false;
                    ClearDropSearchState(session, failed);
                    BigHaulRegistry.MarkState(failed, "SkippedNoPath", "drop target unavailable after drag drop search");
                    Plugin.Warn("[BIGDragDropSearch] hauler=" + Plugin.SafeName(hauler)
                        + " item=" + Plugin.Describe(failed)
                        + " reason=" + reason
                        + " attempts=" + attempts
                        + " action=stop-drag-and-skip"
                        + " pending=" + session.Pending.Count
                        + " backlog=" + session.Backlog.Count + ".");
                    return true;
                }

                return false;
            }

            attempts++;
            session.DropSearchAttempts[id] = attempts;
            session.NextDropSearchAtUtc[id] = DateTime.UtcNow.AddSeconds(DragDropSearchRetrySeconds);

            if (attempts == 1 || attempts % 10 == 0)
            {
                Plugin.Warn("[BIGDragDropSearch] hauler=" + Plugin.SafeName(hauler)
                    + " item=" + Plugin.Describe(failed)
                    + " reason=" + reason
                    + " attempts=" + attempts + "/" + MaxDragDropSearchAttempts
                    + " action=retry-later"
                    + " retrySeconds=" + DragDropSearchRetrySeconds.ToString("0.0") + ".");
            }

            return true;
        }

        private static bool IsDropSearchCoolingDown(BigLooseHaulSession session, CondOwner item)
        {
            if (session == null || item == null)
                return false;

            var id = SafeId(StackHead(item) ?? item);
            if (string.IsNullOrEmpty(id))
                return false;

            DateTime next;
            return session.NextDropSearchAtUtc.TryGetValue(id, out next) && DateTime.UtcNow < next;
        }

        private static void ClearDropSearchState(BigLooseHaulSession session, CondOwner item)
        {
            if (session == null || item == null)
                return;

            var id = SafeId(StackHead(item) ?? item);
            if (string.IsNullOrEmpty(id))
                return;

            session.DropSearchAttempts.Remove(id);
            session.NextDropSearchAtUtc.Remove(id);
        }

        private static bool IsDropFailed(BigLooseHaulSession session, CondOwner item)
        {
            if (session == null || item == null || session.FailedDropIds.Count == 0)
                return false;

            var id = SafeId(StackHead(item) ?? item);
            return !string.IsNullOrEmpty(id) && session.FailedDropIds.Contains(id);
        }

        private static bool TryQueueFinalHelperRelease(CondOwner hauler, BigLooseHaulSession session)
        {
            if (hauler == null || session == null || session.Mode != BigHaulPaintMode.Haul)
                return false;

            if (session.CurrentPickup != null || session.CurrentDrop != null || session.CurrentHelperAcquire != null)
                return false;

            if (session.Pending.Count > 0 || session.Backlog.Count > 0)
                return false;

            SyncCarriedFromHauler(hauler, session, "before-helper-release");
            if (session.Carried.Count > 0)
            {
                session.DropPhase = true;
                QueueNextDrop(hauler, session);
                return true;
            }

            foreach (var helper in FindSessionOwnedHelpers(hauler, session))
            {
                DropPlan plan;
                if (!TryFindDropPlan(hauler, helper.Item, out plan))
                {
                    MarkSessionHelperReleased(session, helper.Item, helper.Slot, "no helper drop target", "retained");
                    Plugin.Warn("[BIGHelperRelease] no drop target; retaining helper. hauler=" + Plugin.SafeName(hauler) + " helper=" + Plugin.Describe(helper.Item) + " slot=" + helper.Slot + ".");
                    continue;
                }

                plan.Item = helper.Item;
                plan.HelperRelease = true;
                plan.HelperSlot = helper.Slot;
                session.CurrentDrop = plan;
                session.DropPhase = true;

                if (!QueueWalkToDrop(hauler, plan))
                {
                    session.CurrentDrop = null;
                    Plugin.Warn("[BIGHelperRelease] failed to queue walk. hauler=" + Plugin.SafeName(hauler) + " helper=" + Plugin.Describe(helper.Item) + " target=" + plan.Summary + ".");
                    continue;
                }

                if (helper.Slot == BigHaulHelperSlot.Drag && !QueueStopDrag(hauler))
                {
                    session.CurrentDrop = null;
                    Plugin.Warn("[BIGHelperRelease] failed to queue drag stop. hauler=" + Plugin.SafeName(hauler) + " helper=" + Plugin.Describe(helper.Item) + " target=" + plan.Summary + ".");
                    return true;
                }

                Plugin.ModInfo("[BIGHelperRelease] hauler=" + Plugin.SafeName(hauler)
                    + " helper=" + Plugin.Describe(helper.Item)
                    + " slot=" + helper.Slot
                    + " target=" + plan.Summary
                    + " queue=" + Plugin.QueueSummary(hauler) + ".");
                return true;
            }

            return false;
        }

        private static void FinishDrop(CondOwner hauler, BigLooseHaulSession session)
        {
            var plan = session.CurrentDrop;
            session.CurrentDrop = null;

            if (plan == null || plan.Item == null || plan.Item.bDestroyed)
            {
                Plugin.Warn("[BIGDropDone] drop target vanished. hauler=" + Plugin.SafeName(hauler) + ".");
                return;
            }

            var item = ResolveOwnedCarried(hauler, plan.Item);
            if (!IsOwnedByHauler(hauler, item))
            {
                RemoveSame(session.Carried, item ?? plan.Item);
                if (plan.HelperRelease && plan.HelperSlot == BigHaulHelperSlot.Drag)
                {
                    MarkSessionHelperReleased(session, plan.Item, plan.HelperSlot, plan.Summary, "released");
                    SyncCarriedFromHauler(hauler, session, "after-helper-release");
                    ClearDropPhaseIfEmpty(hauler, session);
                    return;
                }

                if (session.Mode == BigHaulPaintMode.Drag)
                {
                    BigHaulRegistry.MarkState(plan.Item, "Done", "BIG drag drop complete");
                    Plugin.ModInfo("[BIGDragDropDone] hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(plan.Item) + " target=" + plan.Summary + " pending=" + session.Pending.Count + " carried=" + session.Carried.Count + ".");
                    return;
                }

                Plugin.Warn("[" + ModeMarker(session.Mode, "DropDone") + "] carried item no longer belonged to hauler. planned=" + Plugin.Describe(plan.Item) + " resolved=" + Plugin.Describe(item) + ".");
                return;
            }

            plan.Item = item;

            if (!ExecuteDrop(hauler, plan))
            {
                if (!ContainsSame(session.Carried, item))
                    session.Carried.Add(item);

                Plugin.Warn("[BIGDropDone] failed: hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " target=" + plan.Summary + ".");
                return;
            }

            RemoveSame(session.Carried, item);
            if (plan.HelperRelease)
            {
                MarkSessionHelperReleased(session, item, plan.HelperSlot, plan.Summary, "released");
            }
            else
            {
                BigHaulRegistry.MarkState(item, "Done", "BIG drop complete");
                Plugin.ModInfo("[BIGDropDone] hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " target=" + plan.Summary + " pending=" + session.Pending.Count + " carried=" + session.Carried.Count + ".");
            }

            SyncCarriedFromHauler(hauler, session, "after-drop");
            ClearDropPhaseIfEmpty(hauler, session);
        }

        private static List<SessionHelperCandidate> FindSessionOwnedHelpers(CondOwner hauler, BigLooseHaulSession session)
        {
            var helpers = new List<SessionHelperCandidate>();
            var seen = new HashSet<string>();

            if (hauler == null || session == null || session.SessionOwnedHelperIds.Count == 0)
                return helpers;

            Action<CondOwner> addIfOwned = helper =>
            {
                var id = SafeId(helper);
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    return;

                if (!session.SessionOwnedHelperIds.Contains(id) || session.PreExistingHelperIds.Contains(id))
                    return;

                var slot = Plugin.GetSessionHelperSlot(helper);
                if (slot == BigHaulHelperSlot.None)
                    return;

                helpers.Add(new SessionHelperCandidate
                {
                    Item = helper,
                    Slot = slot,
                    Score = slot == BigHaulHelperSlot.Hand ? 0f : 1f
                });
            };

            try
            {
                foreach (var helper in Plugin.GetHaulHelpersForHauler(hauler))
                    addIfOwned(helper);

                addIfOwned(GetDragSlotItem(hauler));
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGHelperRelease] helper scan failed: hauler=" + Plugin.SafeName(hauler) + " error=" + ex.Message);
            }

            return helpers.OrderBy(helper => helper.Score).ToList();
        }

        private static void MarkSessionHelperReleased(BigLooseHaulSession session, CondOwner helper, BigHaulHelperSlot slot, string target, string result)
        {
            var id = SafeId(helper);
            if (!string.IsNullOrEmpty(id))
                session.SessionOwnedHelperIds.Remove(id);

            if (helper != null)
                BigHaulRegistry.MarkState(helper, "Done", "BIG helper " + result);

            Plugin.ModInfo("[BIGHelperReleased] hauler=" + session.HaulerName
                + " helper=" + Plugin.Describe(helper)
                + " slot=" + slot
                + " result=" + result
                + " target=" + target
                + " remainingOwnedHelpers=" + session.SessionOwnedHelperIds.Count + ".");
        }

        private static void ClearDropPhaseIfEmpty(CondOwner hauler, BigLooseHaulSession session)
        {
            if (session == null || session.Carried.Count != 0)
                return;

            session.DropPhase = false;
            session.DropAnchorShip = null;
            session.DropAnchorZone = null;
            session.DropAnchorPosition = Vector3.zero;
            Plugin.ModInfo("[BIGDropPhase] hauler=" + Plugin.SafeName(hauler) + " state=finished pending=" + session.Pending.Count + ".");
        }

        private static bool TryFindDropPlan(CondOwner hauler, CondOwner item, out DropPlan best, bool allowAnyStockpileZone = false)
        {
            best = null;

            try
            {
                if (hauler == null || item == null || hauler.ship == null)
                    return false;

                var zones = hauler.ship.GetZones("IsZoneStockpile", hauler, true);
                if (zones == null || zones.Count == 0)
                    return false;

                zones.Sort();
                var validDest = DataHandler.GetCondTrigger("TIsValidHaulDest");
                var candidates = new List<DropPlan>();

                foreach (var zone in zones)
                {
                    if (zone == null)
                        continue;

                    Ship zoneShip;
                    if (zone.strRegID == null || CrewSim.system == null || CrewSim.system.dictShips == null || !CrewSim.system.dictShips.TryGetValue(zone.strRegID, out zoneShip))
                        continue;

                    var categoryMatch = MatchesZoneCategory(item, zone);
                    if (!categoryMatch && !allowAnyStockpileZone)
                        continue;

                    var zonePenalty = categoryMatch ? 0 : AnyStockpileZonePenalty;
                    var summaryPrefix = categoryMatch ? "" : "any-zone-";

                    if (validDest != null)
                    {
                        foreach (var existing in zoneShip.GetCOsInZone(zone, validDest, false))
                        {
                            if (existing == null || existing == item || existing.bDestroyed)
                                continue;

                            if (existing.CanStackOnItem(item) < item.StackCount)
                                continue;

                            var tile = zoneShip.GetTileAtWorldCoords1(existing.tf.position.x, existing.tf.position.y, true);
                            if (tile == null)
                                continue;

                            candidates.Add(new DropPlan
                            {
                                TargetShip = zoneShip,
                                Zone = zone,
                                StackTarget = existing,
                                WalkTarget = existing,
                                DropPosition = existing.tf.position,
                                Score = ScoreDrop(hauler, existing.tf.position, zonePenalty),
                                Summary = summaryPrefix + "stack ship=" + zoneShip.strRegID + " zone=" + zone.strName + " target=" + Plugin.SafeName(existing)
                            });
                        }
                    }

                    var itemComponent = item.GetComponent<Item>();
                    if (itemComponent == null)
                        continue;

                    var emptyCandidates = BuildEmptyDropPlans(hauler, item, itemComponent, zoneShip, zone);
                    if (emptyCandidates.Count > 0)
                    {
                        if (!categoryMatch)
                        {
                            foreach (var candidate in emptyCandidates)
                            {
                                candidate.Score += zonePenalty;
                                candidate.Summary = summaryPrefix + candidate.Summary;
                            }
                        }

                        candidates.AddRange(emptyCandidates);
                        continue;
                    }

                    Vector3 fits;
                    if (TileUtils.TryFitItem(itemComponent, zoneShip, zone, out fits))
                    {
                        var tile = zoneShip.GetTileAtWorldCoords1(fits.x, fits.y, true);
                        if (tile != null && tile.coProps != null)
                        {
                            var score = ScoreDrop(hauler, fits, 1000 + zonePenalty);
                            if (hauler != null)
                            {
                                BigLooseHaulSession session;
                                if (ActiveSessions.TryGetValue(SafeId(hauler), out session) && session.DropPhase && session.DropAnchorShip == zoneShip && session.DropAnchorZone == zone)
                                    score = ScoreDropAnchor(session, fits) + zonePenalty;
                            }

                            candidates.Add(new DropPlan
                            {
                                TargetShip = zoneShip,
                                Zone = zone,
                                WalkTarget = tile.coProps,
                                DropPosition = new Vector3(fits.x, fits.y, item.tf.position.z),
                                Score = score,
                                Summary = summaryPrefix + "empty-fallback ship=" + zoneShip.strRegID + " zone=" + zone.strName + " tile=" + tile.Index
                            });
                        }
                    }
                }

                best = candidates.OrderBy(candidate => candidate.Score).FirstOrDefault();
                BigLooseHaulSession anchorSession;
                if (best != null && hauler != null && ActiveSessions.TryGetValue(SafeId(hauler), out anchorSession) && anchorSession.DropPhase)
                    EnsureDropAnchor(anchorSession, best);
                return best != null;
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGDropPlan] failed: item=" + Plugin.Describe(item) + " error=" + ex.Message);
                return false;
            }
        }

        private static List<DropPlan> BuildEmptyDropPlans(CondOwner hauler, CondOwner item, Item itemComponent, Ship zoneShip, JsonZone zone)
        {
            var plans = new List<DropPlan>();

            try
            {
                if (item == null || itemComponent == null || zoneShip == null || zone == null || zone.aTiles == null || zone.aTiles.Length == 0)
                    return plans;

                BigLooseHaulSession session = null;
                if (hauler != null)
                    ActiveSessions.TryGetValue(SafeId(hauler), out session);

                Vector3 anchor;
                if (!TryGetDropSpiralAnchor(session, zoneShip, zone, out anchor))
                    anchor = item.tf != null ? item.tf.position : Vector3.zero;

                foreach (var tileIndex in zone.aTiles)
                {
                    var coords = zoneShip.GetWorldCoordsAtTileIndex1(tileIndex);
                    var pos = new Vector3(coords.x, coords.y, item.tf != null ? item.tf.position.z : 0f);
                    var tile = zoneShip.GetTileAtWorldCoords1(pos.x, pos.y, true);
                    if (tile == null || tile.coProps == null)
                        continue;

                    if (!CanItemFitAt(itemComponent, zoneShip, zone, pos))
                        continue;

                    AddEmptyDropPlan(plans, hauler, zoneShip, zone, tile.coProps, tile.Index, pos, anchor, "empty-spiral");
                }

                if (plans.Count == 0)
                {
                    foreach (var tileIndex in zone.aTiles)
                    {
                        var coords = zoneShip.GetWorldCoordsAtTileIndex1(tileIndex);
                        var z = item.tf != null ? item.tf.position.z : 0f;

                        foreach (var pos in BuildOffsetDropPositions(coords.x, coords.y, z))
                        {
                            var tile = zoneShip.GetTileAtWorldCoords1(pos.x, pos.y, true);
                            if (tile == null || tile.coProps == null)
                                continue;

                            if (!CanItemFitAt(itemComponent, zoneShip, zone, pos))
                                continue;

                            AddEmptyDropPlan(plans, hauler, zoneShip, zone, tile.coProps, tile.Index, pos, anchor, "empty-offset");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGDropSpiral] failed: item=" + Plugin.Describe(item) + " error=" + ex.Message);
            }

            return plans;
        }

        private static IEnumerable<Vector3> BuildOffsetDropPositions(float x, float y, float z)
        {
            yield return new Vector3(x + 0.5f, y, z);
            yield return new Vector3(x - 0.5f, y, z);
            yield return new Vector3(x, y + 0.5f, z);
            yield return new Vector3(x, y - 0.5f, z);
            yield return new Vector3(x + 0.5f, y + 0.5f, z);
            yield return new Vector3(x - 0.5f, y + 0.5f, z);
            yield return new Vector3(x + 0.5f, y - 0.5f, z);
            yield return new Vector3(x - 0.5f, y - 0.5f, z);
        }

        private static void AddEmptyDropPlan(List<DropPlan> plans, CondOwner hauler, Ship zoneShip, JsonZone zone, CondOwner walkTarget, int tileIndex, Vector3 pos, Vector3 anchor, string mode)
        {
            if (plans == null || zoneShip == null || zone == null || walkTarget == null)
                return;

            plans.Add(new DropPlan
            {
                TargetShip = zoneShip,
                Zone = zone,
                WalkTarget = walkTarget,
                DropPosition = pos,
                Score = ScoreDropSpiral(hauler, anchor, pos, 1000),
                Summary = mode + " ship=" + zoneShip.strRegID + " zone=" + zone.strName + " tile=" + tileIndex
            });
        }

        private static bool CanItemFitAt(Item itemComponent, Ship zoneShip, JsonZone zone, Vector3 pos)
        {
            try
            {
                return itemComponent != null && itemComponent.CheckFit(pos, zoneShip, null, zone);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetDropSpiralAnchor(BigLooseHaulSession session, Ship zoneShip, JsonZone zone, out Vector3 anchor)
        {
            anchor = Vector3.zero;

            if (session != null
                && session.DropPhase
                && session.DropAnchorShip == zoneShip
                && session.DropAnchorZone == zone)
            {
                anchor = session.DropAnchorPosition;
                return true;
            }

            return TryGetZoneSpiralAnchor(zoneShip, zone, out anchor);
        }

        private static bool TryGetZoneSpiralAnchor(Ship zoneShip, JsonZone zone, out Vector3 anchor)
        {
            anchor = Vector3.zero;

            try
            {
                if (zoneShip == null || zone == null || zone.aTiles == null || zone.aTiles.Length == 0)
                    return false;

                var coords = zone.aTiles
                    .Select(tileIndex => zoneShip.GetWorldCoordsAtTileIndex1(tileIndex))
                    .ToList();

                if (coords.Count == 0)
                    return false;

                var avgX = coords.Average(pos => pos.x);
                var avgY = coords.Average(pos => pos.y);
                var best = coords
                    .OrderBy(pos =>
                    {
                        var dx = pos.x - avgX;
                        var dy = pos.y - avgY;
                        return dx * dx + dy * dy;
                    })
                    .First();

                anchor = new Vector3(best.x, best.y, 0f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool QueueWalkToDrop(CondOwner hauler, DropPlan plan)
        {
            if (hauler == null || plan == null || plan.WalkTarget == null)
                return false;

            Interaction walk = null;
            JsonInteraction walkTemplate;
            if (DataHandler.dictInteractions != null && DataHandler.dictInteractions.TryGetValue("ACTHaulItem", out walkTemplate) && walkTemplate != null)
                walk = new Interaction(walkTemplate);

            if (walk == null)
                walk = DataHandler.GetInteraction("ACTHaulItem");

            if (walk == null)
                return false;

            walk.strName = "BIGWalkToDrop";
            walk.strTitle = "BIG Walk To Drop";
            walk.strDesc = "[us] [is] walking to a BIG drop target.";
            walk.strTooltip = "BIG internal movement.";
            walk.strDuty = null;
            walk.strMapIcon = null;
            walk.strTargetPoint = "use";
            walk.fTargetPointRange = 1f;
            walk.fDuration = 0.000027;
            walk.bManual = true;
            walk.bNoRemember = true;

            return hauler.QueueInteraction(plan.WalkTarget, walk);
        }

        private static bool QueueStopDrag(CondOwner hauler)
        {
            if (hauler == null)
                return false;

            var stopDrag = Plugin.GetStopDragInteraction();
            if (stopDrag == null)
                return false;

            stopDrag.bManual = true;
            return hauler.QueueInteraction(hauler, stopDrag);
        }

        private static bool ExecuteDrop(CondOwner hauler, DropPlan plan)
        {
            try
            {
                if (hauler == null || plan == null || plan.Item == null || plan.TargetShip == null)
                    return false;

                var item = StackHead(plan.Item);
                if (item == null || item.bDestroyed)
                    return false;

                if (plan.StackTarget != null && !plan.StackTarget.bDestroyed && plan.StackTarget.CanStackOnItem(item) >= item.StackCount)
                {
                    Plugin.ModInfo("[BIGDropVisual] mode=stack before item=" + Plugin.Describe(item) + " target=" + plan.Summary + " state=" + DropVisualState(item) + ".");
                    item.RemoveFromCurrentHome(true);
                    var remainder = plan.StackTarget.StackCO(item);
                    Plugin.ModInfo("[BIGDropVisual] mode=stack after item=" + Plugin.Describe(item) + " target=" + plan.Summary + " remainder=" + Plugin.Describe(remainder) + " stackState=" + DropVisualState(plan.StackTarget) + ".");
                    return remainder == null;
                }

                if (hauler.tf == null)
                    return false;

                var offset = plan.DropPosition - hauler.tf.position;
                var sortingProvider = BuildDropSortingProvider(plan.TargetShip, plan.DropPosition);
                Plugin.ModInfo("[BIGDropVisual] mode=vanilla-empty before item=" + Plugin.Describe(item) + " target=" + plan.Summary + " offset=(" + offset.x.ToString("0.00") + "," + offset.y.ToString("0.00") + ") state=" + DropVisualState(item) + ".");

                var leftover = hauler.DropCO(item, false, plan.TargetShip, offset.x, offset.y, true, sortingProvider);

                Plugin.ModInfo("[BIGDropVisual] mode=vanilla-empty after item=" + Plugin.Describe(item) + " target=" + plan.Summary + " leftover=" + Plugin.Describe(leftover) + " state=" + DropVisualState(item) + ".");
                return leftover == null && item.ship == plan.TargetShip;
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGDropExecute] failed: " + ex.Message);
                return false;
            }
        }

        private static Func<int[], int[]> BuildDropSortingProvider(Ship ship, Vector3 dropPosition)
        {
            try
            {
                if (ship == null)
                    return null;

                var targetTile = ship.GetTileAtWorldCoords1(dropPosition.x, dropPosition.y, true);
                if (targetTile == null)
                    return null;

                var targetIndex = targetTile.Index;
                return tiles =>
                {
                    if (tiles == null || tiles.Length == 0)
                        return tiles;

                    return tiles
                        .OrderBy(tileIndex => tileIndex == targetIndex ? 0 : 1)
                        .ThenBy(tileIndex => DropTileSpiralScore(ship, tileIndex, dropPosition))
                        .ToArray();
                };
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGDropSort] failed: " + ex.Message);
                return null;
            }
        }

        private static int DropTileSpiralScore(Ship ship, int tileIndex, Vector3 anchor)
        {
            try
            {
                var coords = ship.GetWorldCoordsAtTileIndex1(tileIndex);
                return ScoreSpiralFromAnchor(anchor, new Vector3(coords.x, coords.y, anchor.z));
            }
            catch
            {
                return int.MaxValue;
            }
        }

        private static string DropVisualState(CondOwner item)
        {
            if (item == null)
                return "<null>";

            try
            {
                var pos = item.tf != null ? item.tf.position : Vector3.zero;
                var shipId = item.ship != null ? item.ship.strRegID : "<none>";
                var parent = item.objCOParent != null ? Plugin.SafeName(item.objCOParent) : "<none>";
                var tile = "<none>";

                if (item.ship != null)
                {
                    var tileRef = item.ship.GetTileAtWorldCoords1(pos.x, pos.y, true);
                    if (tileRef != null)
                        tile = tileRef.Index.ToString();
                }

                return "ship=" + shipId
                    + " tile=" + tile
                    + " parent=" + parent
                    + " visible=" + item.Visible
                    + " pos=(" + pos.x.ToString("0.00") + "," + pos.y.ToString("0.00") + "," + pos.z.ToString("0.00") + ")"
                    + " render=" + RendererState(item);
            }
            catch (Exception ex)
            {
                return "<state-error " + ex.Message + ">";
            }
        }

        private static string RendererState(CondOwner item)
        {
            try
            {
                if (item == null || item.tf == null)
                    return "<none>";

                var renderers = item.tf.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0)
                    return "<none>";

                var renderer = renderers[0];
                if (renderer == null)
                    return "count=" + renderers.Length + " first=<null>";

                return "count=" + renderers.Length
                    + " firstEnabled=" + renderer.enabled
                    + " layer=" + renderer.sortingLayerName
                    + " order=" + renderer.sortingOrder;
            }
            catch (Exception ex)
            {
                return "<renderer-error " + ex.Message + ">";
            }
        }

        private static bool HasAnyFittablePending(CondOwner hauler, BigLooseHaulSession session)
        {
            foreach (var item in session.Pending)
            {
                if (IsValidPendingItem(hauler, item, session.Mode) && CanAcquireNext(hauler, item, session.Mode))
                    return true;
            }

            if (session.Pending.Count == 0 && session.Backlog.Any(item => IsValidPendingItem(hauler, item, session.Mode) && CanAcquireNext(hauler, item, session.Mode)))
                return true;

            return false;
        }

        private static CondOwner FindNearestFittablePending(CondOwner hauler, BigLooseHaulSession session)
        {
            CondOwner best = null;
            var bestScore = float.MaxValue;
            var noPath = new List<CondOwner>();

            foreach (var item in session.Pending)
            {
                if (!IsValidPendingItem(hauler, item, session.Mode))
                    continue;

                string fitReason;
                if (!CanAcquireNext(hauler, item, session.Mode, out fitReason))
                    continue;

                var pickup = Plugin.GetAcquireInteractionForMode(hauler, item, session.Mode, out var pickupReason);
                if (pickup == null)
                {
                    noPath.Add(item);
                    continue;
                }

                pickup.bManual = true;

                var score = PickupSortScore(hauler, item, pickup);
                if (score >= float.MaxValue * 0.5f)
                {
                    noPath.Add(item);
                    continue;
                }

                if (score >= bestScore)
                    continue;

                best = item;
                bestScore = score;
            }

            foreach (var item in noPath)
            {
                RemoveSame(session.Pending, item);
                RemoveSame(session.Backlog, item);
                BigHaulRegistry.MarkState(item, "SkippedNoPath", "no reachable path before pickup");
            }

            if (noPath.Count > 0)
                Plugin.Warn("[" + ModeMarker(session.Mode, "SkipNoPath") + "] hauler=" + Plugin.SafeName(hauler) + " count=" + noPath.Count + " pending=" + session.Pending.Count + " backlog=" + session.Backlog.Count + ".");

            if (best != null)
            {
                string fitReason;
                CanAcquireNext(hauler, best, session.Mode, out fitReason);
                Plugin.ModInfo("[" + ModeMarker(session.Mode, "PickupScore") + "] hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(best) + " score=" + bestScore.ToString("0.0") + " sameShip=" + IsSameShip(hauler, best) + " fit=" + fitReason + ".");
            }

            return best;
        }

        private static bool CanAcquireNext(CondOwner hauler, CondOwner item, BigHaulPaintMode mode)
        {
            string reason;
            return CanAcquireNext(hauler, item, mode, out reason);
        }

        private static bool CanAcquireNext(CondOwner hauler, CondOwner item, BigHaulPaintMode mode, out string reason)
        {
            reason = null;

            switch (mode)
            {
                case BigHaulPaintMode.Drag:
                    if (GetDragSlotItem(hauler) == null && !HasCond(hauler, "IsDragging"))
                    {
                        reason = "drag-slot-free";
                        return true;
                    }

                    reason = "drag-slot-occupied";
                    return false;
                default:
                    string helperReason;
                    if (CanFitInventory(hauler, item))
                    {
                        reason = "inventory";
                        return true;
                    }

                    if (Plugin.CanFitHaulHelper(hauler, item, out helperReason))
                    {
                        reason = "helper:" + helperReason;
                        return true;
                    }

                    reason = "no-space:" + helperReason;
                    return false;
            }
        }

        private static CondOwner GetDragSlotItem(CondOwner hauler)
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

        private static bool HasCond(CondOwner co, string cond)
        {
            try
            {
                return co != null && co.HasCond(cond);
            }
            catch
            {
                return false;
            }
        }

        private static bool CanFitInventory(CondOwner hauler, CondOwner item)
        {
            try
            {
                if (hauler == null || item == null)
                    return false;

                if (hauler.objContainer != null && hauler.objContainer.CanFit(item, false, true))
                    return true;

                if (hauler.compSlots == null)
                    return false;

                foreach (var slot in hauler.compSlots.GetSlotsHeldFirst(true))
                {
                    if (slot != null && slot.CanFit(item, false, true))
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGCanFit] failed: hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " error=" + ex.Message);
                return false;
            }
        }

        private static bool MatchesZoneCategory(CondOwner item, JsonZone zone)
        {
            if (zone.categoryConds == null || zone.categoryConds.Length == 0)
                return true;

            foreach (var cond in zone.categoryConds)
            {
                try
                {
                    if (item.HasCond(cond))
                        return true;
                }
                catch
                {
                    // Bad or stale category conditions should just fail this zone.
                }
            }

            return false;
        }

        private static void PruneSession(CondOwner hauler, BigLooseHaulSession session)
        {
            session.Pending = session.Pending
                .Where(item => IsRetainablePendingItem(hauler, item, session))
                .Where(item => !IsDropFailed(session, item))
                .GroupBy(SafeId)
                .Select(group => group.First())
                .ToList();

            session.Backlog = session.Backlog
                .Where(item => IsRetainablePendingItem(hauler, item, session))
                .Where(item => !IsDropFailed(session, item))
                .GroupBy(SafeId)
                .Select(group => group.First())
                .ToList();

            session.Carried = session.Carried
                .Where(item => item != null && !item.bDestroyed)
                .Select(StackHead)
                .Where(item => item != null)
                .Where(item => !IsDropFailed(session, item))
                .Where(item => session.Mode != BigHaulPaintMode.Haul || Plugin.GetSessionHelperSlot(item) == BigHaulHelperSlot.None)
                .GroupBy(SafeId)
                .Select(group => group.First())
                .ToList();
        }

        private static bool IsRetainablePendingItem(CondOwner hauler, CondOwner item, BigLooseHaulSession session)
        {
            try
            {
                if (session == null)
                    return false;

                if (IsValidPendingItem(hauler, item, session.Mode))
                    return true;

                if (session.Mode != BigHaulPaintMode.Haul)
                    return false;

                string reason;
                return Plugin.IsHaulCandidateIgnoringCurrentCapacity(item, out reason);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidPendingItem(CondOwner hauler, CondOwner item, BigHaulPaintMode mode)
        {
            try
            {
                if (mode == BigHaulPaintMode.Haul)
                {
                    BigHaulHelperSlot helperSlot;
                    string helperReason;
                    if (Plugin.IsSessionHelperCandidate(item, out helperSlot, out helperReason))
                        return false;
                }

                string reason;
                return Plugin.IsCandidateForMode(hauler, item, mode, out reason);
            }
            catch
            {
                return false;
            }
        }

        private static void RequeuePending(BigLooseHaulSession session, CondOwner item)
        {
            if (session == null || item == null)
                return;

            RemoveSame(session.Pending, item);
            RemoveSame(session.Backlog, item);
            session.Pending.Insert(0, item);
        }

        internal static bool CancelSession(CondOwner hauler, string reason, bool clearQueue)
        {
            if (hauler == null)
                return false;

            BigLooseHaulSession session;
            var haulerId = SafeId(hauler);
            if (string.IsNullOrEmpty(haulerId) || !ActiveSessions.TryGetValue(haulerId, out session))
                return false;

            ActiveSessions.Remove(haulerId);

            if (session.CurrentPickup != null && IsValidPendingItem(hauler, session.CurrentPickup, session.Mode))
                BigHaulRegistry.MarkState(session.CurrentPickup, "Cancelled", reason);

            if (session.CurrentHelperAcquire != null)
                BigHaulRegistry.MarkState(session.CurrentHelperAcquire, "Cancelled", reason);

            foreach (var item in session.Pending.ToList())
                BigHaulRegistry.MarkState(item, "Cancelled", reason);

            foreach (var item in session.Backlog.ToList())
                BigHaulRegistry.MarkState(item, "Cancelled", reason);

            foreach (var helper in session.HandHelperCandidates.Concat(session.DragHelperCandidates).ToList())
                BigHaulRegistry.MarkState(helper, "Cancelled", reason);

            if (clearQueue)
                SafeClearQueue(hauler, "cancel BIG session: " + reason);

            Plugin.Warn("[BIGCancel] hauler=" + Plugin.SafeName(hauler)
                + " reason=" + reason
                + " pending=" + session.Pending.Count
                + " backlog=" + session.Backlog.Count
                + " carried=" + session.Carried.Count
                + " currentPickup=" + Plugin.Describe(session.CurrentPickup)
                + " currentHelper=" + Plugin.Describe(session.CurrentHelperAcquire)
                + " currentDrop=" + (session.CurrentDrop != null ? session.CurrentDrop.Summary : "<none>")
                + ".");
            return true;
        }

        internal static bool PauseSessionForPlayerOrder(CondOwner hauler, string reason, bool clearQueue)
        {
            if (hauler == null)
                return false;

            BigLooseHaulSession session;
            var haulerId = SafeId(hauler);
            if (string.IsNullOrEmpty(haulerId) || !ActiveSessions.TryGetValue(haulerId, out session))
                return false;

            SyncCarriedFromHauler(hauler, session, "pause-player-order");

            if (session.CurrentPickup != null)
            {
                var pickup = session.CurrentPickup;
                session.CurrentPickup = null;

                if (IsOwnedByHauler(hauler, pickup))
                {
                    var carried = StackHead(pickup);
                    if (!ContainsSame(session.Carried, carried))
                        session.Carried.Add(carried);

                    BigHaulRegistry.MarkState(pickup, "PickedUp", "BIG pickup completed before pause");
                }
                else if (IsValidPendingItem(hauler, pickup, session.Mode) && !ContainsSame(session.Pending, pickup))
                {
                    session.Pending.Insert(0, pickup);
                }
            }

            if (session.CurrentHelperAcquire != null)
            {
                var helper = session.CurrentHelperAcquire;
                var helperSlot = session.CurrentHelperSlot;
                session.CurrentHelperAcquire = null;
                session.CurrentHelperSlot = BigHaulHelperSlot.None;

                var dragged = helperSlot == BigHaulHelperSlot.Drag ? GetDragSlotItem(hauler) : null;
                if (IsOwnedByHauler(hauler, helper) || SameItem(dragged, helper))
                {
                    var ownedHelper = SameItem(dragged, helper) ? dragged : ResolveOwnedCarried(hauler, helper);
                    var id = SafeId(ownedHelper ?? helper);
                    if (!string.IsNullOrEmpty(id))
                        session.SessionOwnedHelperIds.Add(id);

                    BigHaulRegistry.MarkState(ownedHelper ?? helper, "HelperAcquired", "BIG helper completed before pause");
                }
                else if (helperSlot == BigHaulHelperSlot.Hand && !ContainsSame(session.HandHelperCandidates, helper))
                {
                    session.HandHelperCandidates.Insert(0, helper);
                }
                else if (helperSlot == BigHaulHelperSlot.Drag && !ContainsSame(session.DragHelperCandidates, helper))
                {
                    session.DragHelperCandidates.Insert(0, helper);
                }
            }

            if (session.CurrentDrop != null)
            {
                var drop = session.CurrentDrop;
                session.CurrentDrop = null;

                var item = drop.Item != null ? ResolveOwnedCarried(hauler, drop.Item) : null;
                if (item != null && !IsOwnedByHauler(hauler, item))
                {
                    RemoveSame(session.Carried, item);
                    BigHaulRegistry.MarkState(item, "Done", "BIG drop completed before pause");
                }
                else if (session.Carried.Count > 0)
                {
                    session.DropPhase = true;
                }
            }

            if (clearQueue)
                SafeClearQueue(hauler, "pause BIG session: " + reason);

            session.AutoPausedLogged = false;
            Plugin.Warn("[BIGPause] hauler=" + Plugin.SafeName(hauler)
                + " reason=" + reason
                + " pending=" + session.Pending.Count
                + " backlog=" + session.Backlog.Count
                + " carried=" + session.Carried.Count
                + " dropPhase=" + session.DropPhase
                + " queue=" + Plugin.QueueSummary(hauler) + ".");
            return true;
        }

        internal static bool CancelPendingItemById(string itemId, string reason)
        {
            if (string.IsNullOrEmpty(itemId))
                return false;

            var changed = false;
            foreach (var session in ActiveSessions.Values.ToList())
            {
                var removed = session.Pending.RemoveAll(item => SafeId(item) == itemId);
                var removedBacklog = session.Backlog.RemoveAll(item => SafeId(item) == itemId);
                if (removed > 0)
                {
                    changed = true;
                    Plugin.Warn("[BIGCancelItem] hauler=" + session.HaulerName + " reason=" + reason + " itemId=" + itemId + " removed=" + removed + " pending=" + session.Pending.Count + ".");
                }

                if (removedBacklog > 0)
                {
                    changed = true;
                    Plugin.Warn("[BIGCancelItem] hauler=" + session.HaulerName + " reason=" + reason + " itemId=" + itemId + " removedBacklog=" + removedBacklog + " backlog=" + session.Backlog.Count + ".");
                }

                if (session.CurrentPickup != null && SafeId(session.CurrentPickup) == itemId)
                {
                    changed = true;
                    BigHaulRegistry.MarkState(session.CurrentPickup, "Cancelled", reason);
                    session.CurrentPickup = null;
                    Plugin.Warn("[BIGCancelItem] hauler=" + session.HaulerName + " reason=" + reason + " currentPickup=" + itemId + ".");
                }
            }

            if (BigHaulRegistry.CancelById(itemId, reason))
                changed = true;

            return changed;
        }

        private static bool IsAutoTaskingEnabled(CondOwner hauler)
        {
            try
            {
                return hauler != null && !hauler.HasCond("IsAIManual");
            }
            catch
            {
                return false;
            }
        }

        private static bool IsOwnedByHauler(CondOwner hauler, CondOwner item)
        {
            try
            {
                if (hauler == null || item == null)
                    return false;

                var cursor = item;
                for (var i = 0; i < 16 && cursor != null; i++)
                {
                    if (cursor == hauler || SafeId(cursor) == SafeId(hauler))
                        return true;

                    cursor = cursor.objCOParent;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static CondOwner ResolveOwnedCarried(CondOwner hauler, CondOwner item)
        {
            try
            {
                var head = StackHead(item);
                if (head != null && !head.bDestroyed && IsOwnedByHauler(hauler, head))
                    return head;

                if (RequiresExactCarriedResolution(head ?? item))
                    return null;

                var itemKey = InventoryCargoKey(item);
                if (string.IsNullOrEmpty(itemKey) || hauler == null)
                    return null;

                foreach (var candidate in hauler.GetCOsSafe(true))
                {
                    var candidateHead = StackHead(candidate);
                    if (candidateHead == null || candidateHead.bDestroyed)
                        continue;

                    if (InventoryCargoKey(candidateHead) == itemKey && IsOwnedByHauler(hauler, candidateHead))
                        return candidateHead;
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGResolveCarried] failed: item=" + Plugin.Describe(item) + " error=" + ex.Message);
            }

            return null;
        }

        private static bool RequiresExactCarriedResolution(CondOwner item)
        {
            return Plugin.GetSessionHelperSlot(item) != BigHaulHelperSlot.None
                || HasCond(item, "IsContainer");
        }

        private static void SyncCarriedFromHauler(CondOwner hauler, BigLooseHaulSession session, string reason)
        {
            try
            {
                if (hauler == null || session == null)
                    return;

                var synced = session.Carried
                    .Select(item => ResolveOwnedCarried(hauler, item))
                    .Where(item => item != null)
                    .Select(StackHead)
                    .Where(item => item != null && !item.bDestroyed)
                    .Where(item => !IsDropFailed(session, item))
                    .ToList();

                foreach (var candidate in hauler.GetCOsSafe(true))
                {
                    var head = StackHead(candidate);
                    if (head == null || head.bDestroyed)
                        continue;

                    if (IsDropFailed(session, head))
                        continue;

                    if (!IsOwnedByHauler(hauler, head))
                        continue;

                    if (session.Mode == BigHaulPaintMode.Haul && Plugin.GetSessionHelperSlot(head) != BigHaulHelperSlot.None)
                        continue;

                    if (!BigHaulRegistry.IsTracked(head))
                        continue;

                    if (!ContainsSame(synced, head))
                        synced.Add(head);
                }

                SyncHaulHelperContentsFromHauler(hauler, session, synced, reason);
                SyncInventoryDeltaFromHauler(hauler, session, synced, reason);

                if (session.Mode == BigHaulPaintMode.Drag)
                {
                    var dragged = GetDragSlotItem(hauler);
                    var draggedHead = StackHead(dragged);
                    if (draggedHead != null
                        && !draggedHead.bDestroyed
                        && Plugin.GetSessionHelperSlot(draggedHead) == BigHaulHelperSlot.None
                        && !IsDropFailed(session, draggedHead)
                        && !ContainsSame(synced, draggedHead))
                    {
                        synced.Add(draggedHead);
                    }
                }

                session.Carried = synced
                    .GroupBy(SafeId)
                    .Select(group => group.First())
                    .ToList();

                Plugin.ModInfo("[BIGCarrySync] hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " carried=" + session.Carried.Count + ".");
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGCarrySync] failed: hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " error=" + ex.Message);
            }
        }

        private static void SyncHaulHelperContentsFromHauler(CondOwner hauler, BigLooseHaulSession session, List<CondOwner> carried, string reason)
        {
            try
            {
                if (hauler == null || session == null || carried == null || session.Mode != BigHaulPaintMode.Haul)
                    return;

                var helpers = Plugin.GetHaulHelpersForHauler(hauler).ToList();
                if (helpers.Count == 0)
                    return;

                var scanned = 0;
                var added = 0;
                var helperSummaries = new List<string>();
                foreach (var helper in helpers)
                {
                    var helperAdded = 0;
                    var helperScanned = 0;
                    foreach (var cargo in Plugin.GetDirectHaulHelperCargo(helper))
                    {
                        scanned++;
                        helperScanned++;

                        var head = StackHead(cargo);
                        if (head == null || head.bDestroyed)
                            continue;

                        if (IsDropFailed(session, head))
                            continue;

                        if (!IsOwnedByHauler(hauler, head))
                            continue;

                        if (ContainsSame(carried, head))
                            continue;

                        TrackSessionCargoKey(session, head);
                        carried.Add(head);
                        added++;
                        helperAdded++;
                    }

                    helperSummaries.Add(Plugin.SafeName(helper)
                        + " slot=" + Plugin.GetActiveHaulHelperSlotName(hauler, helper)
                        + " scanned=" + helperScanned
                        + " added=" + helperAdded);
                }

                if (added > 0)
                {
                    Plugin.ModInfo("[BIGHelperDrain] hauler=" + Plugin.SafeName(hauler)
                        + " reason=" + reason
                        + " helpers=" + helpers.Count
                        + " scanned=" + scanned
                        + " added=" + added
                        + " activeHelpers=[" + string.Join("; ", helperSummaries.ToArray()) + "]"
                        + " carried=" + carried.Count + ".");
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGHelperDrain] failed: hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " error=" + ex.Message);
            }
        }

        private static void TrackSessionCargoKeys(BigLooseHaulSession session, IEnumerable<CondOwner> items)
        {
            if (items == null)
                return;

            foreach (var item in items)
                TrackSessionCargoKey(session, item);
        }

        private static void TrackSessionCargoKey(BigLooseHaulSession session, CondOwner item)
        {
            if (session == null || session.Mode != BigHaulPaintMode.Haul)
                return;

            var key = InventoryCargoKey(item);
            if (!string.IsNullOrEmpty(key))
                session.SessionCargoKeys.Add(key);
        }

        private static bool CanAddDirectCarried(BigLooseHaulSession session, CondOwner item, string reason)
        {
            if (session == null || item == null)
                return true;

            if (IsDropFailed(session, item))
                return false;

            if (session.Mode == BigHaulPaintMode.Drag && Plugin.GetSessionHelperSlot(item) != BigHaulHelperSlot.None)
                return false;

            if (session.Mode != BigHaulPaintMode.Haul)
                return true;

            var id = SafeId(item);
            if (string.IsNullOrEmpty(id) || !session.BaselineInventoryIds.Contains(id))
                return true;

            var key = InventoryCargoKey(item);
            if (session.BaselineMergeWarnings.Add(key ?? id))
            {
                Plugin.Warn("[BIGInventoryDelta] protected pre-existing stack from direct pickup add: hauler=" + session.HaulerName
                    + " reason=" + reason
                    + " key=" + (key ?? "<no key>")
                    + " item=" + Plugin.Describe(item) + ".");
            }

            return false;
        }

        private static void CaptureInventoryBaseline(CondOwner hauler, BigLooseHaulSession session, string reason)
        {
            try
            {
                if (hauler == null || session == null || session.Mode != BigHaulPaintMode.Haul || session.InventoryBaselineCaptured)
                    return;

                foreach (var item in GetOwnedInventoryCargo(hauler))
                {
                    var key = InventoryCargoKey(item);
                    if (string.IsNullOrEmpty(key))
                        continue;

                    AddCount(session.BaselineInventoryCounts, key, StackCountSafe(item));

                    var id = SafeId(item);
                    if (!string.IsNullOrEmpty(id))
                        session.BaselineInventoryIds.Add(id);
                }

                session.InventoryBaselineCaptured = true;
                Plugin.ModInfo("[BIGInventoryBaseline] hauler=" + Plugin.SafeName(hauler)
                    + " reason=" + reason
                    + " items=" + session.BaselineInventoryIds.Count
                    + " defs=" + session.BaselineInventoryCounts.Count
                    + " counts=" + CountSummary(session.BaselineInventoryCounts) + ".");
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGInventoryBaseline] failed: hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " error=" + ex.Message);
            }
        }

        private static void SyncInventoryDeltaFromHauler(CondOwner hauler, BigLooseHaulSession session, List<CondOwner> carried, string reason)
        {
            try
            {
                if (hauler == null || session == null || carried == null || session.Mode != BigHaulPaintMode.Haul)
                    return;

                if (!session.InventoryBaselineCaptured)
                    CaptureInventoryBaseline(hauler, session, "late-" + reason);

                if (session.SessionCargoKeys.Count == 0)
                    return;

                var current = GetOwnedInventoryCargo(hauler);
                var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var item in current)
                {
                    var key = InventoryCargoKey(item);
                    if (!string.IsNullOrEmpty(key))
                        AddCount(currentCounts, key, StackCountSafe(item));
                }

                var added = 0;
                var protectedBaseline = 0;
                foreach (var item in current)
                {
                    var head = StackHead(item);
                    if (head == null || head.bDestroyed || ContainsSame(carried, head))
                        continue;

                    if (IsDropFailed(session, head))
                        continue;

                    var key = InventoryCargoKey(head);
                    if (string.IsNullOrEmpty(key) || !session.SessionCargoKeys.Contains(key))
                        continue;

                    var baselineCount = CountFor(session.BaselineInventoryCounts, key);
                    var currentCount = CountFor(currentCounts, key);
                    if (currentCount <= baselineCount)
                        continue;

                    var id = SafeId(head);
                    if (!string.IsNullOrEmpty(id) && session.BaselineInventoryIds.Contains(id))
                    {
                        protectedBaseline++;
                        if (session.BaselineMergeWarnings.Add(key))
                        {
                            Plugin.Warn("[BIGInventoryDelta] protected pre-existing stack from auto-drop: hauler=" + Plugin.SafeName(hauler)
                                + " key=" + key
                                + " baseline=" + baselineCount
                                + " current=" + currentCount
                                + " item=" + Plugin.Describe(head) + ".");
                        }

                        continue;
                    }

                    carried.Add(head);
                    added++;
                }

                if (added > 0 || protectedBaseline > 0)
                {
                    Plugin.ModInfo("[BIGInventoryDelta] hauler=" + Plugin.SafeName(hauler)
                        + " reason=" + reason
                        + " scanned=" + current.Count
                        + " added=" + added
                        + " protectedBaseline=" + protectedBaseline
                        + " carried=" + carried.Count
                        + " sessionDefs=" + session.SessionCargoKeys.Count
                        + " current=" + CountSummary(currentCounts) + ".");
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGInventoryDelta] failed: hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " error=" + ex.Message);
            }
        }

        private static List<CondOwner> GetOwnedInventoryCargo(CondOwner hauler)
        {
            var result = new List<CondOwner>();
            var seen = new HashSet<string>();

            try
            {
                if (hauler == null)
                    return result;

                foreach (var candidate in hauler.GetCOsSafe(true))
                {
                    var head = StackHead(candidate);
                    if (head == null || head.bDestroyed || head == hauler)
                        continue;

                    if (!IsOwnedByHauler(hauler, head))
                        continue;

                    string rejectReason;
                    if (!Plugin.IsInventoryDeltaHaulCargo(head, out rejectReason))
                        continue;

                    var id = SafeId(head);
                    if (!string.IsNullOrEmpty(id) && !seen.Add(id))
                        continue;

                    result.Add(head);
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGInventoryScan] failed: hauler=" + Plugin.SafeName(hauler) + " error=" + ex.Message);
            }

            return result;
        }

        private static string InventoryCargoKey(CondOwner item)
        {
            var head = StackHead(item);
            var itemDef = SafeItemDef(head);
            if (!string.IsNullOrEmpty(itemDef))
                return "item:" + itemDef;

            var coDef = SafeDef(head);
            if (!string.IsNullOrEmpty(coDef))
                return "co:" + coDef;

            var name = Plugin.SafeName(head);
            return !string.IsNullOrEmpty(name) && name != "<null>" ? "name:" + name : null;
        }

        private static string SafeItemDef(CondOwner co)
        {
            try
            {
                return co != null ? co.strItemDef : null;
            }
            catch
            {
                return null;
            }
        }

        private static int StackCountSafe(CondOwner item)
        {
            try
            {
                return item != null && item.StackCount > 0 ? item.StackCount : 1;
            }
            catch
            {
                return 1;
            }
        }

        private static void AddCount(Dictionary<string, int> counts, string key, int amount)
        {
            if (counts == null || string.IsNullOrEmpty(key))
                return;

            if (amount < 1)
                amount = 1;

            int existing;
            counts.TryGetValue(key, out existing);
            counts[key] = existing + amount;
        }

        private static int CountFor(Dictionary<string, int> counts, string key)
        {
            if (counts == null || string.IsNullOrEmpty(key))
                return 0;

            int count;
            return counts.TryGetValue(key, out count) ? count : 0;
        }

        private static string CountSummary(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0)
                return "<empty>";

            var parts = counts
                .OrderBy(pair => pair.Key)
                .Take(8)
                .Select(pair => pair.Key + "=" + pair.Value)
                .ToList();

            if (counts.Count > parts.Count)
                parts.Add("+" + (counts.Count - parts.Count) + " more");

            return string.Join(", ", parts.ToArray());
        }

        private static void SafeClearQueue(CondOwner hauler, string reason)
        {
            try
            {
                if (hauler?.aQueue == null || hauler.aQueue.Count == 0)
                    return;

                Plugin.Warn("[BIGQueueClear] reason=" + reason + " hauler=" + Plugin.SafeName(hauler) + " oldQueue=" + Plugin.QueueSummary(hauler) + ".");
                foreach (var interaction in hauler.aQueue.ToList())
                    hauler.ClearInteraction(interaction, true);

                if (hauler.aQueue.Count > 0)
                    hauler.aQueue.Clear();
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGQueueClear] failed: " + ex.Message);
            }
        }

        private static void CompleteSession(CondOwner hauler, BigLooseHaulSession session, string reason)
        {
            ActiveSessions.Remove(session.HaulerId);
            Plugin.ModInfo("[BIGStageDone] hauler=" + Plugin.SafeName(hauler) + " reason=" + reason + " duration=" + (DateTime.Now - session.StartedAt).TotalSeconds.ToString("0.0") + "s.");
        }

        private static int ScoreDrop(CondOwner hauler, Vector3 pos, int baseScore)
        {
            if (hauler?.tf == null)
                return baseScore;

            return baseScore + Mathf.RoundToInt((hauler.tf.position - pos).sqrMagnitude);
        }

        private static int ScoreDropAnchor(BigLooseHaulSession session, Vector3 pos)
        {
            if (session == null || session.DropAnchorShip == null || session.DropAnchorZone == null)
                return 1000;

            return ScoreSpiralFromAnchor(session.DropAnchorPosition, pos);
        }

        private static int ScoreDropSpiral(CondOwner hauler, Vector3 anchor, Vector3 pos, int baseScore)
        {
            var score = baseScore + ScoreSpiralFromAnchor(anchor, pos);
            if (hauler?.tf != null)
                score += Mathf.RoundToInt((hauler.tf.position - pos).sqrMagnitude);

            return score;
        }

        private static int ScoreSpiralFromAnchor(Vector3 anchor, Vector3 pos)
        {
            var dx = Mathf.RoundToInt(pos.x - anchor.x);
            var dy = Mathf.RoundToInt(pos.y - anchor.y);
            var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (ring == 0)
                return 0;

            var side = ring * 2;
            int ordinal;
            if (dy == -ring)
                ordinal = dx + ring;
            else if (dx == ring)
                ordinal = side + dy + ring;
            else if (dy == ring)
                ordinal = side * 2 + ring - dx;
            else
                ordinal = side * 3 + ring - dy;

            return ring * 10000 + ordinal;
        }

        private static float PickupSortScore(CondOwner hauler, CondOwner item, Interaction pickup)
        {
            if (hauler == null || item == null)
                return float.MaxValue;

            var score = IsSameShip(hauler, item) ? 0f : OffShipPickupPenalty;

            float pathCost;
            if (TryGetPickupPathCost(hauler, item, pickup, out pathCost))
                return score + pathCost;

            return float.MaxValue;
        }

        private static bool TryGetPickupPathCost(CondOwner hauler, CondOwner item, Interaction pickup, out float cost)
        {
            cost = float.MaxValue;

            if (hauler == null || item == null || hauler.ship == null)
                return false;

            var pathfinder = hauler.Pathfinder;
            if (pathfinder == null)
                return false;

            try
            {
                var targetPos = item.GetPos("use");
                var targetTile = hauler.ship.GetTileAtWorldCoords1(targetPos.x, targetPos.y, true);

                if (targetTile == null)
                    return false;

                var currentTile = pathfinder.tilCurrent;
                if (currentTile == null && hauler.tf != null)
                    currentTile = hauler.ship.GetTileAtWorldCoords1(hauler.tf.position.x, hauler.tf.position.y, true);

                if (currentTile != null && currentTile == targetTile)
                {
                    cost = 0f;
                    return true;
                }

                pathfinder.ResetMemory();
                var range = pickup == null ? 1f : Math.Max(0f, pickup.fTargetPointRange);
                var allowAirlocks = hauler.HasAirlockPermission(pickup != null && pickup.bManual);
                var result = pathfinder.SetGoal2(targetTile, range, item, 0f, 0f, allowAirlocks);
                if (result != null && result.HasPath && result.PathLength >= 0f)
                {
                    cost = result.PathLength;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGPickupPath] failed: hauler=" + Plugin.SafeName(hauler) + " item=" + Plugin.Describe(item) + " error=" + ex.Message);
            }
            finally
            {
                try
                {
                    pathfinder.ResetMemory();
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool IsSameShip(CondOwner hauler, CondOwner item)
        {
            try
            {
                return hauler != null
                    && item != null
                    && hauler.ship != null
                    && item.ship != null
                    && string.Equals(hauler.ship.strRegID, item.ship.strRegID, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureDropAnchor(BigLooseHaulSession session, DropPlan plan)
        {
            if (session == null || plan == null || plan.TargetShip == null || plan.Zone == null)
                return;

            if (session.DropAnchorShip != null && session.DropAnchorZone != null)
                return;

            Vector3 anchor;
            if (!TryGetZoneSpiralAnchor(plan.TargetShip, plan.Zone, out anchor))
                anchor = plan.DropPosition;

            session.DropAnchorShip = plan.TargetShip;
            session.DropAnchorZone = plan.Zone;
            session.DropAnchorPosition = anchor;
            Plugin.ModInfo("[BIGDropPhase] hauler=" + session.HaulerName
                + " state=anchor ship=" + plan.TargetShip.strRegID
                + " zone=" + plan.Zone.strName
                + " anchor=(" + anchor.x.ToString("0.00") + "," + anchor.y.ToString("0.00") + ")"
                + " first=(" + plan.DropPosition.x.ToString("0.00") + "," + plan.DropPosition.y.ToString("0.00") + ").");
        }

        private static CondOwner StackHead(CondOwner item)
        {
            return item != null && item.coStackHead != null ? item.coStackHead : item;
        }

        private static string SafeDef(CondOwner co)
        {
            try
            {
                return co != null ? co.strCODef : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsSame(List<CondOwner> list, CondOwner item)
        {
            var id = SafeId(item);
            return !string.IsNullOrEmpty(id) && list.Any(existing => SafeId(existing) == id);
        }

        private static bool SameItem(CondOwner left, CondOwner right)
        {
            var leftId = SafeId(left);
            var rightId = SafeId(right);
            return !string.IsNullOrEmpty(leftId) && leftId == rightId;
        }

        private static void RemoveSame(List<CondOwner> list, CondOwner item)
        {
            var id = SafeId(item);
            if (string.IsNullOrEmpty(id))
                return;

            list.RemoveAll(existing => SafeId(existing) == id);
        }

        private static void CountFailedAttempt(BigLooseHaulSession session, CondOwner item)
        {
            var id = SafeId(item);
            if (string.IsNullOrEmpty(id))
                return;

            int count;
            session.FailedPickupAttempts.TryGetValue(id, out count);
            session.FailedPickupAttempts[id] = count + 1;
        }

        private static int GetFailedAttempts(BigLooseHaulSession session, CondOwner item)
        {
            var id = SafeId(item);
            if (string.IsNullOrEmpty(id))
                return 0;

            int count;
            return session.FailedPickupAttempts.TryGetValue(id, out count) ? count : 0;
        }

        private static string SafeId(CondOwner co)
        {
            try
            {
                return co?.strID;
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class BigLooseHaulSession
    {
        internal string HaulerId;
        internal string HaulerName;
        internal BigHaulPaintMode Mode = BigHaulPaintMode.Haul;
        internal List<CondOwner> Pending = new List<CondOwner>();
        internal List<CondOwner> Backlog = new List<CondOwner>();
        internal List<CondOwner> Carried = new List<CondOwner>();
        internal List<CondOwner> HandHelperCandidates = new List<CondOwner>();
        internal List<CondOwner> DragHelperCandidates = new List<CondOwner>();
        internal CondOwner CurrentPickup;
        internal CondOwner CurrentHelperAcquire;
        internal BigHaulHelperSlot CurrentHelperSlot = BigHaulHelperSlot.None;
        internal DropPlan CurrentDrop;
        internal DateTime StartedAt;
        internal bool AutoPausedLogged;
        internal bool DropPhase;
        internal Ship DropAnchorShip;
        internal JsonZone DropAnchorZone;
        internal Vector3 DropAnchorPosition;
        internal Dictionary<string, int> FailedPickupAttempts = new Dictionary<string, int>();
        internal bool InventoryBaselineCaptured;
        internal Dictionary<string, int> BaselineInventoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        internal HashSet<string> BaselineInventoryIds = new HashSet<string>(StringComparer.Ordinal);
        internal HashSet<string> SessionCargoKeys = new HashSet<string>(StringComparer.Ordinal);
        internal HashSet<string> BaselineMergeWarnings = new HashSet<string>(StringComparer.Ordinal);
        internal bool PreExistingHelpersCaptured;
        internal HashSet<string> PreExistingHelperIds = new HashSet<string>(StringComparer.Ordinal);
        internal HashSet<string> SessionOwnedHelperIds = new HashSet<string>(StringComparer.Ordinal);
        internal HashSet<string> HelperAcquireFailedIds = new HashSet<string>(StringComparer.Ordinal);
        internal HashSet<string> FailedDropIds = new HashSet<string>(StringComparer.Ordinal);
        internal Dictionary<string, int> DropSearchAttempts = new Dictionary<string, int>(StringComparer.Ordinal);
        internal Dictionary<string, DateTime> NextDropSearchAtUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    }

    internal sealed class DropPlan
    {
        internal CondOwner Item;
        internal Ship TargetShip;
        internal JsonZone Zone;
        internal CondOwner StackTarget;
        internal CondOwner WalkTarget;
        internal Vector3 DropPosition;
        internal int Score;
        internal string Summary;
        internal bool HelperRelease;
        internal BigHaulHelperSlot HelperSlot = BigHaulHelperSlot.None;
    }

    internal sealed class SessionHelperCandidate
    {
        internal CondOwner Item;
        internal BigHaulHelperSlot Slot;
        internal float Score;
    }

    internal static class BigHaulRegistry
    {
        private static readonly Dictionary<string, BigHaulRecord> Records = new Dictionary<string, BigHaulRecord>();

        internal static BigHaulRecord Register(CondOwner hauler, CondOwner item, BigHaulPaintMode mode)
        {
            var itemId = item.strID ?? Guid.NewGuid().ToString("N");
            BigHaulRecord record;

            if (!Records.TryGetValue(itemId, out record))
            {
                record = new BigHaulRecord();
                Records[itemId] = record;
            }

            record.ItemId = itemId;
            record.ItemName = Plugin.SafeName(item);
            record.ItemDef = Safe(() => item.strItemDef);
            record.ShipId = Safe(() => item.ship != null ? item.ship.strRegID : null);
            record.Tile = Plugin.SafeTile(item);
            record.Mode = mode.ToString();
            record.State = "Pending";
            record.ClaimedBy = null;
            record.RegisteredBy = Plugin.SafeName(hauler);
            record.RegisteredAt = DateTime.Now;

            return record;
        }

        internal static void EnsureIcon(BigHaulRecord record, CondOwner item)
        {
            if (record == null || item == null || item.gameObject == null)
                return;

            try
            {
                var workManager = CrewSim.objInstance?.workManager;
                if (workManager == null)
                    return;

                GameObject sign;
                if (record.Icon != null)
                    sign = workManager.AttachSignToObject(record.Icon, item.gameObject, Plugin.BigHaulMarkerIcon);
                else
                    sign = workManager.ActivateSignFromPool(item.gameObject, Plugin.BigHaulMarkerIcon);

                if (sign == null)
                    return;

                var renderer = sign.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.enabled = true;

                record.Icon = sign;
                record.IconAttached = true;
                Plugin.ModInfo("BIG icon attached: " + record);
            }
            catch (Exception ex)
            {
                Plugin.Warn("BIG icon attach failed: " + record + " error=" + ex.Message);
            }
        }

        internal static void RefreshOwnedIcons()
        {
            if (Records.Count == 0)
                return;

            foreach (var record in Records.Values.ToList())
            {
                if (record == null || string.IsNullOrEmpty(record.ItemId) || record.State != "Pending")
                    continue;

                try
                {
                    CondOwner item;
                    if (DataHandler.mapCOs != null && DataHandler.mapCOs.TryGetValue(record.ItemId, out item) && item != null)
                    {
                        if (HasCond(item, "IsCarried"))
                        {
                            record.State = "PickedUp";
                            ClearIcon(record, "picked-up");
                            continue;
                        }

                        if (record.Icon == null || !IsIconVisible(record.Icon))
                            EnsureIcon(record, item);

                        continue;
                    }

                    ClearIcon(record, "target-not-loaded");
                }
                catch (Exception ex)
                {
                    Plugin.Warn("BIG icon refresh failed: " + record + " error=" + ex.Message);
                }
            }
        }

        internal static void MarkState(CondOwner item, string state, string reason)
        {
            try
            {
                if (item == null)
                    return;

                BigHaulRecord record;
                if (!Records.TryGetValue(item.strID, out record))
                    return;

                record.State = state ?? record.State;
                if (state != "Pending")
                    ClearIcon(record, reason ?? state);
            }
            catch (Exception ex)
            {
                Plugin.Warn("BIG state mark failed: item=" + Plugin.Describe(item) + " state=" + state + " error=" + ex.Message);
            }
        }

        internal static bool CancelById(string itemId, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(itemId))
                    return false;

                BigHaulRecord record;
                if (!Records.TryGetValue(itemId, out record) || record == null)
                    return false;

                if (record.State == "Cancelled")
                    return false;

                record.State = "Cancelled";
                ClearIcon(record, reason ?? "cancelled");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Warn("BIG cancel by id failed: itemId=" + itemId + " error=" + ex.Message);
                return false;
            }
        }

        internal static bool IsTracked(CondOwner item)
        {
            try
            {
                if (item == null || string.IsNullOrEmpty(item.strID))
                    return false;

                BigHaulRecord record;
                return Records.TryGetValue(item.strID, out record) && record != null && (record.State == "Pending" || record.State == "PickedUp");
            }
            catch
            {
                return false;
            }
        }

        private static bool IsIconVisible(GameObject icon)
        {
            try
            {
                var renderer = icon != null ? icon.GetComponent<MeshRenderer>() : null;
                return renderer != null && renderer.enabled;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasCond(CondOwner co, string cond)
        {
            try
            {
                return co != null && co.HasCond(cond);
            }
            catch
            {
                return false;
            }
        }

        private static void ClearIcon(BigHaulRecord record, string reason)
        {
            if (record == null || record.Icon == null)
                return;

            try
            {
                var sign = record.Icon;
                var workManager = CrewSim.objInstance?.workManager;
                if (workManager != null)
                    sign.transform.SetParent(workManager.transform, false);

                var renderer = sign.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.enabled = false;

                record.Icon = null;
                record.IconAttached = false;
                Plugin.ModInfo("BIG icon cleared: reason=" + reason + " " + record);
            }
            catch (Exception ex)
            {
                Plugin.Warn("BIG icon clear failed: reason=" + reason + " " + record + " error=" + ex.Message);
            }
        }

        private static string Safe(Func<string> getter)
        {
            try
            {
                return getter() ?? "<null>";
            }
            catch
            {
                return "<error>";
            }
        }
    }

    internal sealed class BigHaulRecord
    {
        internal string ItemId;
        internal string ItemName;
        internal string ItemDef;
        internal string ShipId;
        internal string Tile;
        internal string Mode;
        internal string State;
        internal string ClaimedBy;
        internal string RegisteredBy;
        internal DateTime RegisteredAt;
        internal GameObject Icon;
        internal bool IconAttached;

        public override string ToString()
        {
            return "item=" + ItemName
                + " id=" + ItemId
                + " def=" + ItemDef
                + " ship=" + ShipId
                + " tile=" + Tile
                + " mode=" + Mode
                + " state=" + State
                + " by=" + RegisteredBy
                + " icon=" + IconAttached
                + " at=" + RegisteredAt.ToString("HH:mm:ss");
        }
    }

    internal static class BigLog
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static bool _ready;

        internal static void Init(string version)
        {
            lock (Sync)
            {
                try
                {
                    var root = Paths.BepInExRootPath;
                    var dir = Path.Combine(root, "BIGSupportLogs");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, "BIG-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                    _ready = true;
                    WriteRaw("=== BIG Better Inventory Game " + version + " started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                    WriteRaw("Plugin path: " + typeof(Plugin).Assembly.Location);
                    WriteRaw("");
                }
                catch
                {
                    _ready = false;
                }
            }
        }

        internal static void Write(string level, string message)
        {
            lock (Sync)
            {
                if (!_ready)
                    return;

                WriteRaw(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + (message ?? ""));
            }
        }

        internal static void Close()
        {
            lock (Sync)
            {
                if (!_ready)
                    return;

                WriteRaw("");
                WriteRaw("=== ended " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                _ready = false;
            }
        }

        private static void WriteRaw(string line)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // BIG logging must never affect the game.
            }
        }
    }

    [HarmonyPatch(typeof(DataHandler), "Init")]
    internal static class DataHandlerInitPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Plugin.InstallCustomInteraction();
        }
    }

    [HarmonyPatch(typeof(CondOwner), "CheckInteractionFlag")]
    internal static class CheckInteractionFlagPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CondOwner __instance)
        {
            if (!Plugin.IsDataReady())
                return;

            Plugin.TryAddInteractionToCandidate(__instance);
        }
    }

    [HarmonyPatch(typeof(CondOwner), nameof(CondOwner.QueueInteraction))]
    internal static class QueueInteractionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(CondOwner __instance, CondOwner objTarget, Interaction objInteraction, ref bool __result)
        {
            if (objInteraction == null || objInteraction.strName != Plugin.BigHaulInteraction)
                return true;

            try
            {
                var accepted = Plugin.TryRegisterLooseItem(__instance, objTarget, out var message);
                if (accepted)
                {
                    Plugin.ModInfo(message);
                    BigHaulPlanner.StartLooseSession(__instance, new[] { objTarget });
                }
                else
                {
                    Plugin.Warn(message);
                }

                __result = accepted;
            }
            catch (Exception ex)
            {
                Plugin.Error("BIG Haul Items registration failed: " + ex);
                __result = false;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(CondOwner), nameof(CondOwner.EndTurn))]
    internal static class CondOwnerEndTurnPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CondOwner __instance)
        {
            BigHaulPlanner.TryPump(__instance);
        }
    }

    [HarmonyPatch(typeof(CondOwner), nameof(CondOwner.AIIssueOrder))]
    internal static class CondOwnerAIIssueOrderPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CondOwner __instance, Interaction objInt, bool bPlayerOrdered, Tile til)
        {
            if (!bPlayerOrdered)
                return;

            var order = objInt != null ? objInt.strName : (til != null ? "Walk" : "<unknown>");
            Plugin.StopBigHaulPainting("player-order:" + order);
            BigHaulPlanner.PauseSessionForPlayerOrder(__instance, "player-order:" + order, true);
        }
    }

    [HarmonyPatch(typeof(GUIPDA), "ShowJobPaintUI")]
    internal static class GUIPDAShowJobPaintUIPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GUIPDA __instance, string btn)
        {
            if (btn == "actions")
            {
                Plugin.AddBigHaulPanelButton(__instance);
                return;
            }

            var reason = "vanilla-paint-ui:" + (btn ?? "<null>");
            Plugin.StopBigHaulPainting(reason);
            BigHaulPlanner.CancelAllForHauler(CrewSim.GetSelectedCrew(), reason, true);
        }
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.StartPaintingJob))]
    internal static class CrewSimStartPaintingJobPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CrewSim __instance, JsonInstallable ji)
        {
            try
            {
                if (!Plugin.IsVanillaHaulPaintJob(ji))
                    return;

                var hauler = CrewSim.GetSelectedCrew();
                Plugin.StopBigHaulPainting("vanilla-haul-start");
                BigHaulPlanner.CancelAllForHauler(hauler, "vanilla-haul-start", true);
                Plugin.ModInfo("[BIGVanillaHaulStart] hauler=" + Plugin.SafeName(hauler)
                    + " job=" + Plugin.InstallableSummary(ji) + ".");
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGVanillaHaulStart] failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.FinishPaintingJob))]
    internal static class FinishPaintingJobPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Plugin.StopBigHaulPainting();
        }
    }

    [HarmonyPatch(typeof(CrewSim), "PaintBounds")]
    internal static class CrewSimPaintBoundsPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CrewSim __instance, Bounds bnd)
        {
            try
            {
                if (!Plugin.IsVanillaCancelPaintJob(__instance))
                    return;

                var hauler = CrewSim.GetSelectedCrew();
                var cancelled = Plugin.CancelBigItemsInBounds(hauler, bnd, "vanilla-cancel-bounds");
                Plugin.ModInfo("[BIGCancelPaintBounds] job=" + Plugin.InstallableSummary(CrewSim.jiLast) + " cancelled=" + cancelled + ".");
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGCancelPaintBounds] failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(WorkManager), nameof(WorkManager.RemoveTask), new Type[] { typeof(string) })]
    internal static class WorkManagerRemoveTaskByCOIDPatch
    {
        [HarmonyPostfix]
        private static void Postfix(string strCOID)
        {
            try
            {
                if (BigHaulPlanner.CancelPendingItemById(strCOID, "vanilla-cancel-task"))
                {
                    Plugin.Warn("[BIGCancelHook] removed BIG task for coId=" + strCOID + ".");
                    Plugin.TryCancelBigItemsFromCurrentVanillaSelection("vanilla-cancel-selection");
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("[BIGCancelHook] failed: coId=" + strCOID + " error=" + ex.Message);
            }
        }
    }
}
