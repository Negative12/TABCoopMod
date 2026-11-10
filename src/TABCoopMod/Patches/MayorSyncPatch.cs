using System;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Captures a player's mayor pick as a MayorVoteOrSelect command (identified by the mayor's
    /// string ID, not the male/female random slot) so every peer applies the exact same mayor.
    ///
    /// Guards are strict: the engine applies initial mayors while restoring a save, so capture must
    /// stay completely inert during loading / before the level is live. (Hooking this method without
    /// guards previously crashed the process during level load.)
    /// </summary>
    [HarmonyPatch]
    public static class MayorSyncPatch
    {
        private static Type _mayorType;
        private static PropertyInfo _idProp;

        public static MethodBase TargetMethod()
        {
            _mayorType = GameBridge.GameAssembly?.GetType("ZX.ZXMayor");
            if (_mayorType == null) return null;
            return AccessTools.Method(_mayorType, ObfuscatedSymbols.Method_ZXMayor_Apply, new[] { GameBridge.LevelStateType });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance)
        {
            if (!CommandInterceptor.IsInterceptionEnabled
                || CommandExecutor.IsReplayingCommand
                || LockstepClock.LoadingLevel
                || LockstepClock.InDeterministicContext
                || DXGame.Current == null
                || DXGame.Current.IAIterationInt <= 0)
                return true;

            if (__instance == null) return true;

            if (_idProp == null)
                _idProp = _mayorType?.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
            string id = null;
            try { id = _idProp?.GetValue(__instance) as string; } catch { }
            if (string.IsNullOrEmpty(id)) return true;

            CommandInterceptor.Dispatch(new NetCommand
            {
                Type = NetCommandType.MayorVoteOrSelect,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current.IAIterationInt,
                MayorId = id
            });
            return false;
        }
    }
}
