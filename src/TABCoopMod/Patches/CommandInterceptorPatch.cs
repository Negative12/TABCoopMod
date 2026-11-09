using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using ZX.Commands;
using ZX.Components;
using ZX.Entities;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Intercepts local player gameplay commands when Lockstep / Recording mode is active,
    /// converts them into NetCommand packets, and routes them through the active CommandRouter.
    /// Carefully ignores AI/internal command invocations (e.g., zombie attacks, production queue auto-advance).
    /// </summary>
    public static class CommandInterceptor
    {
        public static bool IsInterceptionEnabled { get; set; }
        public static byte LocalPlayerId { get; set; } = 1;

        /// <summary>
        /// Callback invoked whenever a local player command is intercepted.
        /// In single-player replay verification, this records the command and executes it at the current tick;
        /// in multiplayer Lockstep, this queues the command into the current TurnInputBatch.
        /// </summary>
        public static Action<NetCommand> OnLocalCommandCaptured;

        /// <summary>
        /// Internal flag set while CCommandable.#=zrz2EI1A= (production queue auto-advance) is running,
        /// so auto-popped queued orders are NOT mistaken for new player clicks!
        /// </summary>
        [ThreadStatic]
        internal static bool InQueueAutoAdvance;

        /// <summary>
        /// Internal flag set while ExecuteGroupMoveCommand is running, so the per-unit Travel.#=zfmgzkuw=
        /// loop in ZXSystemGameLevel right-click/left-click is swallowed after GroupMoveOrPatrol is already captured.
        /// </summary>
        [ThreadStatic]
        internal static bool SuppressIndividualMoveExecution;
    }

    // 1. Track CCommandable.#=zrz2EI1A= (Queue auto-advance) so we don't intercept internal queue transitions
    [HarmonyPatch(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_AdvanceQueue)]
    public static class Patch_CCommandable_AdvanceQueue
    {
        [HarmonyPrefix]
        public static void Prefix() => CommandInterceptor.InQueueAutoAdvance = true;

        [HarmonyPostfix]
        public static void Postfix() => CommandInterceptor.InQueueAutoAdvance = false;
    }

    // 2. Intercept Group Move / Distributed Target Assignment (#=zQp2M56bpPd9jmiNkVA==)
    [HarmonyPatch]
    public static class Patch_LevelSys_GroupMoveSelected
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                GameBridge.TypeZXSystemGameLevel,
                ObfuscatedSymbols.Method_LevelSys_GroupMoveSelected,
                new[] { typeof(ZXCommand), typeof(PointF) });
        }

        [HarmonyPrefix]
        public static bool Prefix(ZXCommand __0, PointF __1)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand)
                return true;

            var selectedList = GameBridge.GetLocallySelectedList();
            if (selectedList == null || selectedList.Count == 0)
                return true;

            ulong[] unitIds = selectedList
                .Select(s => ((DXComponent)s).Entity)
                .Where(e => e != null && !e.Disposed && e.HasComponent<CMovable>() && e.HasComponent<CCommandable>())
                .Select(e => e.ID)
                .ToArray();

            // Nothing movable selected: let vanilla handle the click (e.g. right-clicking with a
            // building selected). Swallowing it here would silently eat legitimate input.
            if (unitIds.Length == 0)
                return true;

            var netCmd = new NetCommand
            {
                Type = NetCommandType.GroupMoveOrPatrol,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                IsShiftQueued = DXInputState.Current?.IsKeyPressed(DXKeys.Shift, false) ?? false,
                CommandId = __0.ID,
                ExecutorEntityIds = unitIds,
                TargetPosition = __1
            };

            // Show local ground click indicator immediately for responsive feel
            GameBridge.TypeZXSystemGameLevel
                .GetMethod(ObfuscatedSymbols.Method_LevelSys_ShowGroundIndicator, BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(GameBridge.LevelSystemInstance, new object[] { __1, __0.TargetIndicatorColor, 1f });

            CommandInterceptor.SuppressIndividualMoveExecution = true;
            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // 3. Intercept Generic ZXCommand.#=zfmgzkuw=(ZXEntity executor, ZXCommandTarget target)
    [HarmonyPatch(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_Execute)]
    public static class Patch_ZXCommand_Execute
    {
        [HarmonyPrefix]
        public static bool Prefix(ZXCommand __instance, ZXEntity __0, ZXCommandTarget __1)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || CommandInterceptor.InQueueAutoAdvance)
                return true;

            // Only intercept commands issued on Player team entities! (Never intercept Zombie AI attacks or neutral entities)
            if (__0 == null || __0.Team != ZX.ZXTeamType.Player)
                return true;

            // Ignore unit basic auto-attack commands triggered by BHSoldier / BHAttackTower AI behaviors during UpdateGameLogic
            if (LockstepClock.InDeterministicContext)
                return true;

            // If this is the per-unit Travel/Patrol follow-up loop right after #=zQp2M56bpPd9jmiNkVA==, swallow it
            if (CommandInterceptor.SuppressIndividualMoveExecution &&
                (__instance is Travel || __instance is Patrol || __instance is Chase || __instance is UseItem_Grenade))
            {
                return false;
            }

            bool isShift = DXInputState.Current?.IsKeyPressed(DXKeys.Shift, false) ?? false;

            if (__instance is Build buildCmd)
            {
                var netBuild = new NetCommand
                {
                    Type = NetCommandType.PlaceBuilding,
                    PlayerId = CommandInterceptor.LocalPlayerId,
                    TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                    IsShiftQueued = isShift,
                    CommandId = buildCmd.ID,
                    BuildingTypeName = buildCmd.EntityType?.FullName ?? string.Empty,
                    ExecutorEntityIds = new[] { __0.ID },
                    TargetEntityId = __1.SerEntityRef.IDEntity,
                    TargetPosition = __1.SerPosition,
                    TargetSize = __1.SerSize,
                    TargetOrigin = __1.SerOrigin
                };
                CommandInterceptor.OnLocalCommandCaptured?.Invoke(netBuild);
                return false;
            }

            var netCmd = new NetCommand
            {
                Type = NetCommandType.ExecuteCommand,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                IsShiftQueued = isShift,
                CommandId = __instance.ID,
                ExecutorEntityIds = new[] { __0.ID },
                TargetEntityId = __1.SerEntityRef.IDEntity,
                TargetPosition = __1.SerPosition,
                TargetSize = __1.SerSize,
                TargetOrigin = __1.SerOrigin
            };

            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // Reset SuppressIndividualMoveExecution at the end of right-click / left-click command dispatch.
    // NOTE: OMiniMap.OnMouseRightDown is deliberately NOT patched here — its Eazfuscator-generated
    // exception blocks make MonoMod's IL rewriter throw "异常块的错误代码生成" at PatchAll time,
    // which aborts the entire mod initialization. Minimap right-click is instead handled by
    // GameBridge-level reset on the next frame (see SessionLifecyclePatches).
    [HarmonyPatch]
    public static class Patch_LevelSys_ResetMoveSuppression
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var onRightClick = AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_OnSceneRightClick);
            if (onRightClick != null) yield return onRightClick;

            var onExecPending = AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ExecutePendingCmd);
            if (onExecPending != null) yield return onExecPending;
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            CommandInterceptor.SuppressIndividualMoveExecution = false;
        }
    }

    // Reset per-frame command guards at the very start of every render frame.
    // Some input paths cannot be patched (OMiniMap.OnMouseRightDown throws in MonoMod), so the
    // suppression flag set by GroupMove can otherwise leak into later frames and eat real orders.
    [HarmonyPatch(typeof(DXGame), "StartFrame")]
    public static class Patch_ResetCommandGuardsOnStartFrame
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
        }
    }

    // 4. Intercept Cancel Current Order (CCommandable.#=zWGiN6nrTv8Dp)
    [HarmonyPatch(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelCurrent)]
    public static class Patch_CCommandable_CancelCurrent
    {
        [HarmonyPrefix]
        public static bool Prefix(CCommandable __instance)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.InDeterministicContext)
                return true;

            var ent = ((DXComponent)__instance).Entity as ZXEntity;
            if (ent == null || ent.Team != ZX.ZXTeamType.Player)
                return true;

            var netCmd = new NetCommand
            {
                Type = NetCommandType.CancelCurrentOrder,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                ExecutorEntityIds = new[] { ent.ID }
            };
            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // 5. Intercept Cancel Production Queue Slot (CCommandable.#=z8g7GC7xLrr8j)
    [HarmonyPatch(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelQueueAt)]
    public static class Patch_CCommandable_CancelQueueSlot
    {
        [HarmonyPrefix]
        public static bool Prefix(CCommandable __instance, int __0)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.InDeterministicContext)
                return true;

            var ent = ((DXComponent)__instance).Entity as ZXEntity;
            if (ent == null || ent.Team != ZX.ZXTeamType.Player)
                return true;

            var netCmd = new NetCommand
            {
                Type = NetCommandType.CancelQueuedOrder,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                ExecutorEntityIds = new[] { ent.ID },
                QueueSlotIndex = (byte)__0
            };
            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }
}
