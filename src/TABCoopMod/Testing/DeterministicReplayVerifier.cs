using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Loads a save, runs a scripted command scenario for N ticks while recording a per-tick
    /// StateHash timeline, then reloads the same save and repeats. If both timelines are identical
    /// the simulation is deterministic for that scenario. Supports an in-process double run and a
    /// two-process fallback (TABCOOP_REPLAY_RUN=1|2) when the engine cannot reload in place.
    /// </summary>
    public static class DeterministicReplayVerifier
    {
        private const int ReplaySeed = 20261109;       // identical for both runs
        private const int ReloadWatchdogFrames = 300;

        private enum Phase { Idle, AwaitRun1, Running, AwaitReload2, AwaitRun2, Done }

        private static Phase _phase = Phase.Idle;
        private static int _currentRun;
        private static int _baselineTick;
        private static int _ticks;
        private static string _savePath;
        private static string _scenarioPath;
        private static string _run1Path;
        private static string _run2Path;
        private static string _resultPath;

        private static ScriptedCommandDriver _driver;
        private static readonly List<string> _records = new List<string>();
        private static int _reloadWatchFrames;
        private static bool _reloadPending;
        private static int _shotDueCount = -1;
        private static int _armTick = -1;
        private static int _lastEntityCount = -1;
        private static int _settleStreak;
        private const int SettleStreakTicks = 10;
        private const int SettleGiveUpTicks = 600;

        public static bool IsRunning => _phase == Phase.Running;

        public static void Begin()
        {
            if (!TestHarnessConfig.Enabled || string.IsNullOrEmpty(TestHarnessConfig.ReplaySave) || _phase != Phase.Idle)
                return;

            _savePath = TestHarnessConfig.ReplaySave;
            _ticks = TestHarnessConfig.ReplayTicks;
            _scenarioPath = string.Equals(TestHarnessConfig.Scenario, "default", StringComparison.OrdinalIgnoreCase)
                ? null : TestHarnessConfig.Scenario;

            string dir = TestHarnessConfig.OutputDirectory;
            _run1Path = Path.Combine(dir, "coop_replay_run1.timeline");
            _run2Path = Path.Combine(dir, "coop_replay_run2.timeline");
            _resultPath = Path.Combine(dir, "coop_replay_result.txt");

            _phase = TestHarnessConfig.ReplayRun == 2 ? Phase.AwaitRun2 : Phase.AwaitRun1;
            ModLogger.Info($"[ReplayVerifier] ARMED. Waiting for level init. save={_savePath}, ticks={_ticks}, runMode={TestHarnessConfig.ReplayRun}");
        }

        /// <summary>
        /// Must be called BEFORE LoadSaveFileAndEnter: entities/random draws produced during the
        /// save load itself must already use the synced GUID counter and seeded RNG, otherwise the
        /// loaded initial state differs between processes and the very first hash diverges.
        /// </summary>
        public static void PrepareForLoad()
        {
            if (!TestHarnessConfig.Enabled) return;
            try
            {
                LockstepClock.TickGate = new AlwaysReadyTickGate();
                LockstepClock.EnableDeterministicSandbox(ReplaySeed);
                LockstepClock.LoadingLevel = true;
                LockstepClock.ResetBehaviourRandom(ReplaySeed);
                ModLogger.Info("[ReplayVerifier] Deterministic sandbox enabled BEFORE load.");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] PrepareForLoad failed", ex);
            }
        }

        /// <summary>Called from the level InitLevel postfix.</summary>
        public static void OnLevelInitialized()
        {
            if (_phase == Phase.AwaitRun1) StartRun(1);
            else if (_phase == Phase.AwaitRun2) StartRun(2);
        }

        private static void StartRun(int run)
        {
            try
            {
                _currentRun = run;
                ResetStaticsForRun();
                LockstepClock.LoadingLevel = false; // load finished; InitLevel postfix has fired

                // Baseline tick is captured lazily on the first real logic tick, because the save's
                // IAIterationInt is restored only after InitLevel returns.
                _baselineTick = -1;
                _driver = null;
                _armTick = -1;
                _lastEntityCount = -1;
                _settleStreak = 0;
                _reloadPending = false;
                _shotDueCount = -1;

                LockstepClock.EnableDeterministicSandbox(ReplaySeed);
                LockstepClock.CalibrateEntityIdCounterFromLoadedLevel(ReplaySeed);
                LockstepClock.TickGate = new AlwaysReadyTickGate();
                GameBridge.SortEntitiesById();
                LockstepClock.ResetBehaviourRandom(ReplaySeed);

                _records.Clear();
                _phase = Phase.Running;

                ModLogger.Info($"[ReplayVerifier] RUN {run} armed; waiting for first logic tick (target {_ticks} ticks).");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ReplayVerifier] StartRun({run}) failed", ex);
                _phase = Phase.Done;
            }
        }

        public static void OnBeforeLogicTick(int nextSimTick)
        {
            if (_phase != Phase.Running) return;

            if (_baselineTick < 0)
            {
                if (_armTick < 0) _armTick = nextSimTick;

                int total = CountEntities();
                if (total > 0 && total == _lastEntityCount) _settleStreak++;
                else { _settleStreak = 0; _lastEntityCount = total; }

                int sinceArm = _armTick >= 0 ? nextSimTick - _armTick : 0;
                // Align both processes on the same ABSOLUTE tick: first multiple of 100 at least
                // 50 ticks after level init. Slightly different load timing still lands on the same
                // tick, so the two timelines are directly comparable.
                bool aligned = total > 0 && sinceArm >= 50 && (nextSimTick % 100 == 0);
                bool giveUp = sinceArm >= SettleGiveUpTicks;
                if (!aligned && !giveUp) return;

                _baselineTick = nextSimTick;
                LockstepClock.ResetBehaviourRandom(ReplaySeed);
                GameBridge.SortEntitiesById();
                _driver = string.IsNullOrEmpty(_scenarioPath)
                    ? ScriptedCommandDriver.CreateDefault(_baselineTick)
                    : ScriptedCommandDriver.FromFile(_scenarioPath);

                int players = GameBridge.GetPlayerEntities().Count;
                var cc = GameBridge.GetCommandCenterEntity();
                ModLogger.Info($"[ReplayVerifier] RUN {_currentRun} baseline tick {_baselineTick}: entities={total}, players={players}, cc={(cc != null ? cc.ID.ToString() : "none")}, ops={_driver.Count}, aligned={aligned}");
            }

            _driver?.OnBeforeLogicTick(nextSimTick);
        }

        private static int CountEntities()
        {
            int total = 0;
            if (DXGame.Current?.Entities != null)
                foreach (var _ in DXGame.Current.Entities) total++;
            return total;
        }

        private static void DumpPositions()
        {
            try
            {
                var sb = new StringBuilder(1 << 20);
                int n = 0;
                if (DXGame.Current?.Entities != null)
                {
                    foreach (var e in DXGame.Current.Entities)
                    {
                        if (e == null || e.Disposed) continue;
                        if ((e.ID & 0x8000000000000000UL) != 0) continue;
                        sb.Append(e.ID).Append('|')
                          .Append(e.Position.X.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
                          .Append(e.Position.Y.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
                          .Append(e.GetType().Name).Append('\n');
                        n++;
                    }
                }
                string path = Path.Combine(TestHarnessConfig.OutputDirectory, $"coop_pos_run{_currentRun}.txt");
                File.WriteAllText(path, sb.ToString());
                ModLogger.Info($"[ReplayVerifier] Dumped {n} entity positions -> {path}");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] DumpPositions failed", ex);
            }
        }

        /// <summary>
        /// At the first recorded tick, dump positions plus a screenshot and the camera transform so a
        /// report can box divergent entities directly on the image (see scripts/build_divergence_report.py).
        /// </summary>
        private static void DumpSnapshot()
        {
            DumpPositions();

            // Optional camera override (TABCOOP_SHOT_CAM) lets the report look at the interaction
            // area instead of the save's restored camera. Apply now; capture a couple of ticks later
            // so the render target has been redrawn with the new camera.
            if (!string.IsNullOrEmpty(TestHarnessConfig.ShotCamera) && CameraProbe.ApplySpec(TestHarnessConfig.ShotCamera))
            {
                _shotDueCount = _records.Count + 2;
                ModLogger.Info($"[ReplayVerifier] camera override '{TestHarnessConfig.ShotCamera}' applied; screenshot due at record {_shotDueCount}.");
                return;
            }

            CaptureShot();
        }

        private static void MaybeCaptureDeferredShot()
        {
            if (_shotDueCount < 0 || _records.Count < _shotDueCount) return;
            _shotDueCount = -1;
            CaptureShot();
        }

        private static void CaptureShot()
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;

                string shotPath = Path.Combine(TestHarnessConfig.OutputDirectory, $"coop_shot_run{_currentRun}.png");
                string camPath = Path.Combine(TestHarnessConfig.OutputDirectory, $"coop_cam_run{_currentRun}.json");

                using (var bmp = game.CaptureScreen())
                {
                    if (bmp == null) return;
                    bmp.Save(shotPath, ImageFormat.Png);
                    CameraProbe.WriteJson(camPath, bmp.Width, bmp.Height);
                    ModLogger.Info($"[ReplayVerifier] Dumped run{_currentRun} screenshot {bmp.Width}x{bmp.Height} -> {shotPath}; camera -> {camPath}");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] CaptureShot failed", ex);
            }
        }

        public static void OnAfterLogicTick(int tick)
        {
            if (_phase != Phase.Running || _baselineTick < 0) return;

            // Guarantee the logic Task has finished regardless of patch ordering vs the clock patch.
            try { AccessTools.Method(typeof(DXGame), "WaitForGameLogic")?.Invoke(DXGame.Current, null); } catch { }

            try
            {
                var d = StateHasher.ComputeDigest();
                var rec = new ReplayTickRecord
                {
                    Run = _currentRun,
                    Tick = d.SimTick,
                    Hash = d.FullHash,
                    Gold = d.Gold, Wood = d.Wood, Stone = d.Stone, Iron = d.Iron, Oil = d.Oil,
                    EntityCount = d.EntityCount,
                    PosChecksum = d.PosChecksum,
                    LifeChecksum = d.LifeChecksum,
                    ProjectileChecksum = d.ProjectileChecksum,
                    ProductionChecksum = d.ProductionChecksum,
                    ReservationChecksum = d.ReservationChecksum
                };
                _records.Add(rec.ToJson());

                if (TestHarnessConfig.PosDump && _records.Count == 1)
                    DumpSnapshot();

                MaybeCaptureDeferredShot();

                if (d.SimTick >= _baselineTick + _ticks)
                    FinishRun();
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] hash/record failed", ex);
            }
        }

        private static void FinishRun()
        {
            try
            {
                string path = _currentRun == 1 ? _run1Path : _run2Path;
                File.WriteAllLines(path, _records);
                ModLogger.Info($"[ReplayVerifier] RUN {_currentRun} finished: {_records.Count} records -> {path}");

                if (TestHarnessConfig.ReplayRun == 1 || TestHarnessConfig.ReplayRun == 2)
                {
                    _phase = Phase.Done;
                    ModLogger.Info($"[ReplayVerifier] SINGLE-PASS RUN {_currentRun} DONE. Compare externally: {path}");
                    return;
                }

                if (_currentRun == 1)
                {
                    _phase = Phase.AwaitRun2;
                    _reloadPending = true;
                    _reloadWatchFrames = 0;
                    ModLogger.Info("[ReplayVerifier] Reloading save for RUN 2...");
                    DXGame.Current.InvokeOnStartFrame(() =>
                    {
                        try { GameBridge.LoadSaveFileAndEnter(_savePath); }
                        catch (Exception ex) { ModLogger.Error("[ReplayVerifier] Reload for RUN 2 failed", ex); FallbackRequired(); }
                    });
                }
                else
                {
                    Compare();
                    _phase = Phase.Done;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] FinishRun failed", ex);
                _phase = Phase.Done;
            }
        }

        private static void Compare()
        {
            var a = ReplayTimeline.ParseFile(_run1Path);
            var b = ReplayTimeline.ParseFile(_run2Path);
            var result = ReplayTimeline.Compare(a, b);

            string line = $"ReplayVerifier {(result.Pass ? "PASS" : "FAIL")} :: {result}";
            try { File.WriteAllText(_resultPath, line + Environment.NewLine); } catch { }

            if (result.Pass) ModLogger.Info($"[ReplayVerifier] {line}");
            else ModLogger.Warn($"[ReplayVerifier] {line}");
        }

        private static void FallbackRequired()
        {
            _phase = Phase.Done;
            string line = "ReplayVerifier FALLBACK_REQUIRED :: in-process reload did not re-enter the level. " +
                          "Re-run with TABCOOP_REPLAY_RUN=1 then TABCOOP_REPLAY_RUN=2 in separate processes and compare the two timeline files.";
            try { File.WriteAllText(_resultPath, line + Environment.NewLine); } catch { }
            ModLogger.Warn($"[ReplayVerifier] {line}");
        }

        /// <summary>Per-render-frame hook used only to watchdog the reload.</summary>
        public static void OnRenderFrame()
        {
            if (!_reloadPending || _phase != Phase.AwaitRun2) return;
            _reloadWatchFrames++;
            if (_reloadWatchFrames > ReloadWatchdogFrames)
                FallbackRequired();
        }

        private static void ResetStaticsForRun()
        {
            CommandInterceptor.IsInterceptionEnabled = false;
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            CommandExecutor.IsReplayingCommand = false;
            LockstepClock.LogicTickActive = false;
            LockstepClock.InDeterministicContext = false;

            try { DXGame.LastGameTimeIAUpdate = 0.0; } catch { }
            try { DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0; } catch { }
            try { if (DXGame.Current != null) DXGame.Current.Paused = false; } catch { }
        }
    }
}
