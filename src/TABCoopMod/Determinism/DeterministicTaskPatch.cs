using System;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Forces the position-affecting DXHelper_Task.For loops inside DXPhysicWorld.UpdateDefaultTime
    /// to run sequentially while the deterministic sandbox is active. Those two loops apply forces and
    /// read back positions from multiple tasks in scheduler-dependent order — the only simulated state
    /// touched by parallelism once IsIADeterministic + IsMultiplayer are on. Patching
    /// UpdateDefaultTime itself is impossible: MonoMod refuses to rewrite its filtered-catch IL
    /// (HarmonyException), so we neutralize the parallelism at its source.
    ///
    /// Everything else (DXScene.UpdateObjects culling, texture baking, level-table loading) keeps its
    /// native parallelism; serializing those made large saves (6666, 33k entities) take minutes.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicTaskPatch
    {
        // Serialize only the two position-affecting physics loops. Level deserialization and scene
        // culling keep native parallelism: serializing them made 6666.zxsav take minutes, and the
        // ctor seed sequence is already isolated per-thread by DXRandomCtorPatch.
        private static bool Active => LockstepClock.IsDeterministicModeActive && LockstepClock.InPhysicsStep;

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
