using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Sync;
using ZX;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Phase 4 M1: Replaces ZXSystemMapLayers.#=zK$nELVArHhqXXa$0lA==() single-CommandCenter
    /// energy grid BFS with a multi-CommandCenter BFS whenever multiple alive CommandCenters
    /// exist (or Multi-Faction mode is active), implementing the locked Shared Energy Perimeter rule.
    /// </summary>
    [HarmonyPatch]
    public static class EnergyGridMultiBasePatch
    {
        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            return t != null
                ? AccessTools.Method(t, ObfuscatedSymbols.Method_MapLayers_RebuildEnergyGrid, Type.EmptyTypes)
                : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance)
        {
            var ccs = GameBridge.GetAllCommandCenters(aliveOnly: true);
            if (!CoopSessionManager.IsMultiFactionActive && ccs.Count <= 1)
                return true;
            if (ccs.Count == 0)
                return false;

            if (!GameBridge.TryGetEnergyGridState(
                    __instance,
                    out uint[,] energyGrid,
                    out List<Point> frontier,
                    out IList sources,
                    out int lastIdx))
            {
                return true;
            }

            var terrain = GameBridge.GetTerrainResourceCells();
            if (terrain == null)
                return true;

            var game = DXGame.Current;
            if (game == null)
                return true;

            Array.Clear(energyGrid, 0, (int)energyGrid.LongLength);
            frontier.Clear();
            sources.Clear();

            for (int i = 0; i < ccs.Count; i++)
            {
                frontier.Add(ccs[i].Cell);
                sources.Add(ccs[i]);
            }
            lastIdx = 0;

            var transfererList = new List<DXEntity>();
            if (GameBridge.TypeCEnergyTransferer != null)
            {
                var allEntities = GameBridge.GetEntitiesList(game);
                if (allEntities != null)
                {
                    for (int i = 0; i < allEntities.Count; i++)
                    {
                        var e = allEntities[i];
                        if (e != null && !e.Disposed && GameBridge.HasComponent(e, GameBridge.TypeCEnergyTransferer))
                            transfererList.Add(e);
                    }
                }
            }

            int maxX = energyGrid.GetLength(0) - 1;
            int maxY = energyGrid.GetLength(1) - 1;

            while (frontier.Count > 0)
            {
                int tail = frontier.Count - 1;
                Point pt = frontier[tail];
                frontier.RemoveAt(tail);

                bool covered = false;
                int srcCount = sources.Count;
                for (int i = 0; i < srcCount; i++)
                {
                    int idx = (i + lastIdx) % srcCount;
                    var srcEnt = sources[idx] as DXEntity;
                    if (srcEnt == null) continue;
                    int radius = GameBridge.GetEntityEnergyTransferRadius(srcEnt);
                    if (srcEnt.Cell.InRange(pt, radius))
                    {
                        covered = true;
                        lastIdx = i;
                        break;
                    }
                }

                if (!covered)
                    continue;

                energyGrid[pt.X, pt.Y]++;

                for (int j = 0; j < transfererList.Count; j++)
                {
                    var tEnt = transfererList[j];
                    if (tEnt.Cell == pt && !sources.Contains(tEnt))
                    {
                        sources.Add(tEnt);
                        lastIdx = sources.Count - 1;
                    }
                }

                if (pt.X > 1)
                {
                    var terr = terrain[pt.X - 1, pt.Y];
                    if (terr != ZXTerrainResourceType.Sea && terr != ZXTerrainResourceType.Mountain && energyGrid[pt.X - 1, pt.Y] == 0)
                        frontier.Add(new Point(pt.X - 1, pt.Y));
                }
                if (pt.X < maxX)
                {
                    var terr = terrain[pt.X + 1, pt.Y];
                    if (terr != ZXTerrainResourceType.Sea && terr != ZXTerrainResourceType.Mountain && energyGrid[pt.X + 1, pt.Y] == 0)
                        frontier.Add(new Point(pt.X + 1, pt.Y));
                }
                if (pt.Y < maxY)
                {
                    var terr = terrain[pt.X, pt.Y + 1];
                    if (terr != ZXTerrainResourceType.Sea && terr != ZXTerrainResourceType.Mountain && energyGrid[pt.X, pt.Y + 1] == 0)
                        frontier.Add(new Point(pt.X, pt.Y + 1));
                }
                if (pt.Y > 1)
                {
                    var terr = terrain[pt.X, pt.Y - 1];
                    if (terr != ZXTerrainResourceType.Sea && terr != ZXTerrainResourceType.Mountain && energyGrid[pt.X, pt.Y - 1] == 0)
                        frontier.Add(new Point(pt.X, pt.Y - 1));
                }
            }

            GameBridge.SetEnergyLastIdx(__instance, lastIdx);
            return false;
        }
    }
}
