using System;
using System.Reflection;
using System.Threading;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Controls deterministic simulation stepping, GUID bifurcation (Synced Entity IDs vs Local UI IDs),
    /// and deterministic Mersenne Twister (DXRandom) seeding.
    /// </summary>
    public static class LockstepClock
    {
        public const double MsPerLogicTick = 33.333333333333336; // 30 Hz IA Update
        public const double MsPerPhysicsTick = 83.33333333333333; // 12 Hz Physics Update

        /// <summary>
        /// True when a multiplayer or deterministic replay session is active.
        /// </summary>
        public static bool IsDeterministicModeActive { get; private set; }

        /// <summary>
        /// True while the main thread is executing inside a synchronized logic tick or replaying a synced command.
        /// Used by DXGuid.New() patch to separate gameplay entity IDs from local UI object IDs.
        /// </summary>
        [ThreadStatic]
        public static bool InDeterministicContext;

        /// <summary>
        /// Cross-thread flag set while a logic tick is executing. The engine runs entity updates
        /// inside a Task on a worker thread, so [ThreadStatic] InDeterministicContext cannot reach
        /// DXGuid.New()/DXRandom there; this global flag does. Safe because the main thread blocks
        /// in WaitForGameLogic until the tick finishes.
        /// </summary>
        public static volatile bool LogicTickActive;

        /// <summary>
        /// True while a save/level is being loaded. During load, DXRandom.Global draws must use the
        /// seeded shared instance. NOTE: DXGuid is deliberately NOT routed through this flag — doing
        /// so gave load-time temp/UI objects synced low IDs, polluting the entity set with a
        /// process-varying count. Load-time GUIDs stay local (high bit) and excluded from hashing.
        /// </summary>
        public static volatile bool LoadingLevel;

        /// <summary>
        /// Controls whether the fixed-step clock is allowed to advance. Default: always ready
        /// (single-process deterministic replay). Network lockstep swaps in a gated implementation.
        /// </summary>
        public static ITickGate TickGate { get; set; } = new AlwaysReadyTickGate();

        public static int CurrentSimTick => DXGame.Current?.IAIterationInt ?? 0;

        private static long _syncedEntityIdCounter = 100000L;
        private static long _localUiIdCounter = 1L;
        private static int _syncedRandomSeed = 1337;
        private static DXRandom _syncedGlobalRandom;
        private static DXRandom _auxGlobalRandom;
        private static DXRandom _renderGlobalRandom;

        /// <summary>True while DXGame.UpdatePhysics() is on the stack (physics callbacks run outside LogicTickActive).</summary>
        public static volatile bool InPhysicsContext;

        /// <summary>
        /// Seed stashed by the session/verifier before LoadSaveFileAndEnter. The counter must not be
        /// calibrated until the async loader finishes (see OnLevelLoadComplete), because at load start
        /// DXGame.Current.Entities still holds the OLD level and the save's GameTime is not restored.
        /// </summary>
        public static int PendingLoadSeed { get; private set; } = 1337;
        public static bool HasPendingPostLoadCalibration { get; private set; }

        public static long CurrentSyncedEntityIdCounter => _syncedEntityIdCounter;

        public static void EnableDeterministicSandbox(int initialSeed, long startingEntityIdCounter = 100000L)
        {
            _syncedRandomSeed = initialSeed;
            _syncedEntityIdCounter = startingEntityIdCounter;
            _syncedGlobalRandom = new DXRandom(initialSeed);
            _auxGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x5EEDBEEF));
            _renderGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x1234ABCD));

            // The parameterless-ctor seed counter must restart together with the shared RNG, otherwise
            // a run that allocated a different number of DXRandom instances before syncing starts
            // would get a permanently shifted seed sequence.
            DXRandomCtorPatch.ResetCounter();

            // Enable the engine's built-in single-threaded entity update & synchronous physics flag!
            DXGame.IsIADeterministic = true;
            IsDeterministicModeActive = true;
            SetEngineMultiplayerMode(true);

            // Unload background async physics/time threads if they were already running
            DXGame.Current?.UnloadPhysics();

            ModLogger.Info($"[LockstepClock] Deterministic sandbox ENABLED (Seed={initialSeed}, StartEntityId={startingEntityIdCounter}, IsIADeterministic={DXGame.IsIADeterministic}).");
        }

        public static void DisableDeterministicSandbox()
        {
            IsDeterministicModeActive = false;
            InDeterministicContext = false;
            LogicTickActive = false;
            InPhysicsContext = false;
            LoadingLevel = false;
            HasPendingPostLoadCalibration = false;
            TickGate = new AlwaysReadyTickGate();
            DXGame.IsIADeterministic = false;
            SetEngineMultiplayerMode(false);
            ModLogger.Info("[LockstepClock] Deterministic sandbox DISABLED.");
        }

        private static MethodInfo _setPlayerModeMethod;

        /// <summary>
        /// Switches ZXGame's ZXPlayerModeType. Multiplayer makes CMovable compute paths synchronously
        /// on the logic thread (DXWorldGrid.FindPath) instead of the nondeterministic background queue.
        /// </summary>
        public static void SetEngineMultiplayerMode(bool multiplayer)
        {
            try
            {
                if (_setPlayerModeMethod == null)
                {
                    _setPlayerModeMethod = AccessTools.Method(GameBridge.TypeZXGame,
                        ObfuscatedSymbols.Method_ZXGame_SetPlayerMode);
                }
                if (_setPlayerModeMethod == null) return;

                var paramType = _setPlayerModeMethod.GetParameters()[0].ParameterType;
                object mode = Enum.ToObject(paramType, multiplayer ? 1 : 0);
                _setPlayerModeMethod.Invoke(null, new[] { mode });
                ModLogger.Info($"[LockstepClock] ZXPlayerModeType set to {(multiplayer ? "Multiplayer" : "Singleplayer")}.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] SetEngineMultiplayerMode failed: {ex.Message}");
            }
        }

        public static ulong NextSyncedGuid()
        {
            long next = Interlocked.Increment(ref _syncedEntityIdCounter);
            return (ulong)next;
        }

        public static ulong NextLocalUiGuid()
        {
            long next = Interlocked.Increment(ref _localUiIdCounter);
            return 0x8000000000000000UL | (ulong)next;
        }

        public static DXRandom GetSyncedGlobalRandom()
        {
            if (_syncedGlobalRandom == null)
            {
                _syncedGlobalRandom = new DXRandom(_syncedRandomSeed);
            }
            return _syncedGlobalRandom;
        }

        /// <summary>
        /// Fixed-seed RNG for Global draws made OUTSIDE the logic tick (physics callbacks, scene
        /// updates). DXRandom.Global is [ThreadStatic] and seeded from new Random(), so without this
        /// those draws are nondeterministic between processes. Kept separate from the logic stream so
        /// render-frame counts can never shift the simulation's random sequence.
        /// </summary>
        public static DXRandom GetAuxGlobalRandom()
        {
            if (_auxGlobalRandom == null)
            {
                _auxGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x5EEDBEEF));
            }
            return _auxGlobalRandom;
        }

        /// <summary>
        /// Separate fixed-seed stream for pure rendering-phase draws. Keeping it apart from the
        /// physics stream means frame/viewport-dependent visual randomness can never shift the
        /// random sequence consumed by deterministic simulation callbacks.
        /// </summary>
        public static DXRandom GetRenderGlobalRandom()
        {
            if (_renderGlobalRandom == null)
            {
                _renderGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x1234ABCD));
            }
            return _renderGlobalRandom;
        }

        public static int SyncedRandomSeed => _syncedRandomSeed;

        private static FieldInfo _behaviourGlobalRandomField;

        /// <summary>
        /// DXBehaviour&lt;TData&gt;.GlobalRandom is a SEPARATE static RNG used by the AI behaviour trees
        /// (zombie roaming etc.). Its seed is captured at type-init from DXRandom.Global, so it is
        /// nondeterministic across processes and is NOT covered by the DXRandom.Global patch.
        /// Re-seat it to a fixed-seed DXRandom so behaviour-driven movement is reproducible.
        /// </summary>
        public static void ResetBehaviourRandom(int seed)
        {
            try
            {
                Type bData = Core.GameBridge.GameAssembly?.GetType("ZX.Behaviours.BData");
                if (bData == null) return;
                Type closed = typeof(DXBehaviour<>).MakeGenericType(bData);
                if (_behaviourGlobalRandomField == null)
                    _behaviourGlobalRandomField = closed.GetField("_GlobalRandom",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (_behaviourGlobalRandomField == null) return;
                _behaviourGlobalRandomField.SetValue(null, new DXRandom(seed));
                ModLogger.Info($"[LockstepClock] Reset DXBehaviour<{bData.Name}>.GlobalRandom (seed={seed}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] ResetBehaviourRandom failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans all currently loaded entities in the level and sets _syncedEntityIdCounter
        /// strictly above the maximum existing non-UI entity ID so newly spawned entities never collide.
        /// </summary>
        public static void CalibrateEntityIdCounterFromLoadedLevel(int seed)
        {
            long maxId = 100000L;
            if (DXGame.Current?.Entities != null)
            {
                foreach (var ent in DXGame.Current.Entities)
                {
                    if (ent != null && (ent.ID & 0x8000000000000000UL) == 0)
                    {
                        if ((long)ent.ID > maxId)
                            maxId = (long)ent.ID;
                    }
                }
            }
            // Stay clear of long.MaxValue: saved IDs are already close to 2^63-1 on late saves.
            long start = maxId > long.MaxValue - 8192L ? long.MaxValue - 8192L : maxId + 1000L;
            EnableDeterministicSandbox(seed, start);
        }

        /// <summary>
        /// Called before LoadSaveFileAndEnter. The actual calibration is deferred to the level-load
        /// completion postfix, when the async loader has restored every entity and GameTime.
        /// </summary>
        public static void RequestPostLoadCalibration(int seed)
        {
            PendingLoadSeed = seed;
            HasPendingPostLoadCalibration = true;
            LoadingLevel = true;
        }

        /// <summary>
        /// Runs on the main thread from ZXSystemGameLevel.#=zwK_pUIeRBpbp Postfix — the first moment
        /// the loaded level is fully restored. Calibrates synced entity IDs, sorts update order,
        /// seeds level RNG and zero-phases the logic/physics clocks.
        /// </summary>
        public static void OnLevelLoadComplete()
        {
            if (!IsDeterministicModeActive)
            {
                LoadingLevel = false;
                return;
            }

            try
            {
                int seed = HasPendingPostLoadCalibration ? PendingLoadSeed : _syncedRandomSeed;
                HasPendingPostLoadCalibration = false;

                CalibrateEntityIdCounterFromLoadedLevel(seed);
                LoadingLevel = false;

                GameBridge.SortEntitiesById();
                GameBridge.SeedLevelRandom(seed);
                ResetBehaviourRandom(seed);
                GameBridge.AlignSimulationClocks();

                ModLogger.Info($"[LockstepClock] Level load complete: ID counter -> {_syncedEntityIdCounter}, seed={seed}, clocks aligned.");
            }
            catch (Exception ex)
            {
                LoadingLevel = false;
                ModLogger.Error("[LockstepClock] OnLevelLoadComplete failed", ex);
            }
        }
    }

    /// <summary>
    /// Patches DXGuid.New(), DXRandom.Global, and DXGame.UpdateGameLogic / LoadPhysics
    /// to enforce deterministic execution when LockstepClock.IsDeterministicModeActive is true.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicSandboxPatches
    {
        // 1. Patch DXGuid.New() for dual-track ID generation
        [HarmonyPatch(typeof(DXGuid), nameof(DXGuid.New))]
        [HarmonyPrefix]
        public static bool DXGuid_New_Prefix(ref ulong __result)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return true;

            // During (async) save loading every freshly generated ID belongs to transient load-time
            // objects: the synced counter is not calibrated yet and low IDs would collide with the
            // save's own entity IDs. Keep them local (high bit) and out of hashing.
            if (LockstepClock.LoadingLevel)
            {
                __result = LockstepClock.NextLocalUiGuid();
                return false;
            }

            if (LockstepClock.InDeterministicContext || LockstepClock.LogicTickActive)
            {
                __result = LockstepClock.NextSyncedGuid();
            }
            else
            {
                __result = LockstepClock.NextLocalUiGuid();
            }
            return false;
        }

        // 2. Patch DXRandom.Global getter to return deterministic PRNG in deterministic context
        [HarmonyPatch(typeof(DXRandom), nameof(DXRandom.Global), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool DXRandom_Global_Prefix(ref DXRandom __result)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return true;

            // DXRandom.Global is [ThreadStatic] and seeded from new Random()/real ticks, so it must
            // never be handed out while the sandbox is active. Logic/load draws use the shared synced
            // stream; everything else (physics callbacks, rendering) uses a separate fixed-seed stream.
            if (LockstepClock.InDeterministicContext || LockstepClock.LogicTickActive || LockstepClock.LoadingLevel)
                __result = LockstepClock.GetSyncedGlobalRandom();
            else if (LockstepClock.InPhysicsContext)
                __result = LockstepClock.GetAuxGlobalRandom();
            else
                __result = LockstepClock.GetRenderGlobalRandom();
            return false;
        }

        // 3. Fixed-step clock: drive GameTime in exact 33.333..ms increments and only advance a
        //    tick when the active ITickGate allows it. Vanilla's UpdateGameLogic runs a Task and
        //    loops `while (GameTime >= LastGameTimeIAUpdate + c)`; setting GameTime to exactly
        //    LastGameTimeIAUpdate + c yields exactly one tick, pinning it just below yields none.
        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPrefix]
        public static void UpdateGameLogic_Prefix(DXGame __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return;

            // No simulation ticks or physics steps while a save is loading: pin both clocks to the
            // current GameTime. Scene/loader fades use wall-clock time, so loading still progresses.
            // Freezing also stops load-time GameTime drift, which would otherwise be baked into
            // entity timers initialized during deserialization.
            if (LockstepClock.LoadingLevel)
            {
                DXGame.LastGameTimeIAUpdate = __instance.GameTime;
                DXPhysicWorld.LastGameTimeUpdatePhysics = __instance.GameTime;
                return;
            }

            LockstepClock.LogicTickActive = true;
            LockstepClock.InDeterministicContext = true;

            if (DXGame.LastGameTimeIAUpdate == 0.0)
                DXGame.LastGameTimeIAUpdate = __instance.GameTime;

            int nextTick = __instance.IAIterationInt;
            double boundary = DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick;
            bool ready = LockstepClock.TickGate == null || LockstepClock.TickGate.IsTickReady(nextTick);

            // Network lockstep: apply the turn's buffered commands exactly at its tick boundary.
            if (ready && Sync.CoopSessionManager.IsInSyncedLevel)
                Sync.CoopSessionManager.FlushDueTurnCommands(nextTick);

            __instance.GameTime = ready ? boundary : boundary - 0.001;
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPostfix]
        public static void UpdateGameLogic_Postfix(DXGame __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return;

            // Wait synchronously for _TaskLogic so logic never overlaps rendering or network replay.
            AccessTools.Method(typeof(DXGame), "WaitForGameLogic")?.Invoke(__instance, null);
            LockstepClock.LogicTickActive = false;
            LockstepClock.InDeterministicContext = false;
        }

        // 4. Suppress the engine's background GameTime/physics threads while deterministic:
        //    they advance GameTime by real frame time and would destroy fixed-step reproducibility.
        //    UpdatePhysics() is still called synchronously from SetCamera when IsIADeterministic.
        [HarmonyPatch(typeof(DXGame), "LoadPhysics")]
        [HarmonyPrefix]
        public static bool LoadPhysics_Prefix() => !LockstepClock.IsDeterministicModeActive;

        [HarmonyPatch(typeof(DXGame), "LoadPhysicsOld")]
        [HarmonyPrefix]
        public static bool LoadPhysicsOld_Prefix() => !LockstepClock.IsDeterministicModeActive;

        // Mark the physics section so DXRandom.Global draws from physics callbacks use a stream that
        // render-phase draws cannot perturb.
        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPrefix]
        public static void UpdatePhysics_Enter()
        {
            if (LockstepClock.IsDeterministicModeActive) LockstepClock.InPhysicsContext = true;
        }

        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPostfix]
        public static void UpdatePhysics_Exit() => LockstepClock.InPhysicsContext = false;
    }

    /// <summary>
    /// Forces ZXGame.IsMultiplayer() to true while the deterministic sandbox is active. The engine
    /// already contains the lockstep pathfinder branch: in multiplayer CMovable computes paths
    /// synchronously via DXWorldGrid.FindPath instead of queueing them on background threads.
    /// </summary>
    [HarmonyPatch]
    public static class ZXGameMultiplayerModePatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXGame, ObfuscatedSymbols.Method_ZXGame_IsMultiplayer, Type.EmptyTypes);
        }

        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// Runs after ZXSystemGameLevel.#=zwK_pUIeRBpbp (the main-thread finish action of the async
    /// loader). This is the first point where all saved entities and GameTime are restored, so it is
    /// the only safe place to calibrate the synced ID counter and align the simulation clocks.
    /// </summary>
    [HarmonyPatch]
    public static class LevelLoadCompletePatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_OnLevelLoadComplete, Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            LockstepClock.OnLevelLoadComplete();
            Testing.DeterministicReplayVerifier.OnLevelLoadComplete();
            Sync.CoopSessionManager.OnLevelLoadComplete();
        }
    }

}
