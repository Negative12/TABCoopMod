using System;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;
using TABCoopMod.Sync;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Intercepts pause (Space) and game-speed (+/-) at the input layer instead of hooking
    /// DXGame.set_Paused. Hooking the setter broke level loading (the engine calls it during load).
    /// OnKeyDown/OnKeyUp are only reached from real user input, never from the loader.
    /// </summary>
    public static class PauseAndSpeedInputPatch
    {
        private static bool CanIntercept()
        {
            if (!CommandInterceptor.IsInterceptionEnabled) return false;
            if (LockstepClock.LoadingLevel) return false;
            if (DXGame.Current == null) return false;
            if (DXModalScreen.IsInModal) return false;
            return true;
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_PauseKey
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, "OnKeyDown", new[] { typeof(DXKeys) });
            }

            [HarmonyPrefix]
            public static bool Prefix(DXKeys __0, ref bool __result)
            {
                if (__0 != DXKeys.Space || !CanIntercept()) return true;

                PauseSyncController.RequestPause(!DXGame.Current.Paused);
                __result = true;
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_SpeedKeys
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, "OnKeyUp", new[] { typeof(DXKeys) });
            }

            [HarmonyPrefix]
            public static bool Prefix(DXKeys __0, ref bool __result)
            {
                double delta;
                switch (__0)
                {
                    case DXKeys.Subtract:
                    case DXKeys.OemMinus: delta = -0.2; break;
                    case DXKeys.Add:
                    case DXKeys.Oemplus: delta = 0.2; break;
                    default: return true;
                }

                if (!CanIntercept()) return true;

                float speed = (float)Math.Max(0.0, Math.Min(20.0, DXGame.Current.GameSpeed + delta));
                CommandInterceptor.Dispatch(new NetCommand
                {
                    Type = NetCommandType.SetGameSpeed,
                    PlayerId = CommandInterceptor.LocalPlayerId,
                    GameSpeed = speed
                });
                __result = true;
                return false;
            }
        }
    }
}
