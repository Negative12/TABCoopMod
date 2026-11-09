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

            if (LockstepClock.InDeterministicContext)
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
            if (!LockstepClock.IsDeterministicModeActive || !LockstepClock.InDeterministicContext)
                return true;

            __result = LockstepClock.GetSyncedGlobalRandom();
            return false;
        }

        // 3. Wrap DXEntity.Update() / ZXSystemGameLevel.OnStartUpdate() with InDeterministicContext = true
        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPrefix]
        public static void UpdateGameLogic_Prefix()
        {
            if (LockstepClock.IsDeterministicModeActive)
            {
                LockstepClock.InDeterministicContext = true;
            }
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPostfix]
        public static void UpdateGameLogic_Postfix(DXGame __instance)
        {
            if (LockstepClock.IsDeterministicModeActive)
            {
                // Wait synchronously for _TaskLogic so logic updates never overlap with rendering or network replay!
                AccessTools.Method(typeof(DXGame), "WaitForGameLogic")?.Invoke(__instance, null);
                LockstepClock.InDeterministicContext = false;
            }
        }
    }
}
