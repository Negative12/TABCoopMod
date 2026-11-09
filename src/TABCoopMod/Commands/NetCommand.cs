using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using ZX.Commands;
using ZX.Components;
using ZX.Entities;

namespace TABCoopMod.Commands
{
    public enum NetCommandType : byte
    {
        None = 0,
        ExecuteCommand = 1,      // Generic ZXCommand.#=zfmgzkuw= (Attack, Train, Tech, Destroy, Repair, Market, RallyPoint, etc.)
        GroupMoveOrPatrol = 2,   // Group distributed move/patrol/grenade (#=zgn6bDkSd9MGS3$WvAQ==)
        PlaceBuilding = 3,       // Building placement (Build.#=zBTwSMLo=)
        CancelCurrentOrder = 4,  // Cancel active command/construction (CCommandable.#=zWGiN6nrTv8Dp)
        CancelQueuedOrder = 5,   // Cancel production queue slot (CCommandable.#=z8g7GC7xLrr8j)
        SetPaused = 6,           // Pause / Resume toggle
        SetGameSpeed = 7,        // GameSpeed multiplier change
        MayorVoteOrSelect = 8    // Mayor selection (0 = left male, 1 = right female)
    }

    /// <summary>
    /// Serializable deterministic player command payload.
    /// Carries explicit ExecutorEntityIds so replay NEVER touches or depends on local CSelectable!
    /// </summary>
    public sealed class NetCommand
    {
        public NetCommandType Type { get; set; }
        public byte PlayerId { get; set; } = 1;
        public int TargetSimTick { get; set; }
        public bool IsShiftQueued { get; set; }

        // Command & Entity Identifiers
        public int CommandId { get; set; }
        public string BuildingTypeName { get; set; } = string.Empty;
        public ulong[] ExecutorEntityIds { get; set; } = Array.Empty<ulong>();

        // ZXCommandTarget Payload
        public ulong TargetEntityId { get; set; }
        public PointF TargetPosition { get; set; }
        public SizeF TargetSize { get; set; }
        public PointF TargetOrigin { get; set; }

        // Building / Queue / State Extras
        public Rectangle BuildingCellArea { get; set; }
        public bool IsBonusBuilding { get; set; }
        public bool InstantBuild { get; set; }
        public byte QueueSlotIndex { get; set; }
        public bool TargetPausedState { get; set; }
        public byte MayorOptionIndex { get; set; }
        public bool IsMayorFinalDecision { get; set; }

        public void WriteTo(BinaryWriter w)
        {
            w.Write((byte)Type);
            w.Write(PlayerId);
            w.Write(TargetSimTick);
            w.Write(IsShiftQueued);
            w.Write(CommandId);
            w.Write(BuildingTypeName ?? string.Empty);

            ushort count = (ushort)(ExecutorEntityIds?.Length ?? 0);
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                w.Write(ExecutorEntityIds[i]);
            }

            w.Write(TargetEntityId);
            w.Write(TargetPosition.X);
            w.Write(TargetPosition.Y);
            w.Write(TargetSize.Width);
            w.Write(TargetSize.Height);
            w.Write(TargetOrigin.X);
            w.Write(TargetOrigin.Y);

            w.Write(BuildingCellArea.X);
            w.Write(BuildingCellArea.Y);
            w.Write(BuildingCellArea.Width);
            w.Write(BuildingCellArea.Height);
            w.Write(IsBonusBuilding);
            w.Write(InstantBuild);
            w.Write(QueueSlotIndex);
            w.Write(TargetPausedState);
            w.Write(MayorOptionIndex);
            w.Write(IsMayorFinalDecision);
        }

        public static NetCommand ReadFrom(BinaryReader r)
        {
            var cmd = new NetCommand
            {
                Type = (NetCommandType)r.ReadByte(),
                PlayerId = r.ReadByte(),
                TargetSimTick = r.ReadInt32(),
                IsShiftQueued = r.ReadBoolean(),
                CommandId = r.ReadInt32(),
                BuildingTypeName = r.ReadString()
            };

            ushort count = r.ReadUInt16();
            cmd.ExecutorEntityIds = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                cmd.ExecutorEntityIds[i] = r.ReadUInt64();
            }

            cmd.TargetEntityId = r.ReadUInt64();
            cmd.TargetPosition = new PointF(r.ReadSingle(), r.ReadSingle());
            cmd.TargetSize = new SizeF(r.ReadSingle(), r.ReadSingle());
            cmd.TargetOrigin = new PointF(r.ReadSingle(), r.ReadSingle());

            cmd.BuildingCellArea = new Rectangle(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            cmd.IsBonusBuilding = r.ReadBoolean();
            cmd.InstantBuild = r.ReadBoolean();
            cmd.QueueSlotIndex = r.ReadByte();
            cmd.TargetPausedState = r.ReadBoolean();
            cmd.MayorOptionIndex = r.ReadByte();
            cmd.IsMayorFinalDecision = r.ReadBoolean();

            return cmd;
        }

        public override string ToString()
        {
            return $"[NetCmd P{PlayerId} T={TargetSimTick} {Type} CmdId={CommandId} Entities={ExecutorEntityIds?.Length ?? 0} Pos=({TargetPosition.X:F1},{TargetPosition.Y:F1})]";
        }
    }

    /// <summary>
    /// Executes NetCommands deterministically on the local game state without modifying local player's CSelectable.
    /// </summary>
    public static class CommandExecutor
    {
        /// <summary>
        /// Reentrancy guard: when true, CommandInterceptorPatch lets original game methods run without re-capturing.
        /// </summary>
        [ThreadStatic]
        public static bool IsReplayingCommand;

        public static void Execute(NetCommand netCmd)
        {
            if (netCmd == null || netCmd.Type == NetCommandType.None)
                return;

            bool prevReplaying = IsReplayingCommand;
            bool prevDet = LockstepClock.InDeterministicContext;
            IsReplayingCommand = true;
            LockstepClock.InDeterministicContext = true;

            // Save local Shift key state so we can simulate the sender's Shift modifier accurately
            bool hadShiftKey = false;
            var inputKeys = DXInputState.Current?.Keys;
            if (inputKeys != null)
            {
                hadShiftKey = inputKeys.Contains(DXKeys.Shift);
                if (netCmd.IsShiftQueued && !hadShiftKey)
                    inputKeys.Add(DXKeys.Shift);
                else if (!netCmd.IsShiftQueued && hadShiftKey)
                    inputKeys.Remove(DXKeys.Shift);
            }

            try
            {
                switch (netCmd.Type)
                {
                    case NetCommandType.ExecuteCommand:
                        ExecuteSingleOrBatchCommand(netCmd);
                        break;

                    case NetCommandType.GroupMoveOrPatrol:
                        ExecuteGroupMoveCommand(netCmd);
                        break;

                    case NetCommandType.PlaceBuilding:
                        ExecutePlaceBuilding(netCmd);
                        break;

                    case NetCommandType.CancelCurrentOrder:
                        ExecuteCancelCurrentOrder(netCmd);
                        break;

                    case NetCommandType.CancelQueuedOrder:
                        ExecuteCancelQueuedOrder(netCmd);
                        break;

                    case NetCommandType.SetPaused:
                        if (DXGame.Current != null)
                        {
                            DXGame.Current.Paused = netCmd.TargetPausedState;
                            GameBridge.ShowTopBanner(
                                netCmd.TargetPausedState ? $"Player {netCmd.PlayerId} Paused Game" : $"Player {netCmd.PlayerId} Resumed Game",
                                Color.Gold, 1500);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CommandExecutor] Failed to execute {netCmd}", ex);
            }
            finally
            {
                // Restore local Shift key state
                if (inputKeys != null)
                {
                    if (hadShiftKey && !inputKeys.Contains(DXKeys.Shift))
                        inputKeys.Add(DXKeys.Shift);
                    else if (!hadShiftKey && inputKeys.Contains(DXKeys.Shift))
                        inputKeys.Remove(DXKeys.Shift);
                }

                LockstepClock.InDeterministicContext = prevDet;
                IsReplayingCommand = prevReplaying;
            }
        }

        private static void ExecuteSingleOrBatchCommand(NetCommand netCmd)
        {
            var zxCmd = GameBridge.GetCommandById(netCmd.CommandId);
            if (zxCmd == null)
            {
                ModLogger.Warn($"[CommandExecutor] Unknown CommandId: {netCmd.CommandId}");
                return;
            }

            var target = BuildCommandTarget(netCmd);

            foreach (ulong entityId in netCmd.ExecutorEntityIds)
            {
                var executor = DXGame.Current?.GetEntityByID(entityId) as ZXEntity;
                if (executor == null || executor.Disposed)
                    continue;

                GameBridge.ExecuteCommandOnEntity(zxCmd, executor, target);
            }
        }

        private static void ExecuteGroupMoveCommand(NetCommand netCmd)
        {
            var zxCmd = GameBridge.GetCommandById(netCmd.CommandId);
            if (zxCmd == null) return;

            var entities = new List<DXEntity>(netCmd.ExecutorEntityIds.Length);
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                var ent = DXGame.Current?.GetEntityByID(id);
                if (ent != null && !ent.Disposed && ent.HasComponent<CMovable>() && ent.HasComponent<CCommandable>())
                {
                    entities.Add(ent);
                }
            }

            if (entities.Count == 0) return;

            // Make a copy because #=zgn6bDkSd9MGS3$WvAQ== mutates the passed list via Remove()!
            var entitiesForExecution = new List<DXEntity>(entities);

            // 1. Assign distributed target positions to each unit's CCommandable.Target
            GameBridge.AssignGroupMoveTargets(entities, zxCmd, netCmd.TargetPosition);

            // 2. Trigger command execution on each unit using its assigned CCommandable.Target
            foreach (var dXEntity in entitiesForExecution)
            {
                if (dXEntity is ZXEntity zxEnt)
                {
                    var cmdComp = zxEnt.GetCommandable();
                    if (cmdComp != null)
                    {
                        GameBridge.ExecuteCommandOnEntity(zxCmd, zxEnt, cmdComp.Target);
                    }
                }
            }
        }

        private static void ExecutePlaceBuilding(NetCommand netCmd)
        {
            if (string.IsNullOrEmpty(netCmd.BuildingTypeName)) return;

            Type buildingType = GameBridge.GameAssembly.GetType(netCmd.BuildingTypeName);
            if (buildingType == null)
            {
                ModLogger.Warn($"[CommandExecutor] Unknown BuildingTypeName: {netCmd.BuildingTypeName}");
                return;
            }

            ulong builderId = netCmd.ExecutorEntityIds.Length > 0 ? netCmd.ExecutorEntityIds[0] : 0UL;
            ZXEntity builder = (builderId != 0 ? DXGame.Current?.GetEntityByID(builderId) as ZXEntity : null)
                               ?? GameBridge.CurrentLevelState?.CurrentCommandCenter;

            if (builder == null) return;

            var target = BuildCommandTarget(netCmd);
            var getBuildCmdMethod = HarmonyLib.AccessTools.Method(typeof(Build), ObfuscatedSymbols.Method_BuildOrTrain_GetForType, new[] { typeof(Type) });
            var buildCmd = getBuildCmdMethod?.Invoke(null, new object[] { buildingType }) as ZXCommand;
            if (buildCmd != null)
            {
                // Execute through Build.#=zfmgzkuw= so resource deduction and statistics are properly applied!
                GameBridge.ExecuteCommandOnEntity(buildCmd, builder, target);
            }
            else
            {
                GameBridge.PlaceBuilding(builder, target, buildingType, netCmd.IsBonusBuilding, netCmd.InstantBuild, null);
            }
        }

        private static void ExecuteCancelCurrentOrder(NetCommand netCmd)
        {
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                var zxEnt = DXGame.Current?.GetEntityByID(id) as ZXEntity;
                zxEnt?.GetCommandable()?.CancelCurrentOrder();
            }
        }

        private static void ExecuteCancelQueuedOrder(NetCommand netCmd)
        {
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                var zxEnt = DXGame.Current?.GetEntityByID(id) as ZXEntity;
                zxEnt?.GetCommandable()?.CancelQueuedOrderAt(netCmd.QueueSlotIndex);
            }
        }

        public static ZXCommandTarget BuildCommandTarget(NetCommand netCmd)
        {
            var target = default(ZXCommandTarget);
            target.SerPosition = netCmd.TargetPosition;
            target.SerSize = netCmd.TargetSize;
            target.SerOrigin = netCmd.TargetOrigin;

            if (netCmd.TargetEntityId != 0UL)
            {
                var targetEnt = DXGame.Current?.GetEntityByID(netCmd.TargetEntityId) as ZXEntity;
                target.SerEntityRef = targetEnt;
            }
            return target;
        }
    }
}
