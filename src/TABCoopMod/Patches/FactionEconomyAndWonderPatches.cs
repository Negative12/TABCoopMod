using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Sync;
using ZX;

namespace TABCoopMod.Patches
{
    internal static class FactionMayorBannerTracker
    {
        public static string LastAppliedMayorName;
        public static byte LastAppliedPlayerId;
    }

    // ========================================================================
    // 1. Per-Faction Context Swaps for Economy & Command Lifecycle
    // ========================================================================

    /// <summary>
    /// Hooks ZXLevelState.NeedsRecalculate (#=zv7139RDgRLf_PO97vA==) cleanly (avoiding
    /// ZXLevelState.Recalculate's C# 'catch when' filter IL) so every dirty recalculation
    /// recomputes all player ledgers and shared Spire energy pool without resetting ZXLevelState to a merged sum.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ZXLevelState_Recalculate_MultiFaction
    {
        private static bool _inRecalcAll;

        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.LevelStateType ?? GameBridge.GameAssembly?.GetType("ZX.ZXLevelState");
            return t != null ? AccessTools.Method(t, "#=zv7139RDgRLf_PO97vA==", Type.EmptyTypes) : null;
        }

        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (!__result || _inRecalcAll || !CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized)
                return;

            if (FactionContextManager.ContextDepth > 0)
                return;

            GameBridge.SetLevelStateNeedsRecalculate(false);
            _inRecalcAll = true;
            try
            {
                FactionContextManager.RecalculateAllFactions();
            }
            finally
            {
                _inRecalcAll = false;
            }
            __result = false;
        }
    }

    /// <summary>
    /// Routes resource generator 8-hour production ticks & CommandCenter 8-hour step (#=zgLWBIL0dUIT9ZcCNSQ==)
    /// into the owning player's independent ledger.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CResourceGenerator_Update_Context
    {
        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_CResourceGenerator);
            return t != null ? AccessTools.Method(t, ObfuscatedSymbols.Method_CResourceGenerator_Update, Type.EmptyTypes) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(DXComponent __instance, out bool __state)
        {
            __state = false;
            if (!CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized || __instance?.Entity == null)
                return;

            byte owner = FactionOwnershipTracker.GetOwner(__instance.Entity.ID);
            if (owner != 0)
            {
                FactionContextManager.PushContext(owner);
                __state = true;
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state) FactionContextManager.PopContext();
        }
    }

    /// <summary>
    /// Ensures Building.RecalcResources (#=z3rO8Jok=) adds the scaled Spire energy supply
    /// ((800 * (2 + N - 1)) / 2 -> 1200 in 2P, 1600 in 3P) directly to TheSpire's cached resources.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Building_RecalcResources_SpireScale
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeBuilding != null
                ? AccessTools.Method(GameBridge.TypeBuilding, ObfuscatedSymbols.Method_Building_RecalcResources, Type.EmptyTypes)
                : null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (!CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized || !(__instance is DXEntity ent))
                return;

            byte owner = FactionOwnershipTracker.GetOwner(ent.ID);
            if (owner != 0)
            {
                FactionContextManager.PushContext(owner);
                __state = true;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __instance == null)
                return;
            if (GameBridge.TypeTheSpire != null && GameBridge.TypeTheSpire.IsInstanceOfType(__instance))
            {
                int scaledEnergy = WonderCrowdfundManager.GetScaledSpireEnergy(800);
                GameBridge.ApplyBuildingEnergyOverride(__instance, scaledEnergy);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state) FactionContextManager.PopContext();
        }
    }

    /// <summary>
    /// Routes PickableItem pickup rewards (#=zmrUbwuk=) to the picking unit's owner ledger.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_PickableItem_Consume_Context
    {
        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_PickableItem);
            Type tHuman = GameBridge.GameAssembly?.GetType("ZX.Entities.Human");
            if (t == null || tHuman == null) return null;
            return AccessTools.Method(t, ObfuscatedSymbols.Method_PickableItem_Pick, new[] { tHuman });
        }

        [HarmonyPrefix]
        public static void Prefix(object __0, out bool __state)
        {
            __state = false;
            if (!CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized)
                return;
            if (__0 is DXEntity picker)
            {
                byte owner = FactionOwnershipTracker.GetOwner(picker.ID);
                if (owner != 0)
                {
                    FactionContextManager.PushContext(owner);
                    __state = true;
                }
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state) FactionContextManager.PopContext();
        }
    }

    /// <summary>
    /// Intercepts GoldPerKill drops arriving on the simulation thread outside any faction context
    /// (ContextDepth == 0) and distributes them deterministically without IL-patching CLife.Update.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ZXLevelState_AddGoldDirect_KillBounty
    {
        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.LevelStateType ?? GameBridge.GameAssembly?.GetType("ZX.ZXLevelState");
            return t != null ? AccessTools.Method(t, ObfuscatedSymbols.Method_LevelState_AddGoldDirect, new[] { typeof(int) }) : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(int __0)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized)
                return true;
            if (LockstepClock.LoadingLevel || FactionContextManager.ContextDepth > 0)
                return true;
            if (!LockstepClock.IsSimulationThreadActive || __0 <= 0)
                return true;

            FactionContextManager.DistributeKillGoldDeterministically(__0);
            return false;
        }
    }

    /// <summary>
    /// Context-swaps queued command advancement & cancellations in CCommandable into the entity owner's ledger.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CCommandable_QueueMethods_Context
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type t = GameBridge.TypeCCommandable;
            if (t == null) yield break;

            var mAdv = AccessTools.Method(t, ObfuscatedSymbols.Method_CCommandable_AdvanceQueue, Type.EmptyTypes);
            if (mAdv != null) yield return mAdv;

            var mCancelCur = AccessTools.Method(t, ObfuscatedSymbols.Method_CCommandable_CancelCurrent, Type.EmptyTypes);
            if (mCancelCur != null) yield return mCancelCur;

            var mCancelAt = AccessTools.Method(t, ObfuscatedSymbols.Method_CCommandable_CancelQueueAt, new[] { typeof(int) });
            if (mCancelAt != null) yield return mCancelAt;

            var mUpdate = AccessTools.Method(t, "#=zzHXxhbjNa0Zf", Type.EmptyTypes);
            if (mUpdate != null) yield return mUpdate;
        }

        [HarmonyPrefix]
        public static void Prefix(DXComponent __instance, out bool __state)
        {
            __state = false;
            if (!CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized || __instance?.Entity == null)
                return;

            byte owner = FactionOwnershipTracker.GetOwner(__instance.Entity.ID);
            if (owner != 0)
            {
                FactionContextManager.PushContext(owner);
                __state = true;
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state) FactionContextManager.PopContext();
        }
    }

    /// <summary>
    /// Context-swaps direct ZXCommand execution / completion / cancellation overrides
    /// (Technology, Upgrade, UndoBuilding, Destroy) into the executor entity owner's ledger,
    /// and forwards Functional Wonder construction cancels into WonderCrowdfundManager.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ZXCommand_Lifecycle_Context
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            if (GameBridge.GameAssembly == null || GameBridge.TypeZXEntity == null) yield break;

            string[] typeNames =
            {
                ObfuscatedSymbols.Type_Technology,
                ObfuscatedSymbols.Type_UndoBuilding,
                ObfuscatedSymbols.Type_Destroy,
                ObfuscatedSymbols.Type_Upgrade
            };

            foreach (string name in typeNames)
            {
                Type t = GameBridge.GameAssembly.GetType(name);
                if (t == null) continue;

                var mExec = GameBridge.TypeZXCommandTarget != null
                    ? AccessTools.Method(t, ObfuscatedSymbols.Method_Command_ExecuteImpl, new[] { GameBridge.TypeZXEntity, GameBridge.TypeZXCommandTarget })
                    : null;
                if (mExec != null && mExec.DeclaringType == t) yield return mExec;

                var mComp = AccessTools.Method(t, ObfuscatedSymbols.Method_Command_OnComplete, new[] { GameBridge.TypeZXEntity });
                if (mComp != null && mComp.DeclaringType == t) yield return mComp;

                var mRef = AccessTools.Method(t, ObfuscatedSymbols.Method_ZXCommand_CancelRefund, new[] { GameBridge.TypeZXEntity });
                if (mRef != null && mRef.DeclaringType == t) yield return mRef;
            }

            if (GameBridge.TypeZXCommand != null)
            {
                var mCancel = AccessTools.Method(GameBridge.TypeZXCommand, ObfuscatedSymbols.Method_ZXCommand_CancelProgress, new[] { GameBridge.TypeZXEntity, typeof(bool) });
                if (mCancel != null) yield return mCancel;
            }
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, object __0, MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            if (!CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized)
                return;

            if (__0 is DXEntity exec)
            {
                // Intercept Functional Wonder construction cancellation BEFORE building disappears
                if (__originalMethod != null && __originalMethod.Name == ObfuscatedSymbols.Method_Command_ExecuteImpl)
                {
                    string cmdTypeName = __instance?.GetType().FullName;
                    bool isUndo = cmdTypeName == ObfuscatedSymbols.Type_UndoBuilding;
                    bool isDestroy = cmdTypeName == ObfuscatedSymbols.Type_Destroy;
                    if ((isUndo || isDestroy) && WonderCrowdfundManager.TryClassifyEntityType(exec.GetType(), out var wKind))
                    {
                        var st = WonderCrowdfundManager.GetState(wKind);
                        if (st != null && st.BuildingEntityId == exec.ID)
                        {
                            if (GameBridge.IsBuildingBeingBuilt(exec))
                            {
                                WonderCrowdfundManager.HandleConstructionCanceled(wKind, isUndo ? 100 : 50);
                            }
                            else
                            {
                                // Completed Functional Wonder manually destroyed -> re-open BuildFunding
                                WonderCrowdfundManager.HandleWonderDestroyed(wKind, exec.ID);
                            }
                        }
                    }
                }

                byte owner = FactionOwnershipTracker.GetOwner(exec.ID);
                if (owner != 0)
                {
                    FactionContextManager.PushContext(owner);
                    __state = true;
                }
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (__state) FactionContextManager.PopContext();
        }
    }

    // ========================================================================
    // 2. Functional & Output Wonder Isolation, Voucher Cost Zeroing & Completion
    // ========================================================================

    /// <summary>
    /// Isolates MaxInstances > 0 buildings/Wonders:
    /// - Functional Wonders (Telescope/Academy/Spire): strictly 1 globally across team.
    /// - Output Wonders (CrystalPalace/Transmutator/Victorious) & other MaxInstances>0: 1 per player.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Build_AllInstancesAlreadyBuilt_MultiFaction
    {
        private static PropertyInfo _pBuildEntity;

        public static MethodBase TargetMethod()
        {
            Type tBuild = GameBridge.GameAssembly?.GetType("ZX.Commands.Build");
            return tBuild != null ? AccessTools.PropertyGetter(tBuild, "AllInstancesAlreadyBuilt") : null;
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, ref bool __result)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !FactionContextManager.IsInitialized || __instance == null)
                return;

            if (_pBuildEntity == null)
                _pBuildEntity = __instance.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            var tmplEnt = _pBuildEntity?.GetValue(__instance) as DXEntity;
            if (tmplEnt == null) return;

            Type entType = tmplEnt.GetType();
            if (WonderCrowdfundManager.TryClassifyEntityType(entType, out var fKind))
            {
                __result = WonderCrowdfundManager.IsWonderBlockedGlobally(fKind);
                return;
            }

            object dp = GameBridge.GetEntityDefaultParamsBoxed(tmplEnt);
            if (dp == null) return;
            GameBridge.ReadDefaultParamsStats(dp, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out int maxInstances, out _);
            if (maxInstances <= 0)
            {
                __result = false;
                return;
            }

            byte activeOwner = FactionContextManager.CurrentActiveOwner;
            if (activeOwner == 0) activeOwner = FactionOwnershipTracker.LocalPlayerId;
            if (activeOwner == 0) activeOwner = 1;

            var game = DXGame.Current;
            if (game == null) return;

            int ownedCount = 0;
            foreach (var ent in game.EntitiesOfType(entType))
            {
                if (ent == null || ent.Disposed) continue;
                if (FactionOwnershipTracker.GetOwner(ent.ID) == activeOwner)
                    ownedCount++;
            }
            __result = ownedCount >= maxInstances;
        }
    }

    /// <summary>
    /// Temporarily zeroes Gold/Wood/Stone/Iron/Oil on the Functional Wonder entity template while
    /// Build.#=zcPu1$70ruiRE(ZXEntity) evaluates a ReadyVoucher, keeping ValueType return untouched in CLR 4.6.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Build_VoucherCost
    {
        private static PropertyInfo _pEntityTypeProp;

        public struct CostSwapState
        {
            public bool Swapped;
            public object Dp;
            public int G, W, S, I, O;
        }

        public static MethodBase TargetMethod()
        {
            Type tBuild = GameBridge.GameAssembly?.GetType("ZX.Commands.Build");
            if (tBuild == null || GameBridge.TypeZXEntity == null) return null;
            return AccessTools.Method(tBuild, ObfuscatedSymbols.Method_ZXCommand_GetCost, new[] { GameBridge.TypeZXEntity });
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, out CostSwapState __state)
        {
            __state = default;
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized || __instance == null)
                return;

            if (_pEntityTypeProp == null)
                _pEntityTypeProp = __instance.GetType().GetProperty("EntityType", BindingFlags.Public | BindingFlags.Instance);
            Type entType = _pEntityTypeProp?.GetValue(__instance) as Type;
            if (!WonderCrowdfundManager.TryClassifyEntityType(entType, out var fKind)
                || !WonderCrowdfundManager.CanPlaceBuildVoucher(fKind))
                return;

            var tmpl = DXGame.Current?.GetEntityFromType(entType) as DXEntity;
            object dp = tmpl != null ? GameBridge.GetEntityDefaultParamsBoxed(tmpl) : null;
            if (dp != null && GameBridge.PushZeroCostOnDefaultParams(dp, out int g, out int w, out int s, out int i, out int o))
            {
                __state = new CostSwapState { Swapped = true, Dp = dp, G = g, W = w, S = s, I = i, O = o };
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(CostSwapState __state)
        {
            if (__state.Swapped)
                GameBridge.PopZeroCostOnDefaultParams(__state.Dp, __state.G, __state.W, __state.S, __state.I, __state.O);
        }
    }

    [HarmonyPatch]
    public static class Patch_Build_IsVisible_Wonder
    {
        private static PropertyInfo _pEntityTypeProp;

        public static MethodBase TargetMethod()
        {
            Type tBuild = GameBridge.GameAssembly?.GetType("ZX.Commands.Build");
            if (tBuild == null || GameBridge.TypeZXEntity == null) return null;
            return AccessTools.Method(tBuild, ObfuscatedSymbols.Method_ZXCommand_IsVisible, new[] { GameBridge.TypeZXEntity });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance, object __0, ref bool __result)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized || __instance == null)
                return true;

            if (_pEntityTypeProp == null)
                _pEntityTypeProp = __instance.GetType().GetProperty("EntityType", BindingFlags.Public | BindingFlags.Instance);
            Type entType = _pEntityTypeProp?.GetValue(__instance) as Type;
            if (!WonderCrowdfundManager.TryClassifyEntityType(entType, out var fKind))
                return true;

            if (!WonderCrowdfundManager.CanPlaceBuildVoucher(fKind))
                return true;

            if (WonderCrowdfundManager.IsWonderBlockedGlobally(fKind))
            {
                __result = false;
                return false;
            }
            if (fKind == FunctionalWonderType.Telescope && __0 != null && GameBridge.GetCommandCenterTelescopeEnabled(__0))
            {
                __result = false;
                return false;
            }

            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_Build_CanExecute_Wonder
    {
        private static PropertyInfo _pEntityTypeProp;

        public static MethodBase TargetMethod()
        {
            Type tBuild = GameBridge.GameAssembly?.GetType("ZX.Commands.Build");
            if (tBuild == null || GameBridge.TypeZXEntity == null) return null;
            return AccessTools.Method(tBuild, ObfuscatedSymbols.Method_ZXCommand_CanExecute, new[] { GameBridge.TypeZXEntity });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance, ref bool __result)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized || __instance == null)
                return true;

            if (_pEntityTypeProp == null)
                _pEntityTypeProp = __instance.GetType().GetProperty("EntityType", BindingFlags.Public | BindingFlags.Instance);
            Type entType = _pEntityTypeProp?.GetValue(__instance) as Type;
            if (WonderCrowdfundManager.TryClassifyEntityType(entType, out var fKind))
            {
                if (!WonderCrowdfundManager.CanPlaceBuildVoucher(fKind))
                {
                    __result = false;
                    return false;
                }
                if (WonderCrowdfundManager.IsWonderBlockedGlobally(fKind))
                {
                    __result = false;
                    return false;
                }
                // Once ReadyVoucher is active (team unlocked + funded Research & Build), allow any non-blocked placement
                // that passes the placer's own upkeep check (Workers/Food/Energy) without single-player colonist/Foundry locks.
                var tmpl = DXGame.Current?.GetEntityFromType(entType) as DXEntity;
                if (tmpl != null && GameBridge.TryGetEntityResourcesCost(entType, out _, out _, out _, out _, out _, out int foodReq, out int workReq, out int engReq))
                {
                    if (GameBridge.ReadRawLevelEconomyState(out var e))
                    {
                        int freeWorkers = e.WorkersSupply - e.WorkersNeeded;
                        int freeFood = e.FoodSupply - e.FoodNeeded;
                        int freeEng = e.EnergySupply - e.EnergyNeeded;
                        bool workersOk = workReq <= 0 || freeWorkers >= workReq;
                        bool foodOk = foodReq <= 0 || freeFood >= foodReq;
                        bool engOk = engReq <= 0 || freeEng >= engReq;
                        __result = workersOk && foodOk && engOk;
                        if (!__result)
                            ModLogger.Info($"[WonderCrowdfund] CanExecute({fKind}) false: need(W={workReq},F={foodReq},E={engReq}), avail(W={freeWorkers},F={freeFood},E={freeEng})");
                        return false;
                    }
                }
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_TechTelescope_IsVisible
    {
        public static MethodBase TargetMethod()
        {
            Type tTechTelescope = GameBridge.TypeTechGreatTelescope;
            if (tTechTelescope == null || GameBridge.TypeZXEntity == null) return null;
            return AccessTools.Method(tTechTelescope, ObfuscatedSymbols.Method_ZXCommand_IsVisible, new[] { GameBridge.TypeZXEntity });
        }

        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized)
                return true;

            __result = !WonderCrowdfundManager.IsWonderBlockedGlobally(FunctionalWonderType.Telescope);
            return false;
        }
    }

    /// <summary>
    /// Consumes Functional Wonder Build vouchers when placement succeeds in Build.#=zBTwSMLo=.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Build_ConsumeWonderVoucher
    {
        public static MethodBase TargetMethod()
        {
            Type tBuild = GameBridge.GameAssembly?.GetType("ZX.Commands.Build");
            if (tBuild == null || GameBridge.TypeZXEntity == null || GameBridge.TypeZXCommandTarget == null)
                return null;
            return AccessTools.Method(
                tBuild,
                ObfuscatedSymbols.Method_Build_PlaceBuilding,
                new[] { GameBridge.TypeZXEntity, GameBridge.TypeZXCommandTarget, typeof(Type), typeof(bool), typeof(bool), typeof(DXEntityTemplate) });
        }

        [HarmonyPostfix]
        public static void Postfix(DXEntity __result, object __0, Type __2)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized)
                return;
            if (!WonderCrowdfundManager.TryClassifyEntityType(__2, out var fKind))
                return;

            byte placerPid = FactionOwnershipTracker.GetOwner(__0 as DXEntity);
            if (placerPid == 0) placerPid = CommandExecutor.ActiveCommandPlayerId;
            if (placerPid == 0) placerPid = FactionOwnershipTracker.LocalPlayerId;

            if (fKind == FunctionalWonderType.Telescope)
            {
                if (__0 is DXEntity cc && GameBridge.GetCommandCenterTelescopeEnabled(cc))
                    WonderCrowdfundManager.ConsumeBuildVoucher(FunctionalWonderType.Telescope, placerPid, cc.ID);
            }
            else if (__result != null)
            {
                WonderCrowdfundManager.ConsumeBuildVoucher(fKind, placerPid, __result.ID);
            }
        }
    }

    /// <summary>
    /// Detects Functional Wonder completion when Building / CommandCenter / Structure UpdateEnabled transitions out of IsBeingBuilt.
    /// (Building and CommandCenter override UpdateEnabled without always calling Structure.UpdateEnabled.)
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Structure_WonderCompletion
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var seen = new HashSet<MethodBase>();
            Type[] types = { GameBridge.TypeBuilding, GameBridge.TypeCommandCenter, GameBridge.TypeStructure };
            foreach (Type t in types)
            {
                if (t == null) continue;
                var m = AccessTools.DeclaredMethod(t, ObfuscatedSymbols.Method_Structure_UpdateEnabled, Type.EmptyTypes)
                     ?? AccessTools.Method(t, ObfuscatedSymbols.Method_Structure_UpdateEnabled, Type.EmptyTypes);
                if (m != null && seen.Add(m))
                    yield return m;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized || !(__instance is DXEntity ent))
                return;

            if (ent.GetType() == GameBridge.TypeCommandCenter)
            {
                var st = WonderCrowdfundManager.GetState(FunctionalWonderType.Telescope);
                if (st != null && st.Phase == WonderPhase.Building
                    && GameBridge.GetCommandCenterTelescopeEnabled(ent))
                {
                    object cBuildable = GameBridge.TypeCBuildable != null ? GameBridge.FindComponent(ent, GameBridge.TypeCBuildable) : null;
                    bool finished = cBuildable == null || GameBridge.GetBuildableFactor(cBuildable) >= 0.999f;
                    if (finished)
                        WonderCrowdfundManager.HandleConstructionCompleted(FunctionalWonderType.Telescope, ent);
                }
            }
            else if (WonderCrowdfundManager.TryClassifyEntityType(ent.GetType(), out var fKind))
            {
                var st = WonderCrowdfundManager.GetState(fKind);
                if (st != null && st.Phase == WonderPhase.Building && !GameBridge.IsBuildingBeingBuilt(ent))
                {
                    WonderCrowdfundManager.HandleConstructionCompleted(fKind, ent);
                }
            }
        }
    }

    /// <summary>
    /// Prevents blocking single-player "WONDER COMPLETED" fullscreen modals from pausing all co-op peers
    /// in Multi-Faction mode when a Wonder finishes construction.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_LevelSystem_SuppressWonderModal
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeZXSystemGameLevel != null && GameBridge.TypeZXEntity != null
                ? AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowWonderCompleted, new[] { GameBridge.TypeZXEntity })
                : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(object __0)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized)
                return true;

            if (__0 is DXEntity ent && WonderCrowdfundManager.TryClassifyEntityType(ent.GetType(), out var fKind))
            {
                var st = WonderCrowdfundManager.GetState(fKind);
                if (st != null && st.Phase == WonderPhase.Building)
                    WonderCrowdfundManager.HandleConstructionCompleted(fKind, ent);
                string label = WonderCrowdfundManager.GetDisplayName(fKind);
                string extra = fKind == FunctionalWonderType.Spire
                    ? $" (+{WonderCrowdfundManager.GetScaledSpireEnergy(800)} Shared Energy Grid)"
                    : (fKind == FunctionalWonderType.Academy ? " (Global Veteran Rank)" : " (Global Full Map)");
                GameBridge.ShowTopBanner($"{label} Completed!{extra}", Color.LimeGreen, 4500);
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Detects Functional Wonder death/destruction in ZXLevelState.RecordEntityDead.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_LevelState_WonderDestroyed
    {
        public static MethodBase TargetMethod()
        {
            Type lsType = GameBridge.LevelStateType ?? GameBridge.GameAssembly?.GetType("ZX.ZXLevelState");
            return lsType != null
                ? AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_RecordEntityDead, new[] { typeof(DXEntity) })
                : null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __0)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !WonderCrowdfundManager.IsInitialized)
                return;
            if (__0 is DXEntity deadEnt && WonderCrowdfundManager.TryClassifyEntityType(deadEnt.GetType(), out var fKind))
                WonderCrowdfundManager.HandleWonderDestroyed(fKind, deadEnt.ID);
        }
    }

    // ========================================================================
    // 3. Mayor Sync Extensions: Inject Custom 0% Transfer Fee Mayor & Banner
    // ========================================================================

    [HarmonyPatch]
    public static class Patch_ZXMayor_RegisterCustomMultiFactionMayors
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeZXMayor != null
                ? AccessTools.Method(GameBridge.TypeZXMayor, ObfuscatedSymbols.Method_ZXMayor_LoadAll, Type.EmptyTypes)
                : null;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            GameBridge.ApplyZeroFeeMayorToRegistry();
        }
    }

    [HarmonyPatch]
    public static class Patch_ZXMayor_Apply_MultiFactionBanner
    {
        public static MethodBase TargetMethod()
        {
            Type lsType = GameBridge.LevelStateType ?? GameBridge.GameAssembly?.GetType("ZX.ZXLevelState");
            return GameBridge.TypeZXMayor != null && lsType != null
                ? AccessTools.Method(GameBridge.TypeZXMayor, ObfuscatedSymbols.Method_ZXMayor_Apply, new[] { lsType })
                : null;
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __instance == null)
                return;

            string mayorId = GameBridge.GetMayorId(__instance);
            if (mayorId == FactionContextManager.CoopMayorZeroFeeId && FactionContextManager.IsInitialized)
            {
                FactionContextManager.ZeroTransferFeeActive = true;
                ModLogger.Info("[MultiFaction] Custom Mayor applied: Team Resource Transfer Fee permanently reduced from 20% to 0%!");
            }

            FactionMayorBannerTracker.LastAppliedMayorName = GameBridge.GetMayorName(__instance) ?? mayorId;
            FactionMayorBannerTracker.LastAppliedPlayerId = CommandExecutor.ActiveCommandPlayerId;
        }
    }

    // ========================================================================
    // 4. Faction Minimap & Selection/Ring Color Override (ZXEntity.MinimapColor)
    // ========================================================================

    [HarmonyPatch]
    public static class Patch_ZXEntity_MinimapColor_MultiFaction
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeZXEntity != null
                ? AccessTools.PropertyGetter(GameBridge.TypeZXEntity, "MinimapColor")
                : null;
        }

        [HarmonyPostfix]
        public static void Postfix(DXEntity __instance, ref Color? __result)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !FactionOwnershipTracker.IsInitialized || __instance == null)
                return;
            if (!GameBridge.IsPlayerEntity(__instance))
                return;

            byte owner = FactionOwnershipTracker.GetOwner(__instance.ID);
            if (owner >= 1 && owner <= 3)
                __result = FactionOwnershipTracker.GetPlayerColor(owner);
        }
    }
}
