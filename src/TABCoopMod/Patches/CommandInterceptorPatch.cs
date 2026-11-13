using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;

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
        /// Routes a command through the session (or executes it directly when no session is active).
        /// Use this for commands generated outside the Harmony capture points (pause/speed keys).
        /// </summary>
        public static void Dispatch(NetCommand cmd)
        {
            if (cmd == null) return;
            var callback = OnLocalCommandCaptured;
            if (callback != null) callback(cmd);
            else CommandExecutor.Execute(cmd);
        }

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

    // NOTE: ZXCommand, ZXEntity, ZXCommandTarget, CCommandable and CSelectable are cross-assembly
    // internal types. Desktop CLR 4.6 throws TypeAccessException when a patch signature or body
    // references them directly, so all 5 patches use TargetMethod() + object / DXEntity / DXComponent.

    // 1. Track CCommandable.#=zrz2EI1A= (Queue auto-advance) so we don't intercept internal queue transitions
    [HarmonyPatch]
    public static class Patch_CCommandable_AdvanceQueue
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeCCommandable, ObfuscatedSymbols.Method_CCommandable_AdvanceQueue);

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
                new[] { GameBridge.TypeZXCommand, typeof(PointF) });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __0, PointF __1)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand)
                return true;

            ulong[] unitIds = GameBridge.GetSelectedPlayerEntityIds();

            // Nothing movable selected: let vanilla handle the click (e.g. right-clicking with a
            // building selected). Swallowing it here would silently eat legitimate input.
            if (unitIds == null || unitIds.Length == 0)
                return true;

            var netCmd = new NetCommand
            {
                Type = NetCommandType.GroupMoveOrPatrol,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                IsShiftQueued = DXInputState.Current?.IsKeyPressed(DXKeys.Shift, false) ?? false,
                CommandId = GameBridge.GetCommandId(__0),
                ExecutorEntityIds = unitIds,
                TargetPosition = __1
            };

            // Show local ground click indicator immediately for responsive feel
            GameBridge.ShowGroundIndicatorForCommand(__0, __1, 1f);

            CommandInterceptor.SuppressIndividualMoveExecution = true;
            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // 3. Intercept Generic ZXCommand.#=zfmgzkuw=(ZXEntity executor, ZXCommandTarget target)
    [HarmonyPatch]
    public static class Patch_ZXCommand_Execute
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                GameBridge.TypeZXCommand,
                ObfuscatedSymbols.Method_ZXCommand_Execute,
                new[] { GameBridge.TypeZXEntity, GameBridge.TypeZXCommandTarget });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance, object __0, object __1)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || CommandInterceptor.InQueueAutoAdvance)
                return true;

            // Only intercept commands issued on Player team entities! (Never intercept Zombie AI attacks or neutral entities)
            var executor = __0 as DXEntity;
            if (executor == null || !GameBridge.IsPlayerEntity(executor))
                return true;

            // Ignore unit basic auto-attack commands triggered by BHSoldier / BHAttackTower AI behaviors during UpdateGameLogic
            if (LockstepClock.InDeterministicContext)
                return true;

            // If this is the per-unit Travel/Patrol follow-up loop right after #=zQp2M56bpPd9jmiNkVA==, swallow it
            if (CommandInterceptor.SuppressIndividualMoveExecution && GameBridge.IsGroupRoutedCommand(__instance))
                return false;

            bool isShift = DXInputState.Current?.IsKeyPressed(DXKeys.Shift, false) ?? false;
            int cmdId = GameBridge.GetCommandId(__instance);
            GameBridge.UnpackCommandTarget(__1, out ulong targetEntityId, out PointF targetPos, out SizeF targetSize, out PointF targetOrigin);

            // Build and the free mayor-reward buildings (EntityBonusCreation) are both entity-creation
            // commands; they must be replayed as PlaceBuilding so the peer builds the same structure.
            if (GameBridge.IsBuildOrBonusCommand(__instance, out bool isBonus))
            {
                Type buildingType = GameBridge.GetBuildingTypeFromCommand(__instance);
                if (buildingType != null)
                {
                    var netBuild = new NetCommand
                    {
                        Type = NetCommandType.PlaceBuilding,
                        PlayerId = CommandInterceptor.LocalPlayerId,
                        TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                        IsShiftQueued = isShift,
                        CommandId = cmdId,
                        BuildingTypeName = buildingType.FullName,
                        IsBonusBuilding = isBonus,
                        ExecutorEntityIds = new[] { executor.ID },
                        TargetEntityId = targetEntityId,
                        TargetPosition = targetPos,
                        TargetSize = targetSize,
                        TargetOrigin = targetOrigin
                    };
                    CommandInterceptor.OnLocalCommandCaptured?.Invoke(netBuild);
                    return false;
                }
            }

            var netCmd = new NetCommand
            {
                Type = NetCommandType.ExecuteCommand,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                IsShiftQueued = isShift,
                CommandId = cmdId,
                ExecutorEntityIds = new[] { executor.ID },
                TargetEntityId = targetEntityId,
                TargetPosition = targetPos,
                TargetSize = targetSize,
                TargetOrigin = targetOrigin
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
    [HarmonyPatch]
    public static class Patch_CCommandable_CancelCurrent
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeCCommandable, ObfuscatedSymbols.Method_CCommandable_CancelCurrent);

        [HarmonyPrefix]
        public static bool Prefix(DXComponent __instance)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.InDeterministicContext)
                return true;

            var ent = __instance?.Entity;
            if (ent == null || !GameBridge.IsPlayerEntity(ent))
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
    [HarmonyPatch]
    public static class Patch_CCommandable_CancelQueueSlot
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeCCommandable, ObfuscatedSymbols.Method_CCommandable_CancelQueueAt, new[] { typeof(int) });

        [HarmonyPrefix]
        public static bool Prefix(DXComponent __instance, int __0)
        {
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.InDeterministicContext)
                return true;

            var ent = __instance?.Entity;
            if (ent == null || !GameBridge.IsPlayerEntity(ent))
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
