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

        public static long CurrentSyncedEntityIdCounter => _syncedEntityIdCounter;

        public static void EnableDeterministicSandbox(int initialSeed, long startingEntityIdCounter = 100000L)
        {
            _syncedRandomSeed = initialSeed;
            _syncedEntityIdCounter = startingEntityIdCounter;
            _syncedGlobalRandom = new DXRandom(initialSeed);

            // Enable the engine's built-in single-threaded entity update & synchronous physics flag!
            DXGame.IsIADeterministic = true;
            IsDeterministicModeActive = true;

            // Unload background async physics/time threads if they were already running
            DXGame.Current?.UnloadPhysics();

            ModLogger.Info($"[LockstepClock] Deterministic sandbox ENABLED (Seed={initialSeed}, StartEntityId={startingEntityIdCounter}, IsIADeterministic={DXGame.IsIADeterministic}).");
        }

        public static void DisableDeterministicSandbox()
        {
            IsDeterministicModeActive = false;
            InDeterministicContext = false;
            LogicTickActive = false;
            LoadingLevel = false;
            TickGate = new AlwaysReadyTickGate();
            DXGame.IsIADeterministic = false;
            ModLogger.Info("[LockstepClock] Deterministic sandbox DISABLED.");
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
            EnableDeterministicSandbox(seed, maxId + 1000L);
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
            if (!LockstepClock.IsDeterministicModeActive ||
                !(LockstepClock.InDeterministicContext || LockstepClock.LogicTickActive || LockstepClock.LoadingLevel))
                return true;

            __result = LockstepClock.GetSyncedGlobalRandom();
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

            LockstepClock.LogicTickActive = true;
            LockstepClock.InDeterministicContext = true;

            if (DXGame.LastGameTimeIAUpdate == 0.0)
                DXGame.LastGameTimeIAUpdate = __instance.GameTime;

            int nextTick = __instance.IAIterationInt;
            double boundary = DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick;
            bool ready = LockstepClock.TickGate == null || LockstepClock.TickGate.IsTickReady(nextTick);

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
    }
}
