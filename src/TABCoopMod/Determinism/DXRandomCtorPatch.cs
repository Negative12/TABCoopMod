using System;
using System.Reflection;
using System.Threading;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Patches DXRandom's parameterless constructor, which vanilla seeds from
    /// ThreadId * Ticks (nondeterministic). In deterministic mode we re-seed it via the private
    /// init_genrand(uint) from a synced counter so every process draws the same sequence.
    ///
    /// Kept in its own patch class so a failure here can never disable the rest of
    /// DeterministicSandboxPatches (that exact mistake once took down all determinism patches).
    /// </summary>
    [HarmonyPatch]
    public static class DXRandomCtorPatch
    {
        private static MethodInfo _initGenrand;
        private static long _seedCounter;

        public static MethodBase TargetMethod()
        {
            return AccessTools.Constructor(typeof(DXRandom), Type.EmptyTypes);
        }

        [HarmonyPrefix]
        public static bool Prefix(DXRandom __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;

            if (_initGenrand == null)
                _initGenrand = AccessTools.Method(typeof(DXRandom), "init_genrand", new[] { typeof(uint) });
            if (_initGenrand == null) return true;

            long n = Interlocked.Increment(ref _seedCounter);
            uint seed = unchecked((uint)(LockstepClock.SyncedRandomSeed) + (uint)(n * 2654435761L));
            try { _initGenrand.Invoke(__instance, new object[] { seed }); }
            catch { return true; }
            return false;
        }
    }
}
