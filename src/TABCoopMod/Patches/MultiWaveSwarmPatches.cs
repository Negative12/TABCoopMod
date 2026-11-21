using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Sync;
using ZX;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Phase 4 M4 N-stream swarm dispatching, late-wave lag-control compression,
    /// deterministic merge/pass-through routing, and CUnitGenerator target tagging.
    ///
    /// Hook chain:
    ///   1. ZXLevelEvent.#=zUMCW0sQ=()        -> Pick N outward edge generators (Wave 1-2 100% split,
    ///                                           Wave 3+ 75% split / 20% 2-merge / 5% 3-merge,
    ///                                           Mini-waves round-robin rotation).
    ///   2. ZXLevelEvent.#=zHI6D76EGh68R()    -> Compress late/FinalSwarm T1/T2 overflow past per-stream
    ///                                           unit cap (850 periodic / 1400 FinalSwarm) into
    ///                                           tendency-weighted elites (7:1) + ZombieMutants (180:1)
    ///                                           (+1 ZombieGiant on FinalSwarm).
    ///   3. ZXLevelEvent.#=zrITPs$A=()        -> Open/close dispatch context around GenerationRequest queue.
    ///   4. CUnitGenerator.#=zERrxnH$byRKd()  -> Map request IDEvent -> targetPid & adjust GenerationSpeed
    ///                                           when merged or late-compressed.
    ///   5. CUnitGenerator.#=zzHXxhbjNa0Zf()  -> Tag newly created Zombies with targetPid before AddToScene().
    /// </summary>
    internal static class MultiWaveSwarmContext
    {
        internal sealed class PlannedDispatch
        {
            public readonly List<object> Generators = new List<object>();
            public readonly List<byte> TargetPids = new List<byte>();
            public readonly List<string> EdgeDirs = new List<string>();
            public int MergeFactor = 1;
            public bool IsMerged => MergeFactor > 1;
            public bool IsLateCompressed;
        }

        private static readonly object _sync = new object();
        private static readonly ConditionalWeakTable<object, PlannedDispatch> _plannedByEvent =
            new ConditionalWeakTable<object, PlannedDispatch>();

        [ThreadStatic]
        private static object _activeTriggerEvent;

        [ThreadStatic]
        private static PlannedDispatch _activeTriggerPlan;

        [ThreadStatic]
        private static Dictionary<object, int> _activeBeforeRequestCounts;

        [ThreadStatic]
        private static bool _activeLateCompressed;

        // Observable telemetry for automated M4 verification & logs
        public static int TotalStreamsDispatched { get; private set; }
        public static int TotalMergedWaves { get; private set; }
        public static int TotalCompressedWaves { get; private set; }
        public static int TotalCompressedMutants { get; private set; }
        public static int TotalCompressedGiants { get; private set; }
        public static string LastDispatchSummary { get; private set; } = string.Empty;

        public static bool IsActive =>
            CoopSessionManager.IsMultiFactionActive ||
            (FactionOwnershipTracker.IsInitialized && FactionOwnershipTracker.ActivePlayerCount >= 2);

        public static void Reset()
        {
            lock (_sync)
            {
                TotalStreamsDispatched = 0;
                TotalMergedWaves = 0;
                TotalCompressedWaves = 0;
                TotalCompressedMutants = 0;
                TotalCompressedGiants = 0;
                LastDispatchSummary = string.Empty;
            }
            _activeTriggerEvent = null;
            _activeTriggerPlan = null;
            _activeBeforeRequestCounts = null;
            _activeLateCompressed = false;
        }

        internal static void SetPlannedDispatch(object levelEvent, PlannedDispatch plan)
        {
            if (levelEvent == null || plan == null) return;
            lock (_sync)
            {
                _plannedByEvent.Remove(levelEvent);
                _plannedByEvent.Add(levelEvent, plan);
            }
            // Capture ONLY the first generator-selection inside #=zrITPs$A=() (line 1760),
            // never the end-of-method lookahead (line 1813) that prepares the next wave.
            if (ReferenceEquals(_activeTriggerEvent, levelEvent) && _activeTriggerPlan == null)
            {
                _activeTriggerPlan = plan;
            }
        }

        internal static PlannedDispatch GetPlannedDispatch(object levelEvent)
        {
            if (levelEvent == null) return null;
            lock (_sync)
            {
                return _plannedByEvent.TryGetValue(levelEvent, out var plan) ? plan : null;
            }
        }

        private static PropertyInfo _propUnitGenRequests;

        private static IList GetGeneratorRequests(object unitGen)
        {
            if (unitGen == null) return null;
            if (_propUnitGenRequests == null)
                _propUnitGenRequests = AccessTools.Property(unitGen.GetType(), "Requests");
            return _propUnitGenRequests?.GetValue(unitGen) as IList;
        }

        internal static void BeginTrigger(object levelEvent)
        {
            _activeTriggerEvent = levelEvent;
            _activeTriggerPlan = GetPlannedDispatch(levelEvent);
            if (_activeTriggerPlan == null && levelEvent != null)
            {
                bool isPeriodic = GameBridge.IsLevelEventPeriodicSwarm(levelEvent, out bool isFinal, out _, out bool attackCc, out _);
                if (isPeriodic && !isFinal && attackCc)
                {
                    // Clear any pre-MultiFaction cached single generator so #=zrITPs$A= invokes #=zUMCW0sQ=().
                    GameBridge.SetLevelEventCurrentGenerators(levelEvent, null);
                }
            }

            _activeBeforeRequestCounts = new Dictionary<object, int>();
            var allGens = GameBridge.GetAllEdgeUnitGenerators();
            if (allGens != null)
            {
                foreach (object g in allGens)
                {
                    if (g == null) continue;
                    var reqs = GetGeneratorRequests(g);
                    _activeBeforeRequestCounts[g] = reqs != null ? reqs.Count : 0;
                }
            }
            _activeLateCompressed = false;
        }

        internal static void MarkLateCompressed(int mutantsAdded, int giantsAdded)
        {
            _activeLateCompressed = true;
            if (_activeTriggerPlan != null)
                _activeTriggerPlan.IsLateCompressed = true;

            lock (_sync)
            {
                TotalCompressedWaves++;
                TotalCompressedMutants += mutantsAdded;
                TotalCompressedGiants += giantsAdded;
            }
        }

        internal static void EndTrigger(object levelEvent)
        {
            var plan = _activeTriggerPlan;
            var beforeCounts = _activeBeforeRequestCounts;

            try
            {
                if (beforeCounts != null)
                {
                    if (plan != null && plan.TargetPids.Count > 0 && plan.Generators.Count > 0)
                    {
                        var cursors = new Dictionary<object, int>(beforeCounts);
                        int limit = Math.Min(plan.Generators.Count, plan.TargetPids.Count);
                        for (int i = 0; i < limit; i++)
                        {
                            object g = plan.Generators[i];
                            if (g == null) continue;
                            var reqs = GetGeneratorRequests(g);
                            if (reqs == null) continue;

                            int cur = cursors.TryGetValue(g, out int c) ? c : 0;
                            if (cur >= reqs.Count) continue;

                            ulong firstEvId = GameBridge.GetGenerationRequestEventId(reqs[cur]);
                            byte targetPid = plan.TargetPids[i];
                            if (firstEvId != 0UL)
                                FactionOwnershipTracker.SetSubSwarmEventTarget(firstEvId, targetPid);

                            int speedCap = plan.MergeFactor > 1 ? Math.Max(2, 6 / plan.MergeFactor) : 12;
                            if (_activeLateCompressed) speedCap = Math.Min(speedCap, 4);

                            while (cur < reqs.Count && GameBridge.GetGenerationRequestEventId(reqs[cur]) == firstEvId)
                            {
                                if (speedCap < 12)
                                {
                                    int curSpeed = GameBridge.GetGenerationRequestSpeed(reqs[cur]);
                                    if (curSpeed > speedCap)
                                        GameBridge.SetGenerationRequestSpeed(reqs[cur], speedCap);
                                }
                                cur++;
                            }
                            cursors[g] = cur;
                        }
                    }
                    else if (_activeLateCompressed)
                    {
                        foreach (var kv in beforeCounts)
                        {
                            var reqs = GetGeneratorRequests(kv.Key);
                            if (reqs == null) continue;
                            for (int idx = kv.Value; idx < reqs.Count; idx++)
                            {
                                int curSpeed = GameBridge.GetGenerationRequestSpeed(reqs[idx]);
                                if (curSpeed > 4)
                                    GameBridge.SetGenerationRequestSpeed(reqs[idx], 4);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[M4Swarm] EndTrigger request tagging error: {ex.Message}");
            }

            if (plan != null && plan.TargetPids.Count > 0)
            {
                lock (_sync)
                {
                    TotalStreamsDispatched += plan.TargetPids.Count;
                    if (plan.IsMerged) TotalMergedWaves++;
                    LastDispatchSummary = $"streams={plan.TargetPids.Count} edges=[{string.Join(",", plan.EdgeDirs)}] pids=[{string.Join(",", plan.TargetPids)}] merge={plan.MergeFactor} compressed={_activeLateCompressed}";
                }
            }
            _activeTriggerEvent = null;
            _activeTriggerPlan = null;
            _activeBeforeRequestCounts = null;
            _activeLateCompressed = false;
        }

        internal static int GetLevelEventId(object levelEvent)
        {
            if (levelEvent == null) return 0;
            try
            {
                var prop = AccessTools.Property(levelEvent.GetType(), "ID");
                if (prop != null) return (int)prop.GetValue(levelEvent);
            }
            catch { }
            return 0;
        }

        internal static string PickNearestEdgeForCc(Point ccCell, int mapNCells, Dictionary<string, object> byDir, HashSet<string> forbidden = null)
        {
            int n = Math.Max(128, mapNCells);
            var candidates = new List<(string Dir, int Dist)>
            {
                ("N", ccCell.Y),
                ("E", n - 1 - ccCell.X),
                ("S", n - 1 - ccCell.Y),
                ("W", ccCell.X)
            };
            candidates.Sort((a, b) =>
            {
                int cmp = a.Dist.CompareTo(b.Dist);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Dir, b.Dir);
            });

            for (int i = 0; i < candidates.Count; i++)
            {
                string d = candidates[i].Dir;
                if (byDir.ContainsKey(d) && (forbidden == null || !forbidden.Contains(d)))
                    return d;
            }
            foreach (var kv in byDir)
            {
                if (forbidden == null || !forbidden.Contains(kv.Key))
                    return kv.Key;
            }
            foreach (var kv in byDir)
                return kv.Key;
            return null;
        }

        internal static PlannedDispatch BuildDispatchPlan(
            int eventId,
            int waveIdxZeroBased,
            bool isMiniWave,
            List<byte> alivePids,
            Dictionary<string, object> edgeGensByDir,
            int mapNCells,
            out List<object> orderedGenerators)
        {
            orderedGenerators = new List<object>();
            var plan = new PlannedDispatch();
            if (alivePids == null || alivePids.Count == 0 || edgeGensByDir == null || edgeGensByDir.Count == 0)
                return plan;

            // Resolve outward edge per alive player
            var outwardDirByPid = new Dictionary<byte, string>();
            var usedDistinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (byte pid in alivePids)
            {
                var ccs = FactionOwnershipTracker.GetAliveCommandCentersForPlayer(pid);
                Point ccCell = ccs.Count > 0 ? ccs[0].Cell : new Point(mapNCells / 2, mapNCells / 2);
                string dir = PickNearestEdgeForCc(ccCell, mapNCells, edgeGensByDir, usedDistinct);
                if (dir == null)
                    dir = PickNearestEdgeForCc(ccCell, mapNCells, edgeGensByDir, null);
                if (dir != null)
                {
                    outwardDirByPid[pid] = dir;
                    usedDistinct.Add(dir);
                }
            }

            if (alivePids.Count == 1 || isMiniWave)
            {
                byte pid = alivePids[(waveIdxZeroBased + Math.Abs(eventId)) % alivePids.Count];
                string dir = outwardDirByPid.TryGetValue(pid, out string d) ? d : null;
                if (dir != null && edgeGensByDir.TryGetValue(dir, out object gen))
                {
                    plan.TargetPids.Add(pid);
                    plan.EdgeDirs.Add(dir);
                    orderedGenerators.Add(gen);
                }
                return plan;
            }

            // Deterministic PRNG driven purely by MapSeed + EventID + WaveIndex
            int seed = FactionOwnershipTracker.ActiveMapSeed ^ (eventId * 7919) ^ (waveIdxZeroBased * 104729) ^ 0x51F04A17;
            var rng = new Random(seed);

            // Wave 1 & 2 (index 0, 1): 100% independent split generators
            // Wave 3+ (index >= 2): 75% distinct / 20% 2-wave merge / 5% 3-wave full merge (when 3 alive)
            int mergeMode = 0; // 0 = distinct, 1 = two-merge, 2 = three-merge
            if (waveIdxZeroBased >= 2)
            {
                int roll = rng.Next(100);
                if (alivePids.Count >= 3 && roll >= 95)
                    mergeMode = 2;
                else if (roll >= 75)
                    mergeMode = 1;
            }

            if (mergeMode == 2 && alivePids.Count >= 3)
            {
                byte anchorPid = alivePids[rng.Next(alivePids.Count)];
                string anchorDir = outwardDirByPid[anchorPid];
                object anchorGen = edgeGensByDir[anchorDir];
                plan.MergeFactor = alivePids.Count;
                for (int i = 0; i < alivePids.Count; i++)
                {
                    plan.TargetPids.Add(alivePids[i]);
                    plan.EdgeDirs.Add(anchorDir);
                    orderedGenerators.Add(anchorGen);
                }
                return plan;
            }

            if (mergeMode == 1 && alivePids.Count >= 2)
            {
                int idxA = rng.Next(alivePids.Count);
                int idxB = (idxA + 1 + rng.Next(alivePids.Count - 1)) % alivePids.Count;
                byte pidA = alivePids[idxA];
                byte pidB = alivePids[idxB];
                string mergeDir = outwardDirByPid[pidA];
                object mergeGen = edgeGensByDir[mergeDir];
                plan.MergeFactor = 2;

                for (int i = 0; i < alivePids.Count; i++)
                {
                    byte pid = alivePids[i];
                    string dir = (pid == pidB || pid == pidA) ? mergeDir : outwardDirByPid[pid];
                    object gen = (pid == pidB || pid == pidA) ? mergeGen : edgeGensByDir[dir];
                    plan.TargetPids.Add(pid);
                    plan.EdgeDirs.Add(dir);
                    orderedGenerators.Add(gen);
                }
                return plan;
            }

            // Distinct streams for all alive players
            for (int i = 0; i < alivePids.Count; i++)
            {
                byte pid = alivePids[i];
                if (!outwardDirByPid.TryGetValue(pid, out string dir)) continue;
                plan.TargetPids.Add(pid);
                plan.EdgeDirs.Add(dir);
                orderedGenerators.Add(edgeGensByDir[dir]);
            }
            return plan;
        }

        internal static Dictionary<string, object> RunSwarmAudit(bool triggerLive)
        {
            var res = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["multiFactionActive"] = IsActive,
                ["spawnMode"] = FactionOwnershipTracker.ActiveSpawnMode.ToString(),
                ["mutationTendency"] = FactionOwnershipTracker.ActiveMutationTendency.ToString()
            };

            var allEdgeGens = GameBridge.GetAllEdgeUnitGenerators();
            var byDir = new Dictionary<string, object>(StringComparer.Ordinal);
            if (allEdgeGens != null)
            {
                foreach (object g in allEdgeGens)
                {
                    string name = GameBridge.GetUnitGeneratorName(g);
                    if (name == "N" || name == "S" || name == "E" || name == "W")
                        byDir[name] = g;
                }
            }
            res["edgeGeneratorCount"] = byDir.Count;

            int playerCount = Math.Max(2, (int)FactionOwnershipTracker.ActivePlayerCount);
            var alivePids = new List<byte>();
            for (byte pid = 1; pid <= playerCount; pid++)
            {
                if (FactionOwnershipTracker.GetAliveCommandCentersForPlayer(pid).Count > 0)
                    alivePids.Add(pid);
            }
            if (alivePids.Count == 0) alivePids.Add(1);
            res["alivePlayers"] = alivePids.Count;

            int mapNCells = GameBridge.GridNCells > 0 ? GameBridge.GridNCells : 360;
            int mergedIn25 = 0;
            bool w1Distinct = false;
            bool w2Distinct = false;
            string w1Edges = "";
            string w2Edges = "";
            if (byDir.Count > 0)
            {
                for (int w = 0; w < 25; w++)
                {
                    var plan = BuildDispatchPlan(1000 + (w * 17), w, false, alivePids, byDir, mapNCells, out _);
                    int uniqueDirs = new HashSet<string>(plan.EdgeDirs, StringComparer.Ordinal).Count;
                    bool isSplit = uniqueDirs == Math.Min(alivePids.Count, byDir.Count) && !plan.IsMerged;
                    if (w == 0) { w1Distinct = isSplit; w1Edges = string.Join(",", plan.EdgeDirs); }
                    if (w == 1) { w2Distinct = isSplit; w2Edges = string.Join(",", plan.EdgeDirs); }
                    if (w >= 2 && plan.IsMerged) mergedIn25++;
                }
            }
            res["wave1Distinct"] = w1Distinct;
            res["wave2Distinct"] = w2Distinct;
            res["wave1Edges"] = w1Edges;
            res["wave2Edges"] = w2Edges;
            res["mergedWavesAcross23LateTrials"] = mergedIn25;

            var events = GameBridge.GetCurrentLevelEvents();
            object periodicEv = null;
            object fallbackPeriodicEv = null;
            object finalEv = null;
            int totalEvents = events?.Count ?? 0;
            if (events != null)
            {
                foreach (object ev in events)
                {
                    if (ev == null) continue;
                    bool isPeriodic = GameBridge.IsLevelEventPeriodicSwarm(ev, out bool isFinal, out bool showCountdown, out bool attackCc, out int maxReps);
                    if (isFinal && finalEv == null) finalEv = ev;
                    else if (isPeriodic && attackCc)
                    {
                        if (fallbackPeriodicEv == null) fallbackPeriodicEv = ev;
                        if (showCountdown && maxReps > 0 && maxReps <= 5 && periodicEv == null)
                            periodicEv = ev;
                    }
                }
            }
            if (periodicEv == null) periodicEv = fallbackPeriodicEv;
            res["levelEventsCount"] = totalEvents;
            res["foundPeriodicSwarm"] = periodicEv != null;
            res["foundFinalSwarm"] = finalEv != null;

            if (triggerLive)
            {
                var tEvent = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXLevelEvent);
                var mTrigger = tEvent != null ? AccessTools.Method(tEvent, ObfuscatedSymbols.Method_LevelEvent_Trigger, Type.EmptyTypes) : null;
                var mBuild = tEvent != null ? AccessTools.Method(tEvent, ObfuscatedSymbols.Method_LevelEvent_BuildEntityCounts, Type.EmptyTypes) : null;
                int beforeStreams = TotalStreamsDispatched;
                int beforeCompressed = TotalCompressedWaves;

                int periodicBeforeDict = 0;
                if (periodicEv != null && mBuild != null)
                {
                    var pDict = mBuild.Invoke(periodicEv, null) as Dictionary<DXEntityTemplate, int>;
                    if (pDict != null) foreach (var kv in pDict) periodicBeforeDict += kv.Value;
                }
                if (periodicEv != null && mTrigger != null)
                {
                    mTrigger.Invoke(periodicEv, null);
                }

                // Also test FinalSwarm compression + trigger if FinalSwarm exists on map
                int finalRawUnits = 0;
                if (finalEv != null && mBuild != null)
                {
                    var dict = mBuild.Invoke(finalEv, null) as Dictionary<DXEntityTemplate, int>;
                    if (dict != null)
                    {
                        foreach (var kv in dict) finalRawUnits += kv.Value;
                    }
                    if (mTrigger != null) mTrigger.Invoke(finalEv, null);
                }

                // Count active requests queued on all edge generators
                int queuedRequests = 0;
                int queuedTotalUnits = 0;
                var taggedTargetPids = new HashSet<byte>();
                var seenEvIds = new HashSet<ulong>();
                if (allEdgeGens != null)
                {
                    var propReqs = allEdgeGens.Count > 0 ? AccessTools.Property(allEdgeGens[0].GetType(), "Requests") : null;
                    foreach (object g in allEdgeGens)
                    {
                        if (propReqs?.GetValue(g) is IList reqs)
                        {
                            queuedRequests += reqs.Count;
                            foreach (object r in reqs)
                            {
                                ulong eid = GameBridge.GetGenerationRequestEventId(r);
                                queuedTotalUnits += GameBridge.GetGenerationRequestCount(r);
                                if (eid != 0UL) seenEvIds.Add(eid);
                                byte p = FactionOwnershipTracker.GetSubSwarmEventTarget(eid);
                                if (p != 0) taggedTargetPids.Add(p);
                            }
                        }
                    }
                }

                res["livePeriodicDeltaStreams"] = TotalStreamsDispatched - beforeStreams;
                res["liveCompressedDelta"] = TotalCompressedWaves - beforeCompressed;
                res["periodicDictUnits"] = periodicBeforeDict;
                res["finalSwarmDictUnits"] = finalRawUnits;
                res["queuedGeneratorRequests"] = queuedRequests;
                res["queuedTotalUnits"] = queuedTotalUnits;
                res["queuedUniqueEventIds"] = seenEvIds.Count;
                res["taggedTargetPlayerCount"] = taggedTargetPids.Count;
            }

            res["totalStreamsDispatched"] = TotalStreamsDispatched;
            res["totalMergedWaves"] = TotalMergedWaves;
            res["totalCompressedWaves"] = TotalCompressedWaves;
            res["totalCompressedMutants"] = TotalCompressedMutants;
            res["totalCompressedGiants"] = TotalCompressedGiants;
            res["lastDispatchSummary"] = LastDispatchSummary;
            return res;
        }
    }

    [HarmonyPatch]
    internal static class LevelEventPickGeneratorsPatch
    {
        static MethodBase TargetMethod()
        {
            var type = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXLevelEvent);
            return type != null ? AccessTools.Method(type, ObfuscatedSymbols.Method_LevelEvent_PickGenerators) : null;
        }

        static void Postfix(object __instance, ref object __result)
        {
            if (!MultiWaveSwarmContext.IsActive || __instance == null) return;

            try
            {
                bool isPeriodic = GameBridge.IsLevelEventPeriodicSwarm(__instance, out bool isFinalSwarm, out _, out bool attackCC, out int maxReps);
                if (!isPeriodic || isFinalSwarm || !attackCC) return;

                var allEdgeGens = GameBridge.GetAllEdgeUnitGenerators();
                if (allEdgeGens == null || allEdgeGens.Count == 0) return;

                var byDir = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (object g in allEdgeGens)
                {
                    string name = GameBridge.GetUnitGeneratorName(g);
                    if (name == "N" || name == "S" || name == "E" || name == "W")
                        byDir[name] = g;
                }
                if (byDir.Count == 0) return;

                int playerCount = Math.Max(2, (int)FactionOwnershipTracker.ActivePlayerCount);
                var alivePids = new List<byte>(playerCount);
                for (byte pid = 1; pid <= playerCount; pid++)
                {
                    if (FactionOwnershipTracker.GetAliveCommandCentersForPlayer(pid).Count > 0)
                        alivePids.Add(pid);
                }
                if (alivePids.Count == 0)
                    alivePids.Add(1);

                int nTriggered = GameBridge.GetLevelEventNTimesTriggered(__instance);
                int eventId = MultiWaveSwarmContext.GetLevelEventId(__instance);
                bool isMiniWave = maxReps > 5;
                int mapNCells = GameBridge.GridNCells > 0 ? GameBridge.GridNCells : 360;

                var plan = MultiWaveSwarmContext.BuildDispatchPlan(
                    eventId,
                    nTriggered,
                    isMiniWave,
                    alivePids,
                    byDir,
                    mapNCells,
                    out var orderedGenerators);

                if (orderedGenerators != null && orderedGenerators.Count > 0)
                {
                    plan.Generators.AddRange(orderedGenerators);
                    IList typed = GameBridge.CreateTypedUnitGeneratorList(orderedGenerators);
                    if (typed != null && typed.Count > 0)
                    {
                        __result = typed;
                        MultiWaveSwarmContext.SetPlannedDispatch(__instance, plan);
                        ModLogger.Verbose($"[M4Swarm] Event#{eventId} waveIdx={nTriggered} (mini={isMiniWave}): edges=[{string.Join(",", plan.EdgeDirs)}] pids=[{string.Join(",", plan.TargetPids)}] merge={plan.MergeFactor}");
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[M4Swarm] LevelEventPickGeneratorsPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    internal static class LevelEventBuildEntityCountsPatch
    {
        static MethodBase TargetMethod()
        {
            var type = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXLevelEvent);
            return type != null ? AccessTools.Method(type, ObfuscatedSymbols.Method_LevelEvent_BuildEntityCounts) : null;
        }

        static void Postfix(object __instance, Dictionary<DXEntityTemplate, int> __result)
        {
            if (!MultiWaveSwarmContext.IsActive || __instance == null || __result == null || __result.Count == 0)
                return;

            try
            {
                GameBridge.IsLevelEventPeriodicSwarm(__instance, out bool isFinalSwarm, out _, out bool attackCC, out _);
                if (!attackCC) return;

                bool isLateOrFinal = isFinalSwarm || GameBridge.ColonyHours >= 55f * 24f;
                if (!isLateOrFinal) return;

                int nTriggered = Math.Max(1, GameBridge.GetLevelEventNTimesTriggered(__instance));
                GameBridge.GetLevelEventRepetitionScale(__instance, out float factorPerRep, out float maxFactor);
                float repNum = factorPerRep != 0f ? (nTriggered - 1) * factorPerRep : 0f;
                if (maxFactor > 0f) repNum = Math.Min(maxFactor, repNum);
                float repMult = Math.Max(0.1f, 1f + repNum);

                int cap = isFinalSwarm ? 1400 : 850;
                int totalEffective = 0;
                var compressibleKeys = new List<DXEntityTemplate>();

                foreach (var kv in __result)
                {
                    if (kv.Key == null || kv.Value <= 0) continue;
                    int eff = Math.Max(1, (int)Math.Round(kv.Value * repMult));
                    totalEffective += eff;
                    if (IsLowTierSwarmTemplate(kv.Key))
                        compressibleKeys.Add(kv.Key);
                }

                if (totalEffective <= cap || compressibleKeys.Count == 0)
                    return;

                int excess = totalEffective - cap;
                int totalLowEff = 0;
                foreach (var tpl in compressibleKeys)
                    totalLowEff += Math.Max(1, (int)Math.Round(__result[tpl] * repMult));

                if (totalLowEff <= 0) return;

                int actualRemovedEff = 0;
                int remainingExcess = excess;
                for (int i = 0; i < compressibleKeys.Count && remainingExcess > 0; i++)
                {
                    var tpl = compressibleKeys[i];
                    int origRaw = __result[tpl];
                    int origEff = Math.Max(1, (int)Math.Round(origRaw * repMult));
                    int shareEff = (i == compressibleKeys.Count - 1)
                        ? remainingExcess
                        : (int)Math.Round(excess * (origEff / (double)totalLowEff));
                    shareEff = Math.Min(origEff, Math.Max(0, shareEff));

                    int rawReduce = Math.Min(origRaw, (int)Math.Ceiling(shareEff / repMult));
                    if (rawReduce <= 0 && shareEff > 0 && origRaw > 0)
                        rawReduce = 1;

                    if (rawReduce > 0)
                    {
                        int effRemoved = (int)Math.Round(rawReduce * repMult);
                        actualRemovedEff += effRemoved;
                        remainingExcess = Math.Max(0, remainingExcess - effRemoved);

                        int newRaw = origRaw - rawReduce;
                        if (newRaw <= 0) __result.Remove(tpl);
                        else __result[tpl] = newRaw;
                    }
                }

                if (actualRemovedEff <= 0) return;

                // Defense-aware differential condensation for 75% overflow:
                // Chubby 8:1 (wall tank, survives AoE), Venom 5:1 (ranged AoE over walls), Harpy 7:1 (wall-jumper);
                // 25% at 180:1 into 1-3 (4 for SwarmHunter) ZombieMutants.
                var tendency = FactionOwnershipTracker.ActiveMutationTendency;
                GetTendencyWeights(tendency, out int wChubby, out int wVenom, out int wHarpy);
                int weightSum = Math.Max(1, wChubby + wVenom + wHarpy);

                double eliteBudget = actualRemovedEff * 0.75;
                int chubbyEff = Math.Max(0, (int)Math.Round((eliteBudget * (wChubby / (double)weightSum)) / 8.0));
                int venomEff = Math.Max(0, (int)Math.Round((eliteBudget * (wVenom / (double)weightSum)) / 5.0));
                int harpyEff = Math.Max(0, (int)Math.Round((eliteBudget * (wHarpy / (double)weightSum)) / 7.0));
                int elitesEff = chubbyEff + venomEff + harpyEff;
                if (elitesEff == 0 && eliteBudget > 0)
                {
                    venomEff = 1;
                    elitesEff = 1;
                }

                AddTemplateEffective(__result, GameBridge.GetEntityTemplateByType(GameBridge.TypeZombieStrongA), chubbyEff, repMult);
                AddTemplateEffective(__result, GameBridge.GetEntityTemplateByType(GameBridge.TypeZombieVenom), venomEff, repMult);
                AddTemplateEffective(__result, GameBridge.GetEntityTemplateByType(GameBridge.TypeZombieHarpy), harpyEff, repMult);

                float mutantBoost = tendency == MutationTendency.SwarmHunter ? 1.5f : 1.0f;
                int maxMutants = tendency == MutationTendency.SwarmHunter ? 4 : 3;
                int mutantsEff = Math.Max(1, Math.Min(maxMutants, (int)Math.Round(actualRemovedEff * 0.25 * mutantBoost / 180.0)));
                AddTemplateEffective(__result, GameBridge.GetEntityTemplateByType(GameBridge.TypeZombieMutant), mutantsEff, repMult);

                int giantsAdded = 0;
                if (isFinalSwarm)
                {
                    var giantTpl = GameBridge.GetEntityTemplateByType(GameBridge.TypeZombieGiant);
                    if (giantTpl != null)
                    {
                        AddTemplateEffective(__result, giantTpl, 1, repMult);
                        giantsAdded = 1;
                    }
                }

                MultiWaveSwarmContext.MarkLateCompressed(mutantsEff, giantsAdded);
                ModLogger.Info($"[M4Swarm] Compressed overflow: totalEff={totalEffective} cap={cap} removedEff={actualRemovedEff} -> elites={elitesEff} (C={chubbyEff}/V={venomEff}/H={harpyEff}) mutants={mutantsEff} giants={giantsAdded} tendency={tendency}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[M4Swarm] LevelEventBuildEntityCountsPatch error: {ex.Message}");
            }
        }

        private static bool IsLowTierSwarmTemplate(DXEntityTemplate tpl)
        {
            string id = tpl?.Entity?.GetType()?.FullName ?? string.Empty;
            return id == ObfuscatedSymbols.Type_ZombieWeakA
                || id == ObfuscatedSymbols.Type_ZombieWorkerA
                || id == ObfuscatedSymbols.Type_ZombieMediumA
                || id == ObfuscatedSymbols.Type_ZombieDressedA;
        }

        private static void GetTendencyWeights(MutationTendency tendency, out int chubby, out int venom, out int harpy)
        {
            switch (tendency)
            {
                case MutationTendency.HeavyArmor:
                    chubby = 70; venom = 15; harpy = 15;
                    break;
                case MutationTendency.Caustic:
                    chubby = 20; venom = 65; harpy = 15;
                    break;
                case MutationTendency.SwarmHunter:
                    chubby = 20; venom = 20; harpy = 60;
                    break;
                default:
                    chubby = 45; venom = 35; harpy = 20;
                    break;
            }
        }

        private static void AddTemplateEffective(Dictionary<DXEntityTemplate, int> dict, DXEntityTemplate tpl, int effCount, float repMult)
        {
            if (dict == null || tpl == null || effCount <= 0) return;
            int rawAdd = Math.Max(1, (int)Math.Round(effCount / repMult));
            if (dict.TryGetValue(tpl, out int existing))
                dict[tpl] = existing + rawAdd;
            else
                dict[tpl] = rawAdd;
        }
    }

    [HarmonyPatch]
    internal static class LevelEventTriggerPatch
    {
        static MethodBase TargetMethod()
        {
            var type = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXLevelEvent);
            return type != null ? AccessTools.Method(type, ObfuscatedSymbols.Method_LevelEvent_Trigger) : null;
        }

        static void Prefix(object __instance)
        {
            if (!MultiWaveSwarmContext.IsActive || __instance == null) return;
            MultiWaveSwarmContext.BeginTrigger(__instance);
        }

        static void Postfix(object __instance)
        {
            if (!MultiWaveSwarmContext.IsActive || __instance == null) return;
            MultiWaveSwarmContext.EndTrigger(__instance);
        }
    }

    [HarmonyPatch]
    internal static class UnitGeneratorUpdatePatch
    {
        private static FieldInfo _fieldSpawnBuffer;
        private static PropertyInfo _propRequests;

        internal struct ReqSnapshot
        {
            public object Req;
            public ulong EventId;
            public int BeforeCount;
        }

        internal struct UpdateState
        {
            public int PrevBufCount;
            public List<ReqSnapshot> Snapshots;
        }

        static MethodBase TargetMethod()
        {
            var type = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_CUnitGenerator);
            if (type == null) return null;
            _fieldSpawnBuffer = AccessTools.Field(type, "#=zPC3qgqTa864Y");
            _propRequests = AccessTools.Property(type, "Requests");
            return AccessTools.Method(type, ObfuscatedSymbols.Method_CUnitGenerator_Update);
        }

        static void Prefix(object __instance, out UpdateState __state)
        {
            __state = default(UpdateState);
            if (!MultiWaveSwarmContext.IsActive || __instance == null || _fieldSpawnBuffer == null || _propRequests == null)
                return;

            try
            {
                var buf = _fieldSpawnBuffer.GetValue(__instance) as List<DXEntity>;
                var reqs = _propRequests.GetValue(__instance) as IList;
                if (buf == null || reqs == null || reqs.Count == 0)
                    return;

                var snaps = new List<ReqSnapshot>(reqs.Count);
                for (int i = reqs.Count - 1; i >= 0; i--)
                {
                    object r = reqs[i];
                    if (r == null) continue;
                    ulong evId = GameBridge.GetGenerationRequestEventId(r);
                    int cnt = GameBridge.GetGenerationRequestCount(r);
                    snaps.Add(new ReqSnapshot { Req = r, EventId = evId, BeforeCount = cnt });
                }

                __state = new UpdateState
                {
                    PrevBufCount = buf.Count,
                    Snapshots = snaps
                };
            }
            catch { }
        }

        static void Postfix(object __instance, UpdateState __state)
        {
            if (__state.Snapshots == null || __instance == null || _fieldSpawnBuffer == null)
                return;

            try
            {
                var buf = _fieldSpawnBuffer.GetValue(__instance) as List<DXEntity>;
                if (buf == null || buf.Count <= __state.PrevBufCount)
                    return;

                int cursor = __state.PrevBufCount;
                for (int i = 0; i < __state.Snapshots.Count && cursor < buf.Count; i++)
                {
                    var snap = __state.Snapshots[i];
                    int afterCount = GameBridge.GetGenerationRequestCount(snap.Req);
                    int spawned = snap.BeforeCount - afterCount;
                    if (spawned <= 0) continue;

                    byte targetPid = FactionOwnershipTracker.GetSubSwarmEventTarget(snap.EventId);
                    int end = Math.Min(buf.Count, cursor + spawned);
                    if (targetPid != 0)
                    {
                        for (int k = cursor; k < end; k++)
                        {
                            var z = buf[k];
                            if (z != null && z.ID != 0UL)
                                FactionOwnershipTracker.SetZombieSwarmTarget(z.ID, targetPid);
                        }
                    }
                    cursor = end;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[M4Swarm] UnitGeneratorUpdatePatch Postfix error: {ex.Message}");
            }
        }
    }
}
