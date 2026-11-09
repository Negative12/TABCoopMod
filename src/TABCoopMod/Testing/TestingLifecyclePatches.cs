using System;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;
using TABCoopMod.Sync;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Wires the test harness into the game loop: optional auto-level-load, F8 single-player
    /// interception toggle, and per-tick hooks for the deterministic replay verifier.
    /// All behaviour is gated on TestHarnessConfig / explicit key press; normal play is unaffected.
    /// </summary>
    [HarmonyPatch]
    public static class TestingLifecyclePatches
    {
        private static bool _autoLoadIssued;
        private static bool _f8Latch;
        private static bool _armIssued;
        private static bool _roleApplied;
        private static bool _menuReady;

        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPostfix]
        public static void StartFrame_Postfix()
        {
            try
            {
                TestHarnessConfig.Initialize();

                if (TestHarnessConfig.Enabled && !_armIssued)
                {
                    _armIssued = true;
                    DeterministicReplayVerifier.Begin();
                }

                DeterministicReplayVerifier.OnRenderFrame();
                HandleSinglePlayerToggle();
                TryApplyRole();
                TryAutoLoad();
            }
            catch (Exception ex)
            {
                ModLogger.Error("[TestingLifecyclePatches] StartFrame hook failed", ex);
            }
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_InitLevel_TestHooks
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_InitLevel);
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                try
                {
                    if (TestHarnessConfig.Enabled && TestHarnessConfig.Intercept)
                    {
                        if (CommandInterceptor.OnLocalCommandCaptured == null)
                            CommandInterceptor.OnLocalCommandCaptured = cmd => CommandExecutor.Execute(cmd);
                        CommandInterceptor.IsInterceptionEnabled = true;
                        ModLogger.Info("[TestHarness] Interception auto-enabled at level init.");
                    }

                    if (TestHarnessConfig.Enabled)
                        DeterministicReplayVerifier.OnLevelInitialized();
                }
                catch (Exception ex)
                {
                    ModLogger.Error("[TestingLifecyclePatches] InitLevel hook failed", ex);
                }
            }
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPrefix]
        public static void UpdateGameLogic_Prefix_Verifier(DXGame __instance)
        {
            if (!TestHarnessConfig.Enabled) return;
            DeterministicReplayVerifier.OnBeforeLogicTick(__instance?.IAIterationInt ?? 0);
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPostfix]
        public static void UpdateGameLogic_Postfix_Verifier(DXGame __instance)
        {
            if (!TestHarnessConfig.Enabled) return;
            DeterministicReplayVerifier.OnAfterLogicTick(__instance?.IAIterationInt ?? 0);
        }

        private static void HandleSinglePlayerToggle()
        {
            if (!TestHarnessConfig.Enabled) return; // avoid surprising normal players with a hotkey
            var input = DXInputState.Current;
            if (input == null) return;

            bool pressed = input.IsKeyPressed(DXKeys.F8, false);
            if (pressed && !_f8Latch)
            {
                // Never fight a live network session: only toggle the single-player loop.
                if (!CoopSessionManager.IsSessionActive)
                {
                    if (CommandInterceptor.OnLocalCommandCaptured == null)
                        CommandInterceptor.OnLocalCommandCaptured = cmd => CommandExecutor.Execute(cmd);

                    CommandInterceptor.IsInterceptionEnabled = !CommandInterceptor.IsInterceptionEnabled;
                    ModLogger.Info($"[TestHarness] F8 -> single-player interception = {CommandInterceptor.IsInterceptionEnabled}");
                }
            }
            _f8Latch = pressed;
        }

        private static void TryApplyRole()
        {
            if (_roleApplied) return;
            string role = TestHarnessConfig.Role;
            if (string.IsNullOrEmpty(role)) return;
            if (CoopSessionManager.IsSessionActive) return;

            if (string.Equals(role, "host", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ModLogger.Info("[TestHarness] Auto-starting loopback HOST session.");
                    CoopSessionManager.StartHostSession(useLoopback: true);
                    _roleApplied = true;
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[TestHarness] Host start failed, will retry: {ex.Message}");
                }
            }
            else if (string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
            {
                // Host may not be listening yet; retry each frame until the blocking connect succeeds.
                try
                {
                    ModLogger.Info("[TestHarness] Auto-joining loopback session as CLIENT.");
                    CoopSessionManager.JoinSession(useLoopback: true, targetAddressOrSteamId: "127.0.0.1:27099");
                    _roleApplied = true;
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[TestHarness] Client join failed, will retry: {ex.Message}");
                }
            }
        }

        private static void TryAutoLoad()
        {
            if (_autoLoadIssued || !TestHarnessConfig.Enabled) return;
            if (string.IsNullOrEmpty(TestHarnessConfig.AutoLoadSave)) return;
            if (CoopSessionManager.IsSessionActive) return;
            if (!_menuReady) return; // wait until the main menu is fully initialized

            if (GameBridge.CurrentLevelState != null) return;
            if (DXGame.Current == null) return;

            _autoLoadIssued = true;
            string save = TestHarnessConfig.AutoLoadSave;
            ModLogger.Info($"[TestHarness] Auto-loading save: {save}");
            DXGame.Current.InvokeOnStartFrame(() =>
            {
                try
                {
                    DeterministicReplayVerifier.PrepareForLoad();
                    GameBridge.LoadSaveFileAndEnter(save);
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[TestHarness] Auto-load failed: {save}", ex);
                    _autoLoadIssued = false; // allow a retry next frame
                }
            });
        }

        [HarmonyPatch]
        public static class Patch_MainMenu_OnLoad_TestHook
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemMainMenu, "OnLoad", Type.EmptyTypes);
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                _menuReady = true;
            }
        }
    }
}
