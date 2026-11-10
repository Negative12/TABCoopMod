using System;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Forces every DXHelper_Task.For parallel loop to run sequentially while the deterministic
    /// sandbox is active. This is the single-threading fix for DXPhysicWorld.UpdateDefaultTime(),
    /// whose two For loops apply forces / update positions from multiple tasks in a scheduler-
    /// dependent order. Patching UpdateDefaultTime itself is impossible: MonoMod refuses to rewrite
    /// its filtered-catch IL (HarmonyException), so we neutralize the parallelism at its source.
    ///
    /// Rendering loops also become sequential in deterministic mode — correct, only slower.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicTaskPatch
    {
        // Sequential only during the running simulation. Level loading stays parallel: entity IDs
        // restored from the save are fixed, load-time temp IDs are local/high-bit, and doing the
        // whole deserialize single-threaded made large saves (6666) take minutes.
        private static bool Active => LockstepClock.IsDeterministicModeActive;

        [HarmonyPatch]
        public static class ForIndex
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int> __2)
            {
                if (!Active) return true;
                for (int i = __0; i < __1; i++) __2(i);
                return false;
            }
        }

        [HarmonyPatch]
        public static class ForRange
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int, int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int, int> __2)
            {
                if (!Active) return true;
                __2(__0, __1);
                return false;
            }
        }

        [HarmonyPatch]
        public static class ForRangeWithThread
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int, int, int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int, int, int> __2)
            {
                if (!Active) return true;
                __2(__0, __1, 0);
                return false;
            }
        }
    }
}
