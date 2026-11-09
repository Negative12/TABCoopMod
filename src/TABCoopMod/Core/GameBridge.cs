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
        private static MethodInfo _methodAssignGroupMove;
        private static MethodInfo _methodPlaceBuilding;

        private static MethodInfo _mCmdById;
        private static MethodInfo _mCmdByName;
        private static MethodInfo _mCmdExec;

        private static MethodInfo _mGetEntityCmd;
        private static MethodInfo _mGetEntitySel;
        private static MethodInfo _mGetEntityLife;

        private static MethodInfo _mCurCmd;
        private static MethodInfo _mCancelCur;
        private static MethodInfo _mCancelSlot;

        private static FieldInfo _fieldSelectedList;
        private static MethodInfo _mPrimarySel;
        private static MethodInfo _mUnselectAll;

        private static MethodInfo _methodShowAlert;
        private static MethodInfo _methodShowYesNo;
        private static MethodInfo _methodGetPlainTextRenderer;

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
                TypeZXSteamManager = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSteamManager);

                Check(TypeZXGame, $"Type ZXGame ({ObfuscatedSymbols.Type_ZXGame})");
                Check(TypeZXSystemGameLevel, $"Type ZXSystemGameLevel ({ObfuscatedSymbols.Type_ZXSystemGameLevel})");
                Check(TypeZXSystemMainMenu, $"Type ZXSystemMainMenu ({ObfuscatedSymbols.Type_ZXSystemMainMenu})");
                Check(TypeZXMessageBoxHelper, $"Type ZXMessageBoxHelper ({ObfuscatedSymbols.Type_ZXMessageBoxHelper})");

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

                Check(_mGetEntityCmd, "ZXEntity.GetCommandable");
                Check(_mGetEntitySel, "ZXEntity.GetSelectable");
                Check(_mGetEntityLife, "ZXEntity.GetLife");

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
                _methodGetPlainTextRenderer = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_GetPlainTextRenderer, Type.EmptyTypes);

                Check(_methodShowAlert, "ZXMessageBoxHelper.ShowAlert");
                Check(_methodShowYesNo, "ZXMessageBoxHelper.ShowYesNo");
                Check(_methodGetPlainTextRenderer, "ZXMessageBoxHelper.GetPlainTextRenderer");

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
        public static ZXGameState CurrentGameState => (ZXGameState)_mGameStateCurrent?.Invoke(null, null);

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

        public static void ShowTopBanner(string message, Color color, int durationMs = 3000)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                _methodShowTopBanner.Invoke(levelSys, new object[] { message, color, durationMs });
            }
        }

        public static ZXCommand GetCommandById(int id) => (ZXCommand)_mCmdById.Invoke(null, new object[] { id });
        public static ZXCommand GetCommandByName(string typeName) => (ZXCommand)_mCmdByName.Invoke(null, new object[] { typeName });

        public static void ExecuteCommandOnEntity(ZXCommand cmd, ZXEntity executor, ZXCommandTarget target)
        {
            _mCmdExec.Invoke(cmd, new object[] { executor, target });
        }

        public static void AssignGroupMoveTargets(List<DXEntity> entities, ZXCommand cmd, PointF destination)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                _methodAssignGroupMove.Invoke(levelSys, new object[] { entities, cmd, destination });
            }
        }

        public static ZXEntity PlaceBuilding(ZXEntity builder, ZXCommandTarget target, Type buildingType, bool isBonus = false, bool instant = false, DXEntityTemplate template = null)
        {
            return (ZXEntity)_methodPlaceBuilding.Invoke(null, new object[] { builder, target, buildingType, isBonus, instant, template });
        }

        public static CCommandable GetCommandable(this ZXEntity entity) => (CCommandable)_mGetEntityCmd.Invoke(entity, null);
        public static CSelectable GetSelectable(this ZXEntity entity) => (CSelectable)_mGetEntitySel.Invoke(entity, null);
        public static CLife GetLife(this ZXEntity entity) => (CLife)_mGetEntityLife.Invoke(entity, null);

        public static ZXCommand GetCurrentCommand(this CCommandable commandable) => (ZXCommand)_mCurCmd.Invoke(commandable, null);
        public static void CancelCurrentOrder(this CCommandable commandable) => _mCancelCur.Invoke(commandable, null);
        public static void CancelQueuedOrderAt(this CCommandable commandable, int slotIndex) => _mCancelSlot.Invoke(commandable, new object[] { slotIndex });

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

        public static ZXWindow ShowAlertMessageBox(string title, string message, Action onOk = null)
        {
            return (ZXWindow)_methodShowAlert.Invoke(null, new object[] { title, message, onOk });
        }

        public static ZXWindow ShowYesNoMessageBox(string title, string message, Action onYes, Action onNo = null)
        {
            return (ZXWindow)_methodShowYesNo.Invoke(null, new object[] { title, message, onYes, onNo });
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

        public static void SortEntitiesById()
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;
                var field = AccessTools.Field(typeof(DXGame), "_Entities");
                if (field?.GetValue(game) is List<DXEntity> list && list.Count > 1)
                {
                    list.Sort((a, b) => a.ID.CompareTo(b.ID));
                    ModLogger.Info($"[GameBridge] Re-ordered {list.Count} entities by ID for deterministic update order.");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SortEntitiesById failed: {ex.Message}");
            }
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

        public static ZXCommand GetCommandGeneric(Type commandType)
        {
            var generic = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetGeneric);
            if (generic == null || !generic.IsGenericMethodDefinition) return null;
            return generic.MakeGenericMethod(commandType).Invoke(null, null) as ZXCommand;
        }
    }
}
