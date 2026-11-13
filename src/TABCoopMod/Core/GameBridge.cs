using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using ZX;
using ZX.Commands;
using ZX.Components;
using ZX.Entities;
using ZX.GUI;

namespace TABCoopMod.Core
{
    /// <summary>
    /// Strongly-typed bridge to TheyAreBillions.exe and DXVision.dll internals.
    /// Isolates all obfuscated reflection/delegate lookups in one place.
    /// </summary>
    public static class GameBridge
    {
        public static Assembly GameAssembly { get; private set; }
        public static Assembly EngineAssembly { get; private set; }

        // Resolved Obfuscated System Types
        public static Type TypeZXGame { get; private set; }
        public static Type TypeZXSystem { get; private set; }
        public static Type TypeZXSystemGameLevel { get; private set; }
        public static Type TypeZXSystemMainMenu { get; private set; }
        public static Type TypeZXMessageBoxHelper { get; private set; }
        public static Type TypeZXWindowMessage { get; private set; }
        public static Type TypeZXSteamManager { get; private set; }

        // Cached Reflection Handles
        private static MethodInfo _mZXGameCurrent;
        private static MethodInfo _mLevelSysCurrent;
        private static MethodInfo _mMainMenuCurrent;
        private static MethodInfo _mGameStateCurrent;
        private static MethodInfo _mLevelStateCurrent;

        private static MethodInfo _mLocalize;
        private static MethodInfo _mGetSavesDir;
        private static MethodInfo _methodWriteSaveFile;
        private static MethodInfo _methodLoadGameAndEnter;
        private static MethodInfo _methodShowTopBanner;
        private static MethodInfo _methodShowGroundIndicator;
        private static MethodInfo _methodAssignGroupMove;
        private static MethodInfo _methodPlaceBuilding;

        private static MethodInfo _mCmdById;
        private static MethodInfo _mCmdByName;
        private static MethodInfo _mCmdExec;

        private static MethodInfo _mGetEntityCmd;
        private static MethodInfo _mGetEntitySel;
        private static MethodInfo _mGetEntityLife;
        private static MethodInfo _mGetEntityBehaviour;
        private static MethodInfo _mGetEntityMovable;

        private static Type _typeZombie;
        private static PropertyInfo _propZombieAttackCC;
        private static PropertyInfo _propMovableMode;
        private static PropertyInfo _propCBehavBehaviour;
        private static PropertyInfo _propBehavData;
        private static FieldInfo _fieldBDataTargetRef;
        private static PropertyInfo _propTargetRefId;
        private static PropertyInfo _propBDataLastEnemy;
        private static bool _zombieAggroReflectionInit;

        private static MethodInfo _mCurCmd;
        private static MethodInfo _mCancelCur;
        private static MethodInfo _mCancelSlot;

        private static FieldInfo _fieldSelectedList;
        private static MethodInfo _mPrimarySel;
        private static MethodInfo _mUnselectAll;

        private static MethodInfo _methodShowAlert;
        private static MethodInfo _methodShowYesNo;
        private static MethodInfo _methodShowListSelector;
        private static MethodInfo _methodGetPlainTextRenderer;
        private static PropertyInfo _propWindowMsgTitle;
        private static PropertyInfo _propWindowMsgText;

        public static bool IsInitialized { get; private set; }

        public static bool InitializeAndValidate()
        {
            if (IsInitialized) return true;

            try
            {
                GameAssembly = typeof(ZX.Program).Assembly;
                EngineAssembly = typeof(DXGame).Assembly;

                int missingCount = 0;
                void Check(object member, string description)
                {
                    if (member == null)
                    {
                        missingCount++;
                        ModLogger.Error($"[GameBridge] MISSING BINDING: {description}");
                    }
                    else
                    {
                        ModLogger.Info($"[GameBridge] Verified: {description}");
                    }
                }

                // 1. Resolve Obfuscated Core Types
                TypeZXGame = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXGame);
                TypeZXSystem = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystem);
                TypeZXSystemGameLevel = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemGameLevel);
                TypeZXSystemMainMenu = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemMainMenu);
                TypeZXMessageBoxHelper = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXMessageBoxHelper);
                TypeZXWindowMessage = GameAssembly.GetType("ZX.Editor.ZXWindowMessage");
                TypeZXSteamManager = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSteamManager);

                Check(TypeZXGame, $"Type ZXGame ({ObfuscatedSymbols.Type_ZXGame})");
                Check(TypeZXSystemGameLevel, $"Type ZXSystemGameLevel ({ObfuscatedSymbols.Type_ZXSystemGameLevel})");
                Check(TypeZXSystemMainMenu, $"Type ZXSystemMainMenu ({ObfuscatedSymbols.Type_ZXSystemMainMenu})");
                Check(TypeZXMessageBoxHelper, $"Type ZXMessageBoxHelper ({ObfuscatedSymbols.Type_ZXMessageBoxHelper})");
                Check(TypeZXWindowMessage, "Type ZXWindowMessage (ZX.Editor.ZXWindowMessage)");

                // 2. Singleton Getters
                _mZXGameCurrent = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mLevelSysCurrent = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mMainMenuCurrent = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mGameStateCurrent = AccessTools.Method(typeof(ZXGameState), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mLevelStateCurrent = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);

                Check(_mZXGameCurrent, "ZXGame.Current getter");
                Check(_mLevelSysCurrent, "ZXSystemGameLevel.Current getter");
                Check(_mMainMenuCurrent, "ZXSystemMainMenu.Current getter");
                Check(_mGameStateCurrent, "ZXGameState.Current getter");
                Check(_mLevelStateCurrent, "ZXLevelState.Current getter");

                // 3. Save / Load & Localization on ZXGame
                _mLocalize = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_Localize, new[] { typeof(string) });
                _mGetSavesDir = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_GetSavesDirectory, Type.EmptyTypes);
                _methodWriteSaveFile = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_WriteSaveFile, new[] { typeof(string) });
                _methodLoadGameAndEnter = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_LoadGameAndEnter, new[] { typeof(string) });

                Check(_mLocalize, "ZXGame.Localize");
                Check(_mGetSavesDir, "ZXGame.GetSavesDirectory");
                Check(_methodWriteSaveFile, "ZXGame.WriteSaveFile (.zxsav)");
                Check(_methodLoadGameAndEnter, "ZXGame.LoadGameAndEnter (.zxsav)");

                // 4. Level System Methods
                _methodShowTopBanner = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowTopBanner,
                    new[] { typeof(string), typeof(Color), typeof(int) });
                _methodShowGroundIndicator = TypeZXSystemGameLevel?.GetMethod(
                    ObfuscatedSymbols.Method_LevelSys_ShowGroundIndicator,
                    BindingFlags.Public | BindingFlags.Instance);
                _methodAssignGroupMove = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_AssignGroupMove,
                    new[] { typeof(List<DXEntity>), typeof(ZXCommand), typeof(PointF) });

                Check(_methodShowTopBanner, "ZXSystemGameLevel.ShowTopBanner");
                Check(_methodAssignGroupMove, "ZXSystemGameLevel.AssignGroupMove");

                // 5. Command Execution & Lookup
                _mCmdById = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetById, new[] { typeof(int) });
                _mCmdByName = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetByName, new[] { typeof(string) });
                _mCmdExec = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_Execute,
                    new[] { typeof(ZXEntity), typeof(ZXCommandTarget) });
                _methodPlaceBuilding = AccessTools.Method(typeof(Build), ObfuscatedSymbols.Method_Build_PlaceBuilding,
                    new[] { typeof(ZXEntity), typeof(ZXCommandTarget), typeof(Type), typeof(bool), typeof(bool), typeof(DXEntityTemplate) });

                Check(_mCmdById, "ZXCommand.GetById");
                Check(_mCmdByName, "ZXCommand.GetByName");
                Check(_mCmdExec, "ZXCommand.Execute");
                Check(_methodPlaceBuilding, "Build.PlaceBuilding");

                // 6. Entity & Component Helpers
                _mGetEntityCmd = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetCommandable, Type.EmptyTypes);
                _mGetEntitySel = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetSelectable, Type.EmptyTypes);
                _mGetEntityLife = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetLife, Type.EmptyTypes);
                _mGetEntityBehaviour = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetBehaviour, Type.EmptyTypes);
                _mGetEntityMovable = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetMovable, Type.EmptyTypes);
                _typeZombie = GameAssembly.GetType(ObfuscatedSymbols.Type_Zombie);

                Check(_mGetEntityCmd, "ZXEntity.GetCommandable");
                Check(_mGetEntitySel, "ZXEntity.GetSelectable");
                Check(_mGetEntityLife, "ZXEntity.GetLife");
                Check(_mGetEntityBehaviour, "ZXEntity.GetBehaviour");
                Check(_mGetEntityMovable, "ZXEntity.GetMovable");
                Check(_typeZombie, "Type ZX.Entities.Zombie");

                _mCurCmd = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_GetCurrentCmd, Type.EmptyTypes);
                _mCancelCur = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelCurrent, Type.EmptyTypes);
                _mCancelSlot = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelQueueAt, new[] { typeof(int) });

                Check(_mCurCmd, "CCommandable.GetCurrentCommand");
                Check(_mCancelCur, "CCommandable.CancelCurrentCommand");
                Check(_mCancelSlot, "CCommandable.CancelQueueSlot");

                // 7. Selection Helpers
                _fieldSelectedList = AccessTools.Field(typeof(CSelectable), ObfuscatedSymbols.Field_CSelectable_SelectedList);
                _mPrimarySel = AccessTools.Method(typeof(CSelectable), ObfuscatedSymbols.Method_CSelectable_GetPrimary, Type.EmptyTypes);
                _mUnselectAll = AccessTools.Method(typeof(CSelectable), ObfuscatedSymbols.Method_CSelectable_UnselectAll, Type.EmptyTypes);

                Check(_fieldSelectedList, "CSelectable.SelectedList field");
                Check(_mPrimarySel, "CSelectable.GetPrimarySelected");
                Check(_mUnselectAll, "CSelectable.UnselectAll");

                // 8. MessageBox & UI Helpers
                _methodShowAlert = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowAlert,
                    new[] { typeof(string), typeof(string), typeof(Action) });
                _methodShowYesNo = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowYesNo,
                    new[] { typeof(string), typeof(string), typeof(Action), typeof(Action) });
                _methodShowListSelector = TypeZXWindowMessage != null
                    ? AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowListSelector,
                        new[] { TypeZXWindowMessage, typeof(Action<int>), typeof(Action), typeof(string[]) })
                    : null;
                _methodGetPlainTextRenderer = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_GetPlainTextRenderer, Type.EmptyTypes);
                _propWindowMsgTitle = TypeZXWindowMessage != null ? AccessTools.Property(TypeZXWindowMessage, "Title") : null;
                _propWindowMsgText = TypeZXWindowMessage != null ? AccessTools.Property(TypeZXWindowMessage, "Text") : null;

                Check(_methodShowAlert, "ZXMessageBoxHelper.ShowAlert");
                Check(_methodShowYesNo, "ZXMessageBoxHelper.ShowYesNo");
                Check(_methodShowListSelector, "ZXMessageBoxHelper.ShowListSelector");
                Check(_methodGetPlainTextRenderer, "ZXMessageBoxHelper.GetPlainTextRenderer");
                Check(_propWindowMsgTitle, "ZXWindowMessage.Title");
                Check(_propWindowMsgText, "ZXWindowMessage.Text");

                if (missingCount == 0)
                {
                    ModLogger.Info("[GameBridge] ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing).");
                    IsInitialized = true;
                    return true;
                }
                else
                {
                    ModLogger.Error($"[GameBridge] Binding validation failed with {missingCount} missing symbol(s)!");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] Exception during InitializeAndValidate", ex);
                return false;
            }
        }

        // ====================================================================
        // Public Strongly-Typed Wrapper API
        // ====================================================================

        public static DXGame ZXGameInstance => _mZXGameCurrent?.Invoke(null, null) as DXGame;
        public static DXSystem LevelSystemInstance => _mLevelSysCurrent?.Invoke(null, null) as DXSystem;
        public static DXSystem MainMenuInstance => _mMainMenuCurrent?.Invoke(null, null) as DXSystem;
        /// <summary>ZXGameState is internal; expose it as object so callers can reflect safely.</summary>
        public static object CurrentGameStateObject => _mGameStateCurrent?.Invoke(null, null);

        private static readonly Dictionary<string, PropertyInfo> _gameStatePropCache = new Dictionary<string, PropertyInfo>();

        public static object GetGameStateProperty(string name)
        {
            object gs = CurrentGameStateObject;
            if (gs == null) return null;
            if (!_gameStatePropCache.TryGetValue(name, out PropertyInfo prop))
            {
                prop = gs.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                _gameStatePropCache[name] = prop;
            }
            try { return prop?.GetValue(gs); } catch { return null; }
        }

        /// <summary>
        /// ZXLevelState is an internal type; a direct cast fails at runtime with TypeAccessException.
        /// Everything touching it must go through reflection, so it is returned as object.
        /// </summary>
        public static object CurrentLevelState => _mLevelStateCurrent?.Invoke(null, null);
        public static Type LevelStateType => _mLevelStateCurrent?.ReturnType;

        private static readonly Dictionary<string, PropertyInfo> _levelPropCache = new Dictionary<string, PropertyInfo>();

        public static PropertyInfo GetLevelProperty(string name)
        {
            if (_levelPropCache.TryGetValue(name, out var cached)) return cached;
            PropertyInfo prop = LevelStateType?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            _levelPropCache[name] = prop;
            return prop;
        }

        public static int GetLevelInt(string name)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(ls)); } catch { return 0; }
        }

        public static bool SetLevelProperty(string name, object value)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null || !prop.CanWrite) return false;
            try { prop.SetValue(ls, value); return true; } catch { return false; }
        }

        /// <summary>
        /// Reveals the whole map via ZXLevelState.ShowFullMap, which is OR'ed into every visibility
        /// check in ZXSystemMapLayers. Rendering-only — it does not mutate the simulation state.
        /// </summary>
        public static bool SetShowFullMap(bool on) => SetLevelProperty("ShowFullMap", on);

        private static Type _mapLayersType;
        private static FieldInfo _mapLayersCurrentField;
        private static PropertyInfo _fogEnabledProp;

        /// <summary>Toggles ZXSystemMapLayers.FogEnabled (the global fog-of-war switch).</summary>
        public static bool SetFogEnabled(bool on)
        {
            if (_mapLayersType == null)
                _mapLayersType = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            if (_mapLayersType == null) return false;

            if (_mapLayersCurrentField == null)
                _mapLayersCurrentField = _mapLayersType.GetField(ObfuscatedSymbols.Field_MapLayers_Current,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object inst = _mapLayersCurrentField?.GetValue(null);
            if (inst == null) return false;

            if (_fogEnabledProp == null)
                _fogEnabledProp = _mapLayersType.GetProperty("FogEnabled", BindingFlags.Public | BindingFlags.Instance);
            if (_fogEnabledProp == null || !_fogEnabledProp.CanWrite) return false;

            try { _fogEnabledProp.SetValue(inst, on); return true; } catch { return false; }
        }

        public static string Localize(string key) => _mLocalize != null ? (string)_mLocalize.Invoke(null, new object[] { key }) : key;
        public static string GetSavesDirectory() => (string)_mGetSavesDir?.Invoke(null, null);

        public static void WriteSaveFile(string fullFilePath)
        {
            var game = _mZXGameCurrent.Invoke(null, null);
            var ls = CurrentLevelState;
            if (ls != null)
            {
                AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_SaveTimeState)?.Invoke(ls, null);
            }
            _methodWriteSaveFile.Invoke(game, new object[] { fullFilePath });
        }

        public static void LoadSaveFileAndEnter(string fileNameOrPath)
        {
            var game = _mZXGameCurrent.Invoke(null, null);
            _methodLoadGameAndEnter.Invoke(game, new object[] { fileNameOrPath });
        }

        /// <summary>
        /// Checks ZXGame's own save-file validity gate (the private static bool that
        /// LoadGameAndEnter consults at its very top). A false result makes that call return
        /// silently in ~5 ms with no exception and no loading — the exact symptom we chase.
        /// </summary>
        public static bool IsSaveLoadable(string path)
        {
            try
            {
                var m = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_IsSaveLoadable, new[] { typeof(string) });
                if (m == null) return true; // cannot check; let the engine decide
                return (bool)m.Invoke(null, new object[] { path });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] IsSaveLoadable check failed: {ex.Message}");
                return true;
            }
        }

        private static MethodInfo _methodAnimate;
        private static int _kickFadeDiag;
        private static PropertyInfo _propZXGameIsLoading;

        public static bool IsGameLoading()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                if (_propZXGameIsLoading == null)
                    _propZXGameIsLoading = AccessTools.Property(TypeZXGame, "IsLoading");
                return _propZXGameIsLoading != null && (bool)_propZXGameIsLoading.GetValue(game, null);
            }
            catch { return false; }
        }

        /// <summary>
        /// Advances the engine's black loading-fade DXObject by hand. LoadGameAndEnter gates the real
        /// load behind three DXAnimation fades (see ZXGame.#=zXAMaTPI=); if the scene renders the fade
        /// object but never advances its animation (unfocused/background window, no scene frames), the
        /// callback chain never fires and the level never loads. Driving the animation keeps the
        /// engine's own state machine intact instead of skipping callbacks.
        /// </summary>
        public static void KickLoadingFade(double advanceMs = 700.0)
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (!(field?.GetValue(game) is DXObject fade))
                {
                    if (++_kickFadeDiag <= 5) ModLogger.Info("[GameBridge] KickLoadingFade: fade object is null.");
                    return;
                }
                bool inScene = fade.Scene != null;
                bool hasAnim = fade.Animation != null;
                if (++_kickFadeDiag <= 5)
                    ModLogger.Info($"[GameBridge] KickLoadingFade: inScene={inScene} hasAnimation={hasAnim} opacity={fade.Opacity:F2} visible={fade.Visible}.");

                if (!inScene || !hasAnim) return;
                if (_methodAnimate == null)
                    _methodAnimate = AccessTools.Method(typeof(DXObject), "Animate", new[] { typeof(double) });
                _methodAnimate?.Invoke(fade, new object[] { advanceMs });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] KickLoadingFade failed: {ex.Message}");
            }
        }

        public static bool IsMainMenuReadyForInput()
        {
            try
            {
                var menu = MainMenuInstance;
                if (menu == null) return false;
                var field = AccessTools.Field(TypeZXSystemMainMenu, ObfuscatedSymbols.Field_MainMenu_ReadyForInput);
                return field != null && (bool)field.GetValue(menu);
            }
            catch { return false; }
        }

        public static bool IsStartupFadeActive()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_StartupFadeActive);
                return field != null && (bool)field.GetValue(game);
            }
            catch { return false; }
        }

        /// <summary>
        /// ZXGame.#=z9DPDdq9qP9lZ silently returns when its black loading-fade object is already in
        /// the scene (e.g. a main-menu transition still animating), so a programmatic load during a
        /// snapshot arrival becomes a no-op. Also works around vanilla LoadGameAndEnter line 229's
        /// inverted null check (`if (CurrentGameSystem == null) CurrentGameSystem.Enabled = false;`),
        /// which otherwise leaves an active ZXSystemGameLevel updating disposed CShadow scene objects
        /// after Scene.Init(Black) and throws NullReferenceException on in-level reloads.
        /// </summary>
        public static void PrepareForProgrammaticLoad()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null) as DXGame;
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (field?.GetValue(game) is DXObject fade && fade.Scene != null)
                {
                    fade.RemoveFromScene();
                    ModLogger.Info("[GameBridge] Detached stale loading-fade object before programmatic load.");
                }

                var sys = AccessTools.Property(TypeZXGame, "CurrentGameSystem")?.GetValue(game) as DXSystem;
                if (sys != null)
                {
                    bool wasInLevel = TypeZXSystemGameLevel != null && TypeZXSystemGameLevel.IsInstanceOfType(sys);
                    sys.Enabled = false;
                    game.Paused = true;
                    game.PostMethods_OnStartFrame?.Clear();
                    game.PostMethods_OnFinishFrame?.Clear();
                    game.PostMethods_OnSafeLogicAndDrawing?.Clear();
                    if (wasInLevel)
                    {
                        game.ClearEntities();
                        ModLogger.Info("[GameBridge] Disabled active level system and cleared entities before in-level reload.");
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] PrepareForProgrammaticLoad failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears any stuck Wait/Hourglass cursor lock left behind by interrupted async level loads
        /// or programmatic snapshot transitions so hover/click cursor updates resume normally.
        /// </summary>
        public static void ClearWaitCursor()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null) as DXGame;
                if (game == null) return;

                var fWait = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_WaitCursorCount);
                int prevCount = 0;
                if (fWait != null)
                {
                    prevCount = (int)fWait.GetValue(game);
                    fWait.SetValue(game, 0);
                }

                bool wasLocked = game.CursorLocked;
                var mEnd = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_EndWaitCursor, Type.EmptyTypes);
                if (mEnd != null)
                {
                    mEnd.Invoke(game, null);
                }
                else
                {
                    game.CursorLocked = false;
                }

                game.CursorVisible = true;
                if (wasLocked || prevCount > 0)
                    ModLogger.Info($"[GameBridge] Cleared stuck WaitCursor (prevCount={prevCount}, wasLocked={wasLocked}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ClearWaitCursor failed: {ex.Message}");
            }
        }

        public static void ShowTopBanner(string message, Color color, int durationMs = 3000)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                _methodShowTopBanner.Invoke(levelSys, new object[] { message, color, durationMs });
            }
        }

        // NOTE: ZXCommand and ZXCommandTarget are internal types. Never cast to / `is`-check them here —
        // the Desktop CLR throws TypeAccessException on castclass/isinst of an inaccessible type
        // (铁律#3). All command execution paths therefore use object + reflection.
        public static object GetCommandById(int id) => _mCmdById.Invoke(null, new object[] { id });
        public static object GetCommandByName(string typeName) => _mCmdByName.Invoke(null, new object[] { typeName });

        public static void ExecuteCommandOnEntity(object cmd, object executor, object target)
        {
            _mCmdExec.Invoke(cmd, new object[] { executor, target });
        }

        public static void AssignGroupMoveTargets(List<DXEntity> entities, object cmd, PointF destination)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                _methodAssignGroupMove.Invoke(levelSys, new object[] { entities, cmd, destination });
            }
        }

        public static object PlaceBuilding(object builder, object target, Type buildingType, bool isBonus = false, bool instant = false, DXEntityTemplate template = null)
        {
            return _methodPlaceBuilding.Invoke(null, new object[] { builder, target, buildingType, isBonus, instant, template });
        }

        public static CSelectable GetSelectable(this ZXEntity entity) => (CSelectable)_mGetEntitySel.Invoke(entity, null);
        public static CLife GetLife(this ZXEntity entity) => (CLife)_mGetEntityLife.Invoke(entity, null);

        public static object GetCurrentCommandObject(object commandable) => _mCurCmd?.Invoke(commandable, null);
        public static void CancelCurrentOrderObject(object commandable) => _mCancelCur?.Invoke(commandable, null);
        public static void CancelQueueSlotObject(object commandable, int slotIndex) => _mCancelSlot?.Invoke(commandable, new object[] { slotIndex });

        public static DXConcurrentList<CSelectable> GetLocallySelectedList()
        {
            return (DXConcurrentList<CSelectable>)_fieldSelectedList.GetValue(null);
        }

        public static ZXEntity GetPrimarySelectedEntity() => (ZXEntity)_mPrimarySel.Invoke(null, null);
        public static void UnselectAllLocal() => _mUnselectAll.Invoke(null, null);

        public static DXTextRenderer GetPlainTextRenderer()
        {
            return (DXTextRenderer)_methodGetPlainTextRenderer.Invoke(null, null);
        }

        public static object ShowAlertMessageBox(string title, string message, Action onOk = null)
        {
            return _methodShowAlert.Invoke(null, new object[] { title, message, onOk });
        }

        public static object ShowYesNoMessageBox(string title, string message, Action onYes, Action onNo = null)
        {
            return _methodShowYesNo.Invoke(null, new object[] { title, message, onYes, onNo });
        }

        public static object ShowListSelectorMessageBox(
            string title,
            string message,
            string[] options,
            Action<int> onOk,
            Action onCancel = null)
        {
            if (TypeZXWindowMessage == null || _methodShowListSelector == null)
                return ShowAlertMessageBox(title, message, onCancel);

            object msgObj = Activator.CreateInstance(TypeZXWindowMessage);
            _propWindowMsgTitle?.SetValue(msgObj, title, null);
            _propWindowMsgText?.SetValue(msgObj, message, null);
            return _methodShowListSelector.Invoke(null, new object[] { msgObj, onOk, onCancel, options });
        }

        // ====================================================================
        // Deterministic test-harness helpers.
        // NOTE: all game entity/component classes are internal, so these return base/public
        // types (DXEntity) and use reflection internally. Never cast to ZXEntity here.
        // ====================================================================

        private static readonly Dictionary<Type, PropertyInfo> _teamPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _lifePropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _ordersPropCache = new Dictionary<Type, PropertyInfo>();
        private static PropertyInfo _ccProp;

        private static int CompareEntitiesDeterministically(DXEntity a, DXEntity b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            bool aHigh = (a.ID & 0x8000000000000000UL) != 0UL;
            bool bHigh = (b.ID & 0x8000000000000000UL) != 0UL;
            if (aHigh != bHigh) return aHigh ? 1 : -1;

            if (!aHigh)
                return a.ID.CompareTo(b.ID);

            int cmp = a.Cell.X.CompareTo(b.Cell.X);
            if (cmp != 0) return cmp;
            cmp = a.Cell.Y.CompareTo(b.Cell.Y);
            if (cmp != 0) return cmp;
            cmp = a.Position.X.CompareTo(b.Position.X);
            if (cmp != 0) return cmp;
            cmp = a.Position.Y.CompareTo(b.Position.Y);
            if (cmp != 0) return cmp;
            cmp = (a.Template?.ID ?? 0UL).CompareTo(b.Template?.ID ?? 0UL);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
            if (cmp != 0) return cmp;
            return a.ID.CompareTo(b.ID);
        }

        private static int CompareBodiesDeterministically(DXPhysicBody a, DXPhysicBody b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int cmp = CompareEntitiesDeterministically(a.GameEntity, b.GameEntity);
            if (cmp != 0) return cmp;

            cmp = a.PositionComputed.X.CompareTo(b.PositionComputed.X);
            if (cmp != 0) return cmp;
            cmp = a.PositionComputed.Y.CompareTo(b.PositionComputed.Y);
            if (cmp != 0) return cmp;
            return ((int)a.Shape).CompareTo((int)b.Shape);
        }

        public static void SortEntitiesById()
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;

                var entitiesField = AccessTools.Field(typeof(DXGame), "_Entities");
                if (entitiesField?.GetValue(game) is List<DXEntity> list && list.Count > 1)
                {
                    list.Sort(CompareEntitiesDeterministically);
                    var indexField = AccessTools.Field(typeof(DXEntity), "_Index");
                    if (indexField != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i] != null)
                                indexField.SetValue(list[i], i);
                        }
                    }
                    ModLogger.Info($"[GameBridge] Re-ordered {list.Count} entities deterministically.");

                    // ClearEntities() clears HashSets in place without resetting bucket capacities, so an
                    // in-level reload leaves larger _buckets arrays than a fresh MainMenu load and makes
                    // EntitiesOfType<T>() iterate in a different hash-collision order (e.g. SysColonists).
                    // Rebuild _EntitiesPerType and _DicBaseTypesPerEntity from fresh HashSets in sorted order.
                    var perTypeField = AccessTools.Field(typeof(DXGame), "_EntitiesPerType");
                    var baseTypesField = AccessTools.Field(typeof(DXGame), "_DicBaseTypesPerEntity");
                    if (perTypeField?.GetValue(game) is Dictionary<Type, HashSet<DXEntity>> perType &&
                        baseTypesField?.GetValue(null) is Dictionary<Type, List<HashSet<DXEntity>>> baseTypes)
                    {
                        int rebuiltSets = 0;
                        lock (DXGame.SyncLogic)
                        {
                            perType.Clear();
                            baseTypes.Clear();
                            for (int i = 0; i < list.Count; i++)
                            {
                                var e = list[i];
                                if (e == null) continue;
                                Type et = e.GetType();
                                if (!baseTypes.TryGetValue(et, out var setList))
                                {
                                    setList = new List<HashSet<DXEntity>>();
                                    baseTypes.Add(et, setList);
                                    Type t = et;
                                    while (t != null && t != typeof(DXEntity))
                                    {
                                        if (!perType.TryGetValue(t, out var hs))
                                        {
                                            hs = new HashSet<DXEntity>();
                                            perType.Add(t, hs);
                                            rebuiltSets++;
                                        }
                                        setList.Add(hs);
                                        t = t.BaseType;
                                    }
                                }
                                for (int j = 0; j < setList.Count; j++)
                                    setList[j].Add(e);
                            }
                        }
                        ModLogger.Info($"[GameBridge] Rebuilt {rebuiltSets} _EntitiesPerType sets deterministically.");
                    }
                }

                // Flush deferred DXEntity.CreateBody() calls queued during async level load, then sort
                // DXPhysicWorld.Bodies in place WITHOUT calling Clear() — InitLevel also registers ~358
                // static terrain/gate colliders directly via DXPhysicBody.CreateRectangle(userData:null
                // or gate sub-bodies) that CreateBody() does not recreate.
                AccessTools.Method(typeof(DXPhysicWorld), "FinishPhysics")?.Invoke(null, null);
                var bodies = DXPhysicWorld.Bodies;
                if (bodies != null && bodies.Count > 1)
                {
                    bodies.RemoveAll(b => b == null || b.Handle == IntPtr.Zero);
                    bodies.Sort(CompareBodiesDeterministically);
                    var bodyIndexField = AccessTools.Field(typeof(DXPhysicBody), "_Index");
                    if (bodyIndexField != null)
                    {
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            if (bodies[i] != null)
                                bodyIndexField.SetValue(bodies[i], i);
                        }
                    }
                    ModLogger.Info($"[GameBridge] Re-ordered {bodies.Count} physics bodies deterministically (preserved terrain/gate colliders).");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SortEntitiesById failed: {ex.InnerException ?? ex}");
            }
        }

        // ====================================================================
        // Deterministic clock / RNG / fog / camera helpers (P0 & P1 fixes).
        // ====================================================================

        /// <summary>
        /// Re-seats the 30Hz logic clock and the 12Hz physics clock on the current GameTime so both
        /// peers start a run with an identical zero-phase boundary. NEVER set these to 0.0: on a
        /// late-game save (GameTime ≈ 5.6e6 ms) that makes UpdatePhysics run tens of thousands of
        /// steps in one frame and desyncs the two processes.
        /// </summary>
        public static void AlignSimulationClocks()
        {
            var game = DXGame.Current;
            if (game == null) return;
            try
            {
                double gt = game.GameTime;
                DXGame.LastGameTimeIAUpdate = gt;
                DXPhysicWorld.LastGameTimeUpdatePhysics = gt;
                DXPhysicWorld.TimeBodyPosition = gt;
                DXPhysicWorld.TimeLastBodyPosition = gt;
                ModLogger.Info($"[GameBridge] Simulation clocks aligned at GameTime={gt:F1}ms.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] AlignSimulationClocks failed: {ex.Message}");
            }
        }

        /// <summary>Re-seeds DXLevel.Current.Random (used by mayor selection / random events).</summary>
        public static void SeedLevelRandom(int seed)
        {
            try
            {
                var level = DXLevel.Current;
                if (level == null) return;
                level.Random = new DXRandom(seed);
                ModLogger.Info($"[GameBridge] Re-seeded DXLevel.Current.Random (seed={seed}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SeedLevelRandom failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes every entity visible immediately. Beyond flipping ShowFullMap/FogEnabled, this clears
        /// the three ZXSystemMapLayers watch arrays and forces SceneObject.Opacity=1/Visible=true:
        /// CInvisibleWhenNotWatched uses the Point overload that ignores FogEnabled and only fades in
        /// by +0.09 every 4 logic ticks, so without this reveal_map shows an empty map for ~45 ticks.
        /// </summary>
        public static bool RevealAll(bool reveal)
        {
            bool ok = SetShowFullMap(reveal);
            SetFogEnabled(!reveal);

            try
            {
                if (!reveal) return ok;

                object inst = GetMapLayersInstance();
                if (inst != null)
                {
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_VisibleCells);
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_VisibleBackBuf);
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_ExploredCells);
                    SetInstanceBool(inst, "LayerFogUpdated", true);
                    SetInstanceBool(inst, "LayerShadowUpdated", true);
                }

                var game = DXGame.Current;
                if (game?.Entities != null)
                {
                    foreach (var e in game.Entities)
                    {
                        var so = e?.SceneObject;
                        if (so == null) continue;
                        so.Opacity = 1f;
                        so.Visible = true;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] RevealAll failed: {ex.Message}");
            }
            return ok;
        }

        private static object GetMapLayersInstance()
        {
            if (_mapLayersType == null)
                _mapLayersType = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            if (_mapLayersType == null) return null;
            if (_mapLayersCurrentField == null)
                _mapLayersCurrentField = _mapLayersType.GetField(ObfuscatedSymbols.Field_MapLayers_Current,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return _mapLayersCurrentField?.GetValue(null);
        }

        private static void ClearIntArray(object instance, string fieldName)
        {
            var f = _mapLayersType?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(instance) is int[] arr) Array.Clear(arr, 0, arr.Length);
        }

        private static void SetInstanceBool(object instance, string propName, bool value)
        {
            var p = _mapLayersType?.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) p.SetValue(instance, value);
        }

        /// <summary>
        /// Centers the camera on a WORLD point. entity.Position is world-grid; CameraArea is isometric
        /// scene space, so the point must go through DXWorldGrid.ScenePointFromWorldPoint. Also clears
        /// the opening zoom animation flag which otherwise overwrites CameraArea every frame.
        /// </summary>
        public static bool SetCameraWorld(PointF worldCenter, float width = 0f, float height = 0f)
        {
            var scene = DXScene.Current;
            if (scene == null) return false;

            DisableCameraIntroAnimation();

            RectangleF cur = scene.CameraArea;
            if (cur.Width <= 0f || cur.Height <= 0f) return false;
            float aspect = cur.Width / cur.Height;

            float w = width;
            if (w <= 0f && height > 0f) w = height * aspect;
            if (w <= 0f) w = cur.Width;
            if (w <= 0f) return false;

            float maxW = Math.Max(15f, 45f * aspect);
            w = Math.Max(15f, Math.Min(w, maxW));

            var scenePt = DXWorldGrid.ScenePointFromWorldPoint(worldCenter);
            scene.SetCameraCenter(scenePt, w);
            ModLogger.Info($"[GameBridge] Camera world=({worldCenter.X:F1},{worldCenter.Y:F1}) -> scene=({scenePt.X:F1},{scenePt.Y:F1}) width={w:F1}");
            return true;
        }

        public static void DisableCameraIntroAnimation()
        {
            try
            {
                var f = AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_CameraIntroAnim);
                f?.SetValue(null, false);
            }
            catch { }
        }

        /// <summary>
        /// Returns the entity Type produced by a Build / EntityBonusCreation command (both internal,
        /// so the caller passes them as object). Used to capture And replay building placements.
        /// </summary>
        public static Type GetBuildingTypeFromCommand(object command)
        {
            if (command == null) return null;
            Type t = command.GetType();
            var entityTypeProp = t.GetProperty("EntityType", BindingFlags.Public | BindingFlags.Instance);
            if (entityTypeProp != null)
            {
                try { if (entityTypeProp.GetValue(command) is Type et) return et; } catch { }
            }
            var entityProp = t.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            if (entityProp != null)
            {
                try { return (entityProp.GetValue(command) as DXEntity)?.GetType(); } catch { }
            }
            return null;
        }


        public static List<DXEntity> GetPlayerEntities()
        {
            var result = new List<DXEntity>();
            var game = DXGame.Current;
            if (game?.Entities == null) return result;

            foreach (var entity in game.Entities)
            {
                if (entity == null || entity.Disposed) continue;
                if ((entity.ID & 0x8000000000000000UL) != 0) continue;
                if (IsPlayerTeam(entity)) result.Add(entity);
            }
            result.Sort((a, b) => a.ID.CompareTo(b.ID));
            return result;
        }

        public static List<DXEntity> GetPlayerEntitiesByTypeName(string typeNameSubstring, int maxCount = int.MaxValue)
        {
            var result = new List<DXEntity>();
            if (string.IsNullOrEmpty(typeNameSubstring)) return result;

            foreach (var entity in GetPlayerEntities())
            {
                if (entity.GetType().Name.IndexOf(typeNameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(entity);
                    if (result.Count >= maxCount) break;
                }
            }
            return result;
        }

        public static bool IsPlayerEntity(object entity) => IsPlayerTeam(entity);

        private static bool IsPlayerTeam(object entity)
        {
            Type t = entity.GetType();
            if (!_teamPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                _teamPropCache[t] = prop;
            }
            if (prop == null) return false;
            try { return string.Equals(prop.GetValue(entity)?.ToString(), "Player", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static DXEntity GetCommandCenterEntity()
        {
            var ls = CurrentLevelState;
            if (ls == null) return null;
            if (_ccProp == null)
                _ccProp = LevelStateType?.GetProperty("CurrentCommandCenter", BindingFlags.Public | BindingFlags.Instance);
            if (_ccProp == null) return null;
            try { return _ccProp.GetValue(ls) as DXEntity; } catch { return null; }
        }

        public static object GetLifeObject(object entity)
        {
            if (entity == null || _mGetEntityLife == null) return null;
            if (!_mGetEntityLife.DeclaringType.IsInstanceOfType(entity)) return null;
            try { return _mGetEntityLife.Invoke(entity, null); } catch { return null; }
        }

        public static int GetLifeValue(object entity)
        {
            object life = GetLifeObject(entity);
            if (life == null) return 0;
            Type t = life.GetType();
            if (!_lifePropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance);
                _lifePropCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(life)); } catch { return 0; }
        }

        /// <summary>
        /// Computes a deterministic aggro/alert signature for a Zombie entity without touching internal
        /// types at compile time. Returns 0 when idle/roaming so pure micro-physics nudges never trip aggro alerts.
        /// </summary>
        public static long GetZombieAggroSignature(DXEntity entity, out bool isAlerted)
        {
            isAlerted = false;
            if (entity == null || _typeZombie == null || !_typeZombie.IsInstanceOfType(entity))
                return 0L;

            try
            {
                if (!_zombieAggroReflectionInit)
                {
                    _zombieAggroReflectionInit = true;
                    _propZombieAttackCC = _typeZombie.GetProperty("AttackCommandCenter", BindingFlags.Public | BindingFlags.Instance);
                }

                bool attackCC = _propZombieAttackCC != null && (bool)_propZombieAttackCC.GetValue(entity);

                int moveMode = 0;
                if (_mGetEntityMovable != null)
                {
                    object movable = _mGetEntityMovable.Invoke(entity, null);
                    if (movable != null)
                    {
                        if (_propMovableMode == null)
                            _propMovableMode = movable.GetType().GetProperty("MovementMode", BindingFlags.Public | BindingFlags.Instance);
                        if (_propMovableMode != null)
                            moveMode = Convert.ToInt32(_propMovableMode.GetValue(movable));
                    }
                }

                ulong targetId = 0;
                bool hasLastEnemy = false;
                if (_mGetEntityBehaviour != null)
                {
                    object cbehav = _mGetEntityBehaviour.Invoke(entity, null);
                    if (cbehav != null)
                    {
                        if (_propCBehavBehaviour == null)
                            _propCBehavBehaviour = cbehav.GetType().GetProperty("Behaviour", BindingFlags.Public | BindingFlags.Instance);
                        object behav = _propCBehavBehaviour?.GetValue(cbehav);
                        if (behav != null)
                        {
                            if (_propBehavData == null)
                                _propBehavData = behav.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            object bdata = _propBehavData?.GetValue(behav);
                            if (bdata != null)
                            {
                                Type bType = bdata.GetType();
                                if (_fieldBDataTargetRef == null)
                                    _fieldBDataTargetRef = bType.GetField(ObfuscatedSymbols.Field_BData_TargetRef, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (_fieldBDataTargetRef != null)
                                {
                                    object boxedRef = _fieldBDataTargetRef.GetValue(bdata);
                                    if (boxedRef != null)
                                    {
                                        if (_propTargetRefId == null)
                                            _propTargetRefId = boxedRef.GetType().GetProperty("IDEntity", BindingFlags.Public | BindingFlags.Instance);
                                        if (_propTargetRefId != null)
                                            targetId = Convert.ToUInt64(_propTargetRefId.GetValue(boxedRef));
                                    }
                                }

                                if (_propBDataLastEnemy == null)
                                    _propBDataLastEnemy = bType.GetProperty("LastEnemyPosition", BindingFlags.Public | BindingFlags.Instance);
                                if (_propBDataLastEnemy != null)
                                {
                                    object lastEnemyObj = _propBDataLastEnemy.GetValue(bdata);
                                    hasLastEnemy = lastEnemyObj != null;
                                }
                            }
                        }
                    }
                }

                // TMovementMode: Walk=0, WalkFast=1, Run=2, Jump=3
                bool chasingMode = moveMode >= 2;
                if (targetId == 0 && !hasLastEnemy && !chasingMode && !attackCC)
                    return 0L;

                isAlerted = true;
                long sig = (long)entity.ID
                    ^ ((long)targetId * 73856093L)
                    ^ ((long)moveMode * 19349663L)
                    ^ (hasLastEnemy ? 83492791L : 0L)
                    ^ (attackCC ? 2654435761L : 0L);
                return sig != 0L ? sig : 1L;
            }
            catch
            {
                return 0L;
            }
        }

        public static object GetCommandableObject(object entity)
        {
            if (entity == null || _mGetEntityCmd == null) return null;
            if (!_mGetEntityCmd.DeclaringType.IsInstanceOfType(entity)) return null;
            try { return _mGetEntityCmd.Invoke(entity, null); } catch { return null; }
        }

        public static IList GetOrdersQueued(object cmdComp)
        {
            if (cmdComp == null) return null;
            Type t = cmdComp.GetType();
            if (!_ordersPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("OrdersQueued", BindingFlags.Public | BindingFlags.Instance);
                _ordersPropCache[t] = prop;
            }
            if (prop == null) return null;
            try { return prop.GetValue(cmdComp) as IList; } catch { return null; }
        }

        public static int GetOrderCommandId(object order)
        {
            if (order == null) return 0;
            Type t = order.GetType();
            if (!_idCommandCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("IDCommand", BindingFlags.Public | BindingFlags.Instance);
                _idCommandCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(order)); } catch { return 0; }
        }

        private static readonly Dictionary<Type, PropertyInfo> _idCommandCache = new Dictionary<Type, PropertyInfo>();

        public static object GetCommandGeneric(Type commandType)
        {
            var generic = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetGeneric);
            if (generic == null || !generic.IsGenericMethodDefinition) return null;
            return generic.MakeGenericMethod(commandType).Invoke(null, null);
        }

        // ====================================================================
        // Command execution internals (all object + reflection; ZXCommand,
        // ZXCommandTarget, Build, Train, CCommandable and CMovable are internal).
        // ====================================================================

        private static readonly Dictionary<Type, PropertyInfo> _cmdIdPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _cmdableTargetPropCache = new Dictionary<Type, PropertyInfo>();
        private static MethodInfo _mBuildGetForType;
        private static MethodInfo _mTrainGetForType;

        private static Type _typeZXCommand;
        private static Type _typeCMovable;
        private static Type _typeCCommandable;
        private static Type[] _groupRoutedCommandTypes;
        private static readonly Dictionary<Type, PropertyInfo> _cmdIndicatorColorCache = new Dictionary<Type, PropertyInfo>();

        public static Type TypeZXCommand
        {
            get
            {
                if (_typeZXCommand == null) _typeZXCommand = GameAssembly?.GetType("ZX.Commands.ZXCommand");
                return _typeZXCommand;
            }
        }

        public static Type TypeZXEntity => _mCmdExec?.GetParameters()[0].ParameterType;
        public static Type TypeZXCommandTarget => _mCmdExec?.GetParameters()[1].ParameterType;

        public static Type TypeCMovable
        {
            get
            {
                if (_typeCMovable == null) _typeCMovable = GameAssembly?.GetType("ZX.Components.CMovable");
                return _typeCMovable;
            }
        }

        public static Type TypeCCommandable
        {
            get
            {
                if (_typeCCommandable == null) _typeCCommandable = _mGetEntityCmd?.ReturnType ?? GameAssembly?.GetType("ZX.Components.CCommandable");
                return _typeCCommandable;
            }
        }

        public static int GetCommandId(object command)
        {
            if (command == null) return 0;
            Type t = command.GetType();
            if (!_cmdIdPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
                _cmdIdPropCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(command)); } catch { return 0; }
        }

        public static ulong[] GetSelectedPlayerEntityIds()
        {
            var listObj = _fieldSelectedList?.GetValue(null) as IEnumerable;
            if (listObj == null) return Array.Empty<ulong>();

            var ids = new List<ulong>();
            foreach (object item in listObj)
            {
                var comp = item as DXComponent;
                var ent = comp?.Entity;
                if (ent == null || ent.Disposed) continue;
                if (HasComponent(ent, TypeCMovable) && HasComponent(ent, TypeCCommandable) && IsPlayerEntity(ent))
                    ids.Add(ent.ID);
            }
            ids.Sort();
            return ids.ToArray();
        }

        public static bool IsGroupRoutedCommand(object command)
        {
            if (command == null) return false;
            if (_groupRoutedCommandTypes == null && GameAssembly != null)
            {
                _groupRoutedCommandTypes = new[]
                {
                    GameAssembly.GetType("ZX.Commands.Travel"),
                    GameAssembly.GetType("ZX.Commands.Patrol"),
                    GameAssembly.GetType("ZX.Commands.Chase"),
                    GameAssembly.GetType("ZX.Commands.UseItem_Grenade")
                };
            }
            if (_groupRoutedCommandTypes == null) return false;
            for (int i = 0; i < _groupRoutedCommandTypes.Length; i++)
            {
                var t = _groupRoutedCommandTypes[i];
                if (t != null && t.IsInstanceOfType(command)) return true;
            }
            return false;
        }

        public static bool IsBuildOrBonusCommand(object command, out bool isBonus)
        {
            isBonus = false;
            if (command == null) return false;
            string name = command.GetType().Name;
            if (string.Equals(name, "EntityBonusCreation", StringComparison.Ordinal))
            {
                isBonus = true;
                return true;
            }
            return string.Equals(name, "Build", StringComparison.Ordinal);
        }

        public static void ShowGroundIndicatorForCommand(object command, PointF worldPos, float scale = 1f)
        {
            if (command == null || _methodShowGroundIndicator == null) return;
            object levelSys = _mLevelSysCurrent?.Invoke(null, null);
            if (levelSys == null) return;

            Type t = command.GetType();
            if (!_cmdIndicatorColorCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("TargetIndicatorColor", BindingFlags.Public | BindingFlags.Instance);
                _cmdIndicatorColorCache[t] = prop;
            }
            if (prop == null) return;

            try
            {
                object colorVal = prop.GetValue(command);
                if (colorVal != null)
                    _methodShowGroundIndicator.Invoke(levelSys, new[] { worldPos, colorVal, scale });
            }
            catch { }
        }

        /// <summary>
        /// Scan the entity's public component list instead of HasComponent&lt;T&gt;() — instantiating a
        /// generic method with an internal type argument trips the CLR's type-access check.
        /// </summary>
        public static bool HasComponent(DXEntity entity, Type componentType)
        {
            if (entity == null || componentType == null) return false;
            var comps = entity.Components;
            if (comps == null) return false;
            for (int i = 0; i < comps.Count; i++)
            {
                if (IsComponentOfType(comps[i], componentType)) return true;
            }
            return false;
        }

        private static bool IsComponentOfType(object component, Type componentType) => componentType.IsInstanceOfType(component);

        public static object GetCommandableTargetObject(object commandable)
        {
            if (commandable == null) return null;
            Type t = commandable.GetType();
            if (!_cmdableTargetPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
                _cmdableTargetPropCache[t] = prop;
            }
            if (prop == null) return null;
            try { return prop.GetValue(commandable); } catch { return null; }
        }

        public static object GetBuildCommandForType(Type entityType) => GetCreateEntityCommandForType(ref _mBuildGetForType, "ZX.Commands.Build", entityType);
        public static object GetTrainCommandForType(Type entityType) => GetCreateEntityCommandForType(ref _mTrainGetForType, "ZX.Commands.Train", entityType);

        private static object GetCreateEntityCommandForType(ref MethodInfo cache, string typeName, Type entityType)
        {
            if (cache == null)
            {
                Type cmdType = GameAssembly?.GetType(typeName);
                if (cmdType == null) return null;
                cache = AccessTools.Method(cmdType, ObfuscatedSymbols.Method_BuildOrTrain_GetForType, new[] { typeof(Type) });
            }
            return cache?.Invoke(null, new object[] { entityType });
        }

        private static Type _typeZXCommandTargetRef;
        private static PropertyInfo _pSerPosition, _pSerSize, _pSerOrigin, _pSerEntityRef;
        private static Type _typeEntityRefClosed;
        private static PropertyInfo _pEntityRefEntity;
        private static PropertyInfo _pEntityRefId;
        private static FieldInfo _fEntityRefId;

        private static void EnsureCommandTargetMeta()
        {
            if (_typeZXCommandTargetRef != null) return;
            _typeZXCommandTargetRef = TypeZXCommandTarget;
            if (_typeZXCommandTargetRef == null) return;

            const BindingFlags F = BindingFlags.Public | BindingFlags.Instance;
            _pSerPosition = _typeZXCommandTargetRef.GetProperty("SerPosition", F);
            _pSerSize = _typeZXCommandTargetRef.GetProperty("SerSize", F);
            _pSerOrigin = _typeZXCommandTargetRef.GetProperty("SerOrigin", F);
            _pSerEntityRef = _typeZXCommandTargetRef.GetProperty("SerEntityRef", F);

            Type zxEntity = TypeZXEntity;
            if (zxEntity != null)
            {
                _typeEntityRefClosed = typeof(DXEntityRef<>).MakeGenericType(zxEntity);
                _pEntityRefEntity = _typeEntityRefClosed.GetProperty("Entity", F);
                _pEntityRefId = _typeEntityRefClosed.GetProperty("IDEntity", F);
                if (_pEntityRefId == null)
                    _fEntityRefId = _typeEntityRefClosed.GetField("IDEntity", F);
            }
        }

        public static void UnpackCommandTarget(object target, out ulong targetEntityId, out PointF pos, out SizeF size, out PointF origin)
        {
            targetEntityId = 0UL;
            pos = PointF.Empty;
            size = SizeF.Empty;
            origin = PointF.Empty;
            if (target == null) return;

            EnsureCommandTargetMeta();
            if (_typeZXCommandTargetRef == null) return;

            try
            {
                if (_pSerPosition?.GetValue(target) is PointF p) pos = p;
                if (_pSerSize?.GetValue(target) is SizeF s) size = s;
                if (_pSerOrigin?.GetValue(target) is PointF o) origin = o;
                object refObj = _pSerEntityRef?.GetValue(target);
                if (refObj != null)
                {
                    if (_pEntityRefId != null)
                        targetEntityId = Convert.ToUInt64(_pEntityRefId.GetValue(refObj));
                    else if (_fEntityRefId != null)
                        targetEntityId = Convert.ToUInt64(_fEntityRefId.GetValue(refObj));
                    else if (_pEntityRefEntity?.GetValue(refObj) is DXEntity refEnt)
                        targetEntityId = refEnt.ID;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] UnpackCommandTarget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a boxed ZXCommandTarget struct via reflection. Setting properties on the boxed
        /// value mutates the box in place, so the resulting object is a complete target.
        /// </summary>
        public static object CreateCommandTarget(PointF position, SizeF size, PointF origin, ulong targetEntityId)
        {
            EnsureCommandTargetMeta();
            if (_typeZXCommandTargetRef == null) return null;

            object box = Activator.CreateInstance(_typeZXCommandTargetRef);
            _pSerPosition?.SetValue(box, position);
            _pSerSize?.SetValue(box, size);
            _pSerOrigin?.SetValue(box, origin);

            if (targetEntityId != 0UL && _typeEntityRefClosed != null && _pEntityRefEntity != null && _pSerEntityRef != null)
            {
                var ent = DXGame.Current?.GetEntityByID(targetEntityId);
                if (ent != null)
                {
                    object refBox = Activator.CreateInstance(_typeEntityRefClosed);
                    _pEntityRefEntity.SetValue(refBox, ent);
                    _pSerEntityRef.SetValue(box, refBox);
                }
            }
            return box;
        }

        // ====================================================================
        // Mayor selection (ZX.ZXMayor). Identify the chosen mayor by its string ID and apply that
        // exact mayor on every peer — avoids re-running the male/female random pick per machine.
        // ====================================================================
        private static Type _typeZXMayor;
        private static FieldInfo _mayorRegistryField;
        private static PropertyInfo _mayorIdProp, _mayorNameProp, _mayorLevelProp;
        private static MethodInfo _mayorApplyMethod;

        private static void EnsureMayorMeta()
        {
            if (_typeZXMayor != null) return;
            _typeZXMayor = GameAssembly?.GetType("ZX.ZXMayor");
            if (_typeZXMayor == null) return;

            const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags IF = BindingFlags.Public | BindingFlags.Instance;
            _mayorRegistryField = _typeZXMayor.GetField(ObfuscatedSymbols.Field_ZXMayor_Registry, SF);
            _mayorIdProp = _typeZXMayor.GetProperty("ID", IF);
            _mayorNameProp = _typeZXMayor.GetProperty("Name", IF);
            _mayorLevelProp = _typeZXMayor.GetProperty("Level", IF);
            _mayorApplyMethod = AccessTools.Method(_typeZXMayor, ObfuscatedSymbols.Method_ZXMayor_Apply, new[] { LevelStateType });
            if (_mayorApplyMethod == null)
            {
                foreach (var m in AccessTools.GetDeclaredMethods(_typeZXMayor))
                {
                    if (m.Name == ObfuscatedSymbols.Method_ZXMayor_Apply && m.GetParameters().Length == 1)
                    { _mayorApplyMethod = m; break; }
                }
            }
        }

        public static List<Dictionary<string, object>> GetMayorList(int levelFilter)
        {
            EnsureMayorMeta();
            var result = new List<Dictionary<string, object>>();
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null) return result;
            foreach (DictionaryEntry kv in dict)
            {
                object mayor = kv.Value;
                if (mayor == null) continue;
                int lvl = _mayorLevelProp != null ? Convert.ToInt32(_mayorLevelProp.GetValue(mayor)) : 0;
                if (levelFilter > 0 && lvl != levelFilter) continue;
                result.Add(new Dictionary<string, object>
                {
                    ["id"] = _mayorIdProp != null ? Convert.ToString(_mayorIdProp.GetValue(mayor)) : Convert.ToString(kv.Key),
                    ["name"] = _mayorNameProp != null ? Convert.ToString(_mayorNameProp.GetValue(mayor)) : "?",
                    ["level"] = lvl
                });
            }
            return result;
        }

        public static bool ApplyMayor(string mayorId)
        {
            EnsureMayorMeta();
            if (string.IsNullOrEmpty(mayorId) || _mayorApplyMethod == null) return false;
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(mayorId)) return false;
            object mayor = dict[mayorId];
            object ls = CurrentLevelState;
            if (mayor == null || ls == null) return false;
            try { _mayorApplyMethod.Invoke(mayor, new[] { ls }); return true; }
            catch (Exception ex) { ModLogger.Warn($"[GameBridge] ApplyMayor failed: {ex.Message}"); return false; }
        }

        public static int GetLevelCollectionCount(string name)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null) return 0;
            try
            {
                object v = prop.GetValue(ls);
                if (v is ICollection col) return col.Count;
                if (v is IEnumerable en) { int n = 0; foreach (var _ in en) n++; return n; }
            }
            catch { }
            return 0;
        }
    }
}
