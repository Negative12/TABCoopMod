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
            var ccs = GameBridge.GetAliveCommandCentersCached();
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

            var transfererList = new List<DXEntity>(64);
            if (GameBridge.TypeCEnergyTransferer != null)
            {
                foreach (var comp in game.ComponentsOfType(GameBridge.TypeCEnergyTransferer))
                {
                    var e = comp?.Entity;
                    if (e != null && !e.Disposed)
                        transfererList.Add(e);
                }
                if (transfererList.Count > 1)
                    transfererList.Sort(GameBridge.CompareEntitiesDeterministically);
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

    /// <summary>
    /// Replaces CEnergyTransferer.#=zeJrYi6vVVMO8Q25qqH7ixNY=() (per-tick disconnected-tower check)
    /// which vanilla seeds solely from local ZXSystemGameLevel.Current.CurrentCommandCenter.
    /// In Separated Multi-Faction matches, Host had CC1 while Client had CC2, causing each peer to
    /// disconnect the other player's CommandCenter energy grid every single tick.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CEnergyTransferer_CheckDisconnectedMultiBase
    {
        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.TypeCEnergyTransferer ?? GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_CEnergyTransferer);
            return t != null
                ? AccessTools.Method(t, ObfuscatedSymbols.Method_CEnergyTransferer_CheckDisconnected, Type.EmptyTypes)
                : null;
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            var ccs = GameBridge.GetAliveCommandCentersCached();
            if (!CoopSessionManager.IsMultiFactionActive && ccs.Count <= 1)
                return true;
            if (ccs.Count == 0)
                return false;

            var game = DXGame.Current;
            Type tEt = GameBridge.TypeCEnergyTransferer;
            if (game == null || tEt == null)
                return true;

            var remaining = new List<DXComponent>(32);
            var connected = new List<DXComponent>(32);
            var connectedSet = new HashSet<DXComponent>();

            // Seed BFS with every alive CommandCenter's CEnergyTransferer.
            for (int i = 0; i < ccs.Count; i++)
            {
                var comp = GameBridge.FindComponent(ccs[i], tEt) as DXComponent;
                if (comp != null && connectedSet.Add(comp))
                    connected.Add(comp);
            }

            foreach (var comp in game.ComponentsOfType(tEt))
            {
                if (comp == null || connectedSet.Contains(comp)) continue;
                var ent = comp.Entity;
                if (ent == null || ent.Disposed) continue;
                if (!GameBridge.IsEntityEnabled(ent)) continue;
                remaining.Add(comp);
            }
            if (remaining.Count > 1)
                remaining.Sort((a, b) => GameBridge.CompareEntitiesDeterministically(a.Entity, b.Entity));

            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var cand = remaining[i];
                    Point cell = cand.Entity.Cell;
                    bool linked = false;
                    for (int j = 0; j < connected.Count; j++)
                    {
                        var srcEnt = connected[j].Entity;
                        if (srcEnt == null) continue;
                        int r = GameBridge.GetEntityEnergyTransferRadius(srcEnt);
                        if (cell.InRange(srcEnt.Cell, r + 1))
                        {
                            linked = true;
                            break;
                        }
                    }
                    if (linked)
                    {
                        progress = true;
                        connected.Add(cand);
                        connectedSet.Add(cand);
                        remaining.RemoveAt(i);
                    }
                }
            }

            for (int i = 0; i < remaining.Count; i++)
            {
                var ent = remaining[i].Entity;
                if (ent != null && GameBridge.TypeBuilding != null && GameBridge.TypeBuilding.IsInstanceOfType(ent))
                {
                    GameBridge.SetBuildingHasEnergyConnection(ent, false);
                    GameBridge.InvokeCEnergyTransfererUpdateGrid(remaining[i]);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Replaces ZXSystemMapLayers.#=z9ok_i231SPp6b3Ux7A==() (level-init energy island pruning)
    /// so separated CommandCenter islands are each validated against ANY CommandCenter via
    /// #=zs1C_pLjYHbdEceIG1y8dYemfTA9s(cell) instead of requiring connectivity to a single cached
    /// cell from the first-scanned island.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapLayers_InitEnergyConnectivityMultiBase
    {
        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            return t != null
                ? AccessTools.Method(t, ObfuscatedSymbols.Method_MapLayers_InitEnergyConnectivity, Type.EmptyTypes)
                : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance)
        {
            var ccs = GameBridge.GetAliveCommandCentersCached();
            if (!CoopSessionManager.IsMultiFactionActive && ccs.Count <= 1)
                return true;

            var game = DXGame.Current;
            var level = DXLevel.Current;
            var terrain = GameBridge.GetTerrainResourceCells();
            var energyGrid = GameBridge.GetMapLayersEnergyGrid(__instance);
            if (game == null || level == null || terrain == null || energyGrid == null)
                return true;

            GameBridge.SetMapLayersEnergyInitDone(__instance, false);

            Type tEt = GameBridge.TypeCEnergyTransferer;
            Type tBldg = GameBridge.TypeBuilding;
            if (tEt != null)
            {
                var transferers = new List<DXComponent>(64);
                foreach (var comp in game.ComponentsOfType(tEt))
                {
                    if (comp != null && comp.Entity != null && !comp.Entity.Disposed)
                        transferers.Add(comp);
                }
                if (transferers.Count > 1)
                    transferers.Sort((a, b) => GameBridge.CompareEntitiesDeterministically(a.Entity, b.Entity));
                for (int i = 0; i < transferers.Count; i++)
                    GameBridge.InvokeCEnergyTransfererUpdateGrid(transferers[i]);
            }
            if (tBldg != null)
            {
                var buildings = new List<DXEntity>(128);
                foreach (var ent in game.EntitiesOfType(tBldg))
                {
                    if (ent != null && !ent.Disposed)
                        buildings.Add(ent);
                }
                if (buildings.Count > 1)
                    buildings.Sort(GameBridge.CompareEntitiesDeterministically);
                for (int i = 0; i < buildings.Count; i++)
                    GameBridge.InvokeBuildingRefreshEnergy(buildings[i], true);
                for (int i = 0; i < buildings.Count; i++)
                    GameBridge.InvokeStructureUpdateEnabled(buildings[i]);
            }

            int nCells = level.NCells;
            Point lastValid = new Point(-10, -10);
            for (int i = 0; i < nCells; i++)
            {
                for (int j = 0; j < nCells; j++)
                {
                    if (energyGrid[i, j] == 0u)
                        continue;

                    var terr = terrain[i, j];
                    if (terr == ZXTerrainResourceType.Sea || terr == ZXTerrainResourceType.Mountain)
                    {
                        energyGrid[i, j] = 0u;
                        continue;
                    }

                    Point pt = new Point(i, j);
                    if (lastValid.DistanceManhattan(pt) <= 1)
                    {
                        lastValid = pt;
                        continue;
                    }

                    if (lastValid.X >= 0 && GameBridge.InvokeMapLayersHasPathBetween(__instance, pt, lastValid))
                    {
                        lastValid = pt;
                        continue;
                    }

                    // Multi-base fix: if not reachable from the previous island's cached cell,
                    // test reachability to ANY alive CommandCenter before pruning!
                    if (GameBridge.InvokeMapLayersHasPathToAnyCC(__instance, pt))
                    {
                        lastValid = pt;
                        continue;
                    }

                    energyGrid[i, j] = 0u;
                }
            }

            GameBridge.SetMapLayersEnergyInitDone(__instance, true);
            return false;
        }
    }

    /// <summary>
    /// Replaces ZXSystemMapLayers.#=zMxZdonzH58cOsMui6Q==() (incremental energy add/remove)
    /// so the post-add connectivity prune also falls back to #=zs1C_pLjYHbdEceIG1y8dYemfTA9s(cell)
    /// across disconnected base islands instead of wiping cells that cannot reach a single cached cell.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapLayers_UpdateEnergyCellsMultiBase
    {
        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            if (t == null || GameBridge.TypeZXEntity == null) return null;
            return AccessTools.Method(
                t,
                ObfuscatedSymbols.Method_MapLayers_UpdateEnergyCells,
                new[] { GameBridge.TypeZXEntity, typeof(Point), typeof(int), typeof(int), typeof(bool) });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance, object __0, Point __1, int __2, int __3, bool __4)
        {
            var ccs = GameBridge.GetAliveCommandCentersCached();
            if (!CoopSessionManager.IsMultiFactionActive && ccs.Count <= 1)
                return true;

            var terrain = GameBridge.GetTerrainResourceCells();
            var energyGrid = GameBridge.GetMapLayersEnergyGrid(__instance);
            var energyLock = GameBridge.GetMapLayersEnergyLock(__instance);
            if (terrain == null || energyGrid == null)
                return true;

            bool isCc = __0 != null && GameBridge.IsCommandCenter(__0 as DXEntity);
            int radius = __3 + 1;
            int nCells = DXWorldGrid.NCells;

            energyLock?.Enter();
            try
            {
                DXHelper_Circle.ProcessCells(__1, radius, nCells, (int x, int y) =>
                {
                    var terr = terrain[x, y];
                    if (terr == ZXTerrainResourceType.Sea || terr == ZXTerrainResourceType.Mountain || terr == ZXTerrainResourceType.Wood
                        || (!GameBridge.InvokeLevelSysIsCellPlayableForEnergy(x, y) && !isCc))
                    {
                        energyGrid[x, y] = 0u;
                    }
                    else
                    {
                        long next = (long)energyGrid[x, y] + __2;
                        if (next >= 0L)
                            energyGrid[x, y] = (uint)next;
                    }
                });

                if (GameBridge.GetMapLayersEnergyInitDone(__instance) && __2 > 0)
                {
                    Point lastValid = new Point(-10, -10);
                    DXHelper_Circle.ProcessCells(__1, radius, nCells, (int x, int y) =>
                    {
                        if (energyGrid[x, y] != 1u)
                            return;

                        Point pt = new Point(x, y);
                        if (lastValid.DistanceManhattan(pt) <= 1)
                        {
                            lastValid = pt;
                            return;
                        }

                        if (lastValid.X >= 0 && GameBridge.InvokeMapLayersHasPathBetween(__instance, pt, lastValid))
                        {
                            lastValid = pt;
                            return;
                        }

                        if (GameBridge.InvokeMapLayersHasPathToAnyCC(__instance, pt))
                        {
                            lastValid = pt;
                            return;
                        }

                        energyGrid[x, y] = 0u;
                    });
                }

                GameBridge.NotifyMapLayersEnergyChanged(__instance, __4);
            }
            finally
            {
                energyLock?.Exit();
            }

            return false;
        }
    }
}
