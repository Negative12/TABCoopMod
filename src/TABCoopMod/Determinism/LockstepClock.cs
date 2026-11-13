using System;
using System.Collections.Generic;
using System.Drawing;
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
        public static int EffectiveLoadSeed => HasPendingPostLoadCalibration ? PendingLoadSeed : _syncedRandomSeed;
        private static DXRandom _syncedGlobalRandom;
        private static DXRandom _auxGlobalRandom;
        private static DXRandom _renderGlobalRandom;

        /// <summary>True while DXGame.UpdatePhysics() is on the stack (physics callbacks run outside LogicTickActive).</summary>
        public static volatile bool InPhysicsContext;

        /// <summary>
        /// Only true on frames where UpdateGameLogic_Prefix allowed a logic tick to advance.
        /// Prevents UpdatePhysics() from stepping ahead during MainMenu, LoadingLevel, or PausedBySession.
        /// </summary>
        public static bool AllowPhysicsThisFrame { get; set; }

        /// <summary>
        /// Same span as InPhysicsContext, but also gates DeterministicTaskPatch: only the two
        /// DXPhysicWorld.UpdateDefaultTime For-loops affect simulated positions. Serializing every
        /// other For (scene culling, texture baking, level tables) made big saves take minutes.
        /// </summary>
        public static volatile bool InPhysicsStep;

        /// <summary>
        /// True while ZXMapDrawer.#=z0Pkwp_k=(DXLevel) runs terrain/tree/grass generation during
        /// InitLevel. Serializing DXHelper_Task.For inside this window prevents worker threads from
        /// consuming DXRandom streams in nondeterministic order.
        /// </summary>
        public static volatile bool InMapDrawer;

        /// <summary>
        /// True while executing our deterministic end-of-physics CSprite pull-animation pass so
        /// CSprite.#=zykGuoNdyOv10tsMpy8FzNkE=() runs for every pullable entity rather than only
        /// those inside the local camera viewport during render-time UpdateSceneObjectParallel.
        /// </summary>
        public static volatile bool AllowCSpritePullUpdate;

        /// <summary>
        /// Managed thread id of the render/frame thread (captured from DXGame.StartFrame). Used to
        /// tell the level-loading worker thread apart from UI/render threads that also construct
        /// DXRandom while a save is deserializing.
        /// </summary>
        public static int MainThreadId { get; private set; }

        /// <summary>
        /// True while the game is paused by an explicit synchronized pause command. The engine's
        /// save loader sets DXGame.Paused=true for its own bookkeeping and never clears it, so the
        /// fixed-step clock must key off THIS flag rather than DXGame.Paused.
        /// </summary>
        public static bool PausedBySession { get; private set; }

        public static void SetSessionPause(bool paused)
        {
            PausedBySession = paused;
            if (paused) ResetHostTickPacing();
        }

        // Real-time monotonic pacing for live Host co-op play (offline replay remains unthrottled).
        private static double _nextHostRealTickMs;

        public static void ResetHostTickPacing() => _nextHostRealTickMs = 0.0;

        internal static bool ShouldAdvanceHostRealTimeTick(double gameSpeed)
        {
            if (gameSpeed <= 0.001)
            {
                _nextHostRealTickMs = 0.0;
                return false;
            }

            double nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (_nextHostRealTickMs <= 0.0 || nowMs - _nextHostRealTickMs > 250.0)
            {
                _nextHostRealTickMs = nowMs;
            }

            // 1ms tolerance avoids skipping a 60Hz VSync frame by sub-millisecond timer jitter.
            if (nowMs + 1.0 < _nextHostRealTickMs)
                return false;

            _nextHostRealTickMs += MsPerLogicTick / gameSpeed;
            return true;
        }

        /// <summary>Executes an action under InDeterministicContext on the calling thread.</summary>
        public static void RunInDeterministicContext(Action action)
        {
            bool prev = InDeterministicContext;
            InDeterministicContext = true;
            try
            {
                EnsureBehaviourRandomOnCurrentThread();
                action?.Invoke();
            }
            finally { InDeterministicContext = prev; }
        }

        [ThreadStatic] private static int _audioRngDepth;
        [ThreadStatic] private static int _visualVfxDepth;

        /// <summary>
        /// True while executing inside ZXSystemAudio synchronous playback/selection on this thread.
        /// Audio depends on local camera frustum and singleton caches, so its PRNG draws must
        /// never consume the shared synced or scene streams.
        /// </summary>
        public static bool InAudioContext => _audioRngDepth > 0;

        /// <summary>
        /// True while executing inside the pure-visual VFX spawner (#=zg10wm6mM9hhx) on this thread.
        /// Several VFX helpers check CheckFastObjectVisibility() / IsPixelAreaVisible / VisibleCells
        /// (which depend on local camera viewport), yet draw DXRandom.Global and allocate bare DXEntities.
        /// </summary>
        public static bool InVisualVfxContext => _visualVfxDepth > 0;

        public static void EnterAudioContext() => _audioRngDepth++;

        public static void ExitAudioContext()
        {
            if (_audioRngDepth > 0) _audioRngDepth--;
        }

        public static void EnterVisualVfxContext() => _visualVfxDepth++;

        public static void ExitVisualVfxContext()
        {
            if (_visualVfxDepth > 0) _visualVfxDepth--;
        }

        public static bool ShouldUseSyncedSceneRandom()
        {
            if (InAudioContext || InVisualVfxContext) return false;
            bool onMain = Thread.CurrentThread.ManagedThreadId == MainThreadId;
            return InDeterministicContext
                || InPhysicsContext
                || (LogicTickActive && !onMain)
                || (LoadingLevel && !onMain);
        }

        /// <summary>
        /// Seed stashed by the session/verifier before LoadSaveFileAndEnter. The counter must not be
        /// calibrated until the async loader finishes (see OnLevelLoadComplete), because at load start
        /// DXGame.Current.Entities still holds the OLD level and the save's GameTime is not restored.
        /// </summary>
        public static int PendingLoadSeed { get; private set; } = 1337;
        public static bool HasPendingPostLoadCalibration { get; private set; }

        /// <summary>Host's synced entity-id counter at snapshot time; applied to the Client after load.</summary>
        public static long PendingSnapshotEntityIdCounter { get; private set; }

        public static long CurrentSyncedEntityIdCounter => _syncedEntityIdCounter;

        /// <summary>Adopt the Host's entity-id counter captured in the snapshot header.</summary>
        public static void SetSyncedEntityIdCounter(long value)
        {
            if (value > 0) _syncedEntityIdCounter = value;
        }

        /// <summary>Record the current (render/frame) thread for load-vs-UI thread classification.</summary>
        public static void CaptureMainThread() => MainThreadId = Thread.CurrentThread.ManagedThreadId;

        public static void EnableDeterministicSandbox(int initialSeed, long startingEntityIdCounter = 100000L)
        {
            _syncedRandomSeed = initialSeed;
            _syncedEntityIdCounter = startingEntityIdCounter;
            _syncedGlobalRandom = new DXRandom(initialSeed);
            _auxGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x5EEDBEEF));
            _renderGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x1234ABCD));
            _syncedSceneRandom = new DXRandom(unchecked(initialSeed ^ 0x517CC1B7));

            // The parameterless-ctor seed counter must restart together with the shared RNG, otherwise
            // a run that allocated a different number of DXRandom instances before syncing starts
            // would get a permanently shifted seed sequence.
            DXRandomCtorPatch.ResetCounter();

            // Keep vanilla CMovable on the FindPathConcurrent branch instead of its buggy
            // IsMultiplayer branch (hardcodes cellSize=1 and recurses infinitely on null paths);
            // DeterministicPathfindingPatch executes FindPathConcurrent synchronously and preserves cellSize.
            SetEngineMultiplayerMode(false);

            // Set IsIADeterministic AFTER SetEngineMultiplayerMode(false), because SetPlayerMode
            // overwrites DXGame.IsIADeterministic = (mode == Multiplayer)!
            DXGame.IsIADeterministic = true;
            AllowPhysicsThisFrame = false;
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
            InPhysicsContext = false;
            InPhysicsStep = false;
            AllowPhysicsThisFrame = false;
            LoadingLevel = false;
            HasPendingPostLoadCalibration = false;
            PendingSnapshotEntityIdCounter = 0;
            PausedBySession = false;
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

        private static DXRandom _syncedSceneRandom = new DXRandom(0x517CC1B7);

        /// <summary>
        /// Dedicated synced RNG backing DXScene.GlobalRandom (and DXLinq.RandomElement / DXHelper_Random)
        /// during deterministic simulation and level loading, keeping it isolated from render-thread draws.
        /// </summary>
        public static DXRandom GetSyncedSceneRandom()
        {
            if (_syncedSceneRandom == null)
            {
                _syncedSceneRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x517CC1B7));
            }
            return _syncedSceneRandom;
        }

        public static int SyncedRandomSeed => _syncedRandomSeed;

        private static int _behaviourResetGeneration;
        [ThreadStatic] private static int _behaviourResetGenerationOnThread;
        private static readonly Dictionary<Type, DXRandom> _sharedBehaviourRandoms = new Dictionary<Type, DXRandom>();
        private static readonly object _behaviourSync = new object();

        /// <summary>
        /// DXBehaviour&lt;TData&gt;._GlobalRandom is [ThreadStatic], and DXGame.UpdateGameLogic runs each
        /// tick's _TaskLogic on an arbitrary ThreadPool worker thread. Giving each worker its own new
        /// DXRandom(seed) would restart or interleave per-thread sequences whenever ThreadPool picks a
        /// different worker across ticks/processes. Bind every worker thread to the SAME per-type shared
        /// DXRandom instance (safe because IsIADeterministic runs _TaskLogic sequentially under WaitForGameLogic).
        /// </summary>
        public static void EnsureBehaviourRandomOnCurrentThread()
        {
            int gen = Volatile.Read(ref _behaviourResetGeneration);
            if (_behaviourResetGenerationOnThread == gen) return;
            _behaviourResetGenerationOnThread = gen;

            try
            {
                lock (_behaviourSync)
                {
                    if (_sharedBehaviourRandoms.Count == 0) return;
                    foreach (var kv in _sharedBehaviourRandoms)
                    {
                        try
                        {
                            AccessTools.Field(kv.Key, "_Seed")?.SetValue(null, _syncedRandomSeed);
                            AccessTools.Field(kv.Key, "_GlobalRandom")?.SetValue(null, kv.Value);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] EnsureBehaviourRandomOnCurrentThread failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the shared deterministic DXRandom instance for every closed DXBehaviour&lt;TData&gt;
        /// generic type and bumps the generation counter so the active _TaskLogic thread rebinds to it.
        /// </summary>
        public static void ResetBehaviourRandom(int seed)
        {
            try
            {
                var asm = Core.GameBridge.GameAssembly;
                if (asm == null) return;

                Type open = typeof(DXBehaviour<>);
                Type baseData = typeof(DXBehaviourData);
                int resetCount = 0;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }

                lock (_behaviourSync)
                {
                    _sharedBehaviourRandoms.Clear();
                    int idx = 0;
                    foreach (var t in types)
                    {
                        if (t == null || t.IsAbstract || t.IsGenericTypeDefinition || !baseData.IsAssignableFrom(t))
                            continue;
                        try
                        {
                            Type closed = open.MakeGenericType(t);
                            var shared = new DXRandom(unchecked(seed ^ (idx * 0x45D9F3B)));
                            _sharedBehaviourRandoms[closed] = shared;
                            AccessTools.Field(closed, "_Seed")?.SetValue(null, seed);
                            AccessTools.Field(closed, "_GlobalRandom")?.SetValue(null, shared);
                            idx++;
                            resetCount++;
                        }
                        catch { /* closed generic not constructible for this T — skip */ }
                    }
                }

                ModLogger.Info($"[LockstepClock] Reset DXBehaviour<*>.GlobalRandom (types={resetCount}, seed={seed}).");
                Interlocked.Increment(ref _behaviourResetGeneration);
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
        /// Resets shared RNG streams right at the start of ZXSystemGameLevel.InitLevel so
        /// MapDrawer terrain generation always sees the exact same deterministic sequence regardless
        /// of draws made during ReadSaveFile.
        /// </summary>
        public static void ResetSyncedRandomStreamsForInitLevel()
        {
            _syncedGlobalRandom = new DXRandom(_syncedRandomSeed);
            _auxGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x5EEDBEEF));
            _syncedSceneRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x517CC1B7));
            DXRandomCtorPatch.ResetCounter();
        }

        /// <summary>
        /// Called before LoadSaveFileAndEnter. The actual calibration is deferred to the level-load
        /// completion postfix, when the async loader has restored every entity and GameTime.
        /// </summary>
        public static void RequestPostLoadCalibration(int seed, long snapshotEntityIdCounter = 0)
        {
            PendingLoadSeed = seed;
            PendingSnapshotEntityIdCounter = snapshotEntityIdCounter;
            HasPendingPostLoadCalibration = true;
            LoadingLevel = true;
            AllowPhysicsThisFrame = false;
            DXGame.LastGameTimeIAUpdate = 0.0;
            DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0;
        }

        /// <summary>
        /// Applies the full deterministic baseline to a loaded level (synced entity ID counter,
        /// sorted entity/physics lists, seeded RNGs, cleared path cache, aligned logic/physics
        /// clocks, normalized CMovable speeds & body velocity/force, and frozen camera intro).
        /// Called symmetrically on both Host and Client.
        /// </summary>
        public static void ApplyDeterministicLevelBaseline(int seed, long snapshotCounter = 0)
        {
            CalibrateEntityIdCounterFromLoadedLevel(seed);

            // A Client adopts the Host's snapshot-time counter so entities spawned on either side
            // after the snapshot share one ID space. Only move forward, never rewind below loaded IDs.
            if (snapshotCounter > _syncedEntityIdCounter)
                _syncedEntityIdCounter = snapshotCounter;
            LoadingLevel = false;

            GameBridge.SortEntitiesById();
            ResetColonistSystem();
            NormalizeMovablesAndPhysicsState();
            GameBridge.SeedLevelRandom(seed);
            ResetBehaviourRandom(seed);
            DXWorldGrid.ResetPathsCached();
            GameBridge.AlignSimulationClocks();
            // The 6s opening camera pan is wall-clock driven; on two processes it lands on
            // different frames, changing which cells are "watched" (and thus visibility-driven
            // RNG/sprite work). Freeze it to the save's camera for a reproducible world.
            GameBridge.DisableCameraIntroAnimation();
        }

        /// <summary>
        /// Resets SysColonists' round-robin building cursor and rebuilds its building list in sorted
        /// entity order so wandering colonists pick identical destinations across Host and Client.
        /// </summary>
        private static void ResetColonistSystem()
        {
            try
            {
                var tSys = Core.GameBridge.GameAssembly?.GetType(Core.ObfuscatedSymbols.Type_SysColonists);
                if (tSys == null) return;
                object inst = AccessTools.Method(tSys, Core.ObfuscatedSymbols.Method_SysColonists_GetCurrent)?.Invoke(null, null);
                if (inst == null) return;

                var fCursor = AccessTools.Field(tSys, Core.ObfuscatedSymbols.Field_SysColonists_Cursor);
                int prevCursor = fCursor != null ? (int)fCursor.GetValue(inst) : -1;
                fCursor?.SetValue(inst, 0);
                AccessTools.Method(tSys, Core.ObfuscatedSymbols.Method_SysColonists_RebuildList)?.Invoke(inst, null);

                int count = 0;
                var list = AccessTools.Field(tSys, Core.ObfuscatedSymbols.Field_SysColonists_List)?.GetValue(inst) as System.Collections.IList;
                if (list != null) count = list.Count;
                ModLogger.Info($"[LockstepClock] Reset SysColonists (prevCursor={prevCursor}, buildings={count}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] ResetColonistSystem failed: {ex.Message}");
            }
        }

        private static bool _movableMetaInit;
        private static Type _typeCMovable;
        private static FieldInfo _fMovableCachedSpeed;
        private static MethodInfo _mMovableComputeSpeed;

        private static void NormalizeMovablesAndPhysicsState()
        {
            try
            {
                var game = DXGame.Current;
                if (game?.Entities == null) return;

                if (!_movableMetaInit)
                {
                    _movableMetaInit = true;
                    _typeCMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
                    if (_typeCMovable != null)
                    {
                        _fMovableCachedSpeed = _typeCMovable.GetField("#=zLnAE$gO8EOG2",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _mMovableComputeSpeed = AccessTools.Method(_typeCMovable, "#=zRwhr6_cVbBr_", Type.EmptyTypes);
                    }
                }

                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;

                    if (_typeCMovable != null && _fMovableCachedSpeed != null && _mMovableComputeSpeed != null && e.HasComponents)
                    {
                        var comps = e.Components;
                        if (comps != null)
                        {
                            for (int c = 0; c < comps.Count; c++)
                            {
                                var comp = comps[c];
                                if (comp != null && _typeCMovable.IsInstanceOfType(comp))
                                {
                                    float spd = (float)_mMovableComputeSpeed.Invoke(comp, null);
                                    _fMovableCachedSpeed.SetValue(comp, spd);
                                    break;
                                }
                            }
                        }
                    }

                    var body = e.Body;
                    if (body != null && body.Handle != IntPtr.Zero && e.PhysicalType != DXBodyType.Static)
                    {
                        body.PhysicVelocity = PointF.Empty;
                        body.PhysicForce = PointF.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] NormalizeMovablesAndPhysicsState failed: {ex.Message}");
            }
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
                long snapshotCounter = PendingSnapshotEntityIdCounter;
                HasPendingPostLoadCalibration = false;
                PendingSnapshotEntityIdCounter = 0;

                if (Sync.CoopSessionManager.IsSessionActive)
                {
                    bool isHost = Sync.CoopSessionManager.Transport != null && Sync.CoopSessionManager.Transport.IsHost;
                    if (isHost)
                    {
                        if (!Sync.CoopSessionManager.IsHostSnapshotReloadPending)
                        {
                            // Initial Host load before exporting coop_host_snapshot.zxsav: calibrate ID counter
                            // without mutating DXLevel.Current.Random or level state prior to serialization.
                            CalibrateEntityIdCounterFromLoadedLevel(seed);
                            LoadingLevel = false;
                            PausedBySession = true;
                            var initHostGame = DXGame.Current;
                            if (initHostGame != null && initHostGame.Paused)
                                initHostGame.Paused = false;
                            ModLogger.Info($"[LockstepClock] Host initial level load complete: counter={_syncedEntityIdCounter}, holding paused for snapshot export.");
                            return;
                        }

                        ApplyDeterministicLevelBaseline(seed, snapshotCounter);
                        PausedBySession = true;
                        var hostGame = DXGame.Current;
                        if (hostGame != null && hostGame.Paused)
                            hostGame.Paused = false;
                        GameBridge.ClearWaitCursor();
                        ModLogger.Info($"[LockstepClock] Host snapshot reload complete: baseline applied (seed={seed}, counter={_syncedEntityIdCounter}).");
                        return;
                    }

                    // Client: apply baseline and keep paused until Host sends SessionStart.
                    ApplyDeterministicLevelBaseline(seed, snapshotCounter);
                    PausedBySession = true;
                    var clientGame = DXGame.Current;
                    if (clientGame != null && clientGame.Paused)
                    {
                        clientGame.Paused = false;
                        ModLogger.Info("[LockstepClock] Cleared load-time pause on Client (session remains paused until SessionStart).");
                    }
                    GameBridge.ClearWaitCursor();
                    ModLogger.Info($"[LockstepClock] Client level load complete: ID counter -> {_syncedEntityIdCounter}, seed={seed}, waiting for SessionStart.");
                    return;
                }

                ApplyDeterministicLevelBaseline(seed, snapshotCounter);

                // The engine's save loader leaves DXGame.Paused = true (only the "Continue" button
                // clears it). Our fixed-step clock honors Paused by freezing, so a programmatic load
                // would never tick. Restore the running state here.
                PausedBySession = false;
                var game = DXGame.Current;
                if (game != null && game.Paused)
                {
                    game.Paused = false;
                    ModLogger.Info("[LockstepClock] Cleared load-time pause at level completion.");
                }
                GameBridge.ClearWaitCursor();

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

            // During (async) save loading or pure-visual VFX particle creation, generated IDs belong
            // to local/transient objects and must never consume the shared synced ID counter.
            if (LockstepClock.LoadingLevel || LockstepClock.InVisualVfxContext)
            {
                __result = LockstepClock.NextLocalUiGuid();
                return false;
            }

            if (LockstepClock.InDeterministicContext || LockstepClock.LogicTickActive || LockstepClock.InPhysicsContext)
                __result = LockstepClock.NextSyncedGuid();
            else
                __result = LockstepClock.NextLocalUiGuid();

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
            // never be handed out while the sandbox is active. Only the logic worker and the level-
            // loading worker may draw from the shared synced stream: the main/render thread runs
            // menu animations and loader fades whose frame counts differ between processes, and
            // feeding those into the synced stream shifted every later draw (the A/B attractor).
            int main = LockstepClock.MainThreadId;
            bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;

            if (LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
                __result = LockstepClock.GetRenderGlobalRandom();
            else if (LockstepClock.InDeterministicContext || (LockstepClock.LogicTickActive && !onMain)
                || (LockstepClock.LoadingLevel && !onMain))
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
        public static bool UpdateGameLogic_Prefix(DXGame __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;

            // Never touch GameTime or run logic during MainMenu or async level loading:
            // MainMenu ticks would advance LastGameTimeIAUpdate to wall-clock ~15s and later
            // clobber the save's restored GameTime while ReadSaveFile/InitLevel runs on T9/T11.
            if (LockstepClock.LoadingLevel || DXScene.Current == null || !(DXScene.Current.RootObject is DXIsometricGridObject))
            {
                LockstepClock.ResetHostTickPacing();
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            // Background windows report Focused=false, and some engine paths branch on focus/render
            // state. Force the flags true so a second (unfocused) process cannot take a different
            // branch from the first one.
            if (!__instance.Focused) __instance.Focused = true;
            if (!__instance.Visible) __instance.Visible = true;

            if (DXGame.LastGameTimeIAUpdate == 0.0)
                DXGame.LastGameTimeIAUpdate = __instance.GameTime;

            int nextTick = __instance.IAIterationInt;
            double boundary = DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick;
            bool ready = LockstepClock.TickGate == null || LockstepClock.TickGate.IsTickReady(nextTick);

            if (!ready)
            {
                __instance.GameTime = DXGame.LastGameTimeIAUpdate;
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            // Network lockstep: pace live Host simulation ticks to real wall-clock time * GameSpeed
            // (so 1.0x runs at 30 Hz instead of render FPS), clear any lingering Wait cursor lock,
            // and apply the turn's buffered commands at its exact tick boundary.
            if (Sync.CoopSessionManager.IsInSyncedLevel)
            {
                if (__instance.CursorLocked && !Core.GameBridge.IsGameLoading())
                    Core.GameBridge.ClearWaitCursor();

                bool isHost = Sync.CoopSessionManager.Transport != null && Sync.CoopSessionManager.Transport.IsHost;
                if (!LockstepClock.PausedBySession && isHost)
                {
                    if (!LockstepClock.ShouldAdvanceHostRealTimeTick(__instance.GameSpeed))
                    {
                        __instance.GameTime = DXGame.LastGameTimeIAUpdate;
                        LockstepClock.AllowPhysicsThisFrame = false;
                        return false;
                    }
                }
                else
                {
                    LockstepClock.ResetHostTickPacing();
                }

                Sync.CoopSessionManager.FlushDueTurnCommands(nextTick);
            }
            else
            {
                LockstepClock.ResetHostTickPacing();
            }

            // Honor a synchronized pause: after flushing this frame's commands, freeze SimTick and
            // GameTime while LockstepClock.PausedBySession is set. The engine's own DXGame.Paused is
            // NOT used here — the save loader leaves it set (and re-sets it) for its own purposes.
            if (LockstepClock.PausedBySession)
            {
                __instance.GameTime = DXGame.LastGameTimeIAUpdate;
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            LockstepClock.LogicTickActive = true;
            LockstepClock.InDeterministicContext = true;
            LockstepClock.AllowPhysicsThisFrame = true;
            __instance.GameTime = boundary;
            return true;
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
        // render-phase draws cannot perturb, and so DeterministicTaskPatch only serializes the two
        // position-affecting For-loops (not scene culling / texture baking / level tables).
        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPrefix]
        public static bool UpdatePhysics_Enter()
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            if (!LockstepClock.AllowPhysicsThisFrame || LockstepClock.LoadingLevel || LockstepClock.PausedBySession)
                return false;

            LockstepClock.AllowPhysicsThisFrame = false;
            LockstepClock.InPhysicsContext = true;
            LockstepClock.InPhysicsStep = true;
            return true;
        }

        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPostfix]
        public static void UpdatePhysics_Exit()
        {
            bool didStep = LockstepClock.InPhysicsStep;
            LockstepClock.InPhysicsContext = false;
            LockstepClock.InPhysicsStep = false;

            if (LockstepClock.IsDeterministicModeActive)
            {
                DXPhysicWorld.TimeLastBodyPosition = DXPhysicWorld.TimeBodyPosition;
                if (didStep && !LockstepClock.LoadingLevel)
                    CSpritePullIsolationPatch.RunDeterministicPullUpdates(DXGame.Current);
            }
        }

        // Prevent DXEntity.UpdateSceneObjectParallel (called only for entities inside the local
        // camera viewport during SetCamera) from mutating authoritative Entity.Position and
        // triggering DXWorldGrid.SetNewCellFor on parallel render threads via InterpolatedPosition.
        [HarmonyPatch(typeof(DXPhysicBody), nameof(DXPhysicBody.InterpolatedPosition), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool InterpolatedPosition_Prefix(DXPhysicBody __instance, ref PointF __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            __result = __instance.PositionComputed;
            return false;
        }

        // Remember which thread runs the frame loop; DXRandomCtorPatch uses it to keep the
        // level-loading worker's seed sequence synced while the UI thread stays off that counter.
        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPrefix]
        public static void StartFrame_CaptureMainThread()
        {
            if (LockstepClock.MainThreadId == 0) LockstepClock.CaptureMainThread();
        }
    }

    /// <summary>
    /// Replaces background-thread pathfinding (DXWorldGrid.FindPathConcurrent / HighPriority) with
    /// deterministic synchronous execution on the active logic thread when deterministic mode is active.
    /// This eliminates background worker scheduling races and avoids vanilla's buggy IsMultiplayer branch
    /// in CMovable (which hardcodes cellSize=1 and recurses infinitely on null paths).
    /// </summary>
    [HarmonyPatch(typeof(DXWorldGrid))]
    public static class DeterministicPathfindingPatch
    {
        private static FieldInfo _movableRequestField;

        [HarmonyPatch(nameof(DXWorldGrid.FindPathConcurrent))]
        [HarmonyPrefix]
        public static bool FindPathConcurrent_Prefix(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes,
            bool useCache)
        {
            if (!LockstepClock.IsDeterministicModeActive)
            {
                pfr = null;
                return true;
            }

            RunSyncPathfinding(out pfr, cellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes);
            return false;
        }

        [HarmonyPatch(nameof(DXWorldGrid.FindPathConcurrentWithHighPriority))]
        [HarmonyPrefix]
        public static bool FindPathConcurrentWithHighPriority_Prefix(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes,
            bool useCache)
        {
            if (!LockstepClock.IsDeterministicModeActive)
            {
                pfr = null;
                return true;
            }

            RunSyncPathfinding(out pfr, cellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes);
            return false;
        }

        private static void RunSyncPathfinding(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes)
        {
            int safeCellSize = cellSize < 1 ? 1 : (cellSize > 3 ? 3 : cellSize);
            var req = new DXWorldGrid.PathFindingRequest(safeCellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes, false);
            pfr = req;

            // Mirror CMovable.#=ziMavkfK9ZJji before invoking onPathFound so #=zxg3EOEU9RJ22's
            // reference check (`request == #=ziMavkfK9ZJji`) passes even if Harmony stages `out pfr`.
            if (userData != null)
            {
                if (_movableRequestField == null)
                    _movableRequestField = AccessTools.Field(userData.GetType(), ObfuscatedSymbols.Field_CMovable_PendingPathRequest);
                _movableRequestField?.SetValue(userData, req);
            }

            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Computing;
            req.Path = DXWorldGrid.FindPath(safeCellSize, p1, p2, mask, nearPath, maxNodes);
            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Computed;
            onPathFound?.Invoke(req);
            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Finished;

            // If no path was found, #=zxg3EOEU9RJ22 clears #=ziMavkfK9ZJji; keep `out pfr` null as well.
            if (req.Path == null)
                pfr = null;
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

    /// <summary>
    /// DXEntity.Update runs on the deterministic logic worker thread (single-threaded branch in
    /// DXGame.UpdateGameLogic). Use it to re-seed that thread's [ThreadStatic] behaviour RNG once
    /// per level load before any behaviour node executes.
    /// </summary>
    [HarmonyPatch(typeof(DXEntity), "Update")]
    public static class EntityUpdateBehaviourRandomPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            if (LockstepClock.IsDeterministicModeActive)
                LockstepClock.EnsureBehaviourRandomOnCurrentThread();
        }
    }

    /// <summary>
    /// Logs any engine message-box alert raised while deterministic mode is active. The save loader
    /// reports deserialization failures this way (no exception escapes), so a silent Client-side
    /// load failure shows up here.
    /// </summary>
    [HarmonyPatch]
    public static class MessageBoxAlertDiagPatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowAlert,
                new[] { typeof(string), typeof(string), typeof(Action) });

        [HarmonyPrefix]
        public static void Prefix(string __0, string __1)
        {
            if (LockstepClock.IsDeterministicModeActive)
                ModLogger.Warn($"[MsgBox] {__0}: {__1}");
        }
    }

    /// <summary>
    /// DXGame.IsFrameSceneModule(n) is true when the RENDER frame counter is a multiple of n. Two
    /// processes render at different rates (focused vs background window), so any logic or UI action
    /// gated on it drifts apart. In deterministic mode, gate on the logic tick counter instead.
    /// </summary>
    [HarmonyPatch(typeof(DXGame), "IsFrameSceneModule")]
    public static class FrameSceneModuleDeterminismPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int iteration, ref bool __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            var game = DXGame.Current;
            __result = iteration > 0 && game != null && (game.IAIterationInt % iteration) == 0;
            return false;
        }
    }

    /// <summary>
    /// Preserves deserialized in-flight projectile kinematics across ZX.Entities.Proyectile.OnSceneAdded.
    /// On load, OnSceneAdded advances Arrow Origin by +0.3f, overwrites Position = Origin, and
    /// recomputes DTime/DX/DY. A Host-to-Client snapshot load runs OnSceneAdded a second time unless
    /// preserved, mutating in-flight projectiles before tick 0. Newly spawned projectiles enter
    /// OnSceneAdded with DTime == 0f; save-restored in-flight projectiles already have DTime > 0f.
    /// </summary>
    [HarmonyPatch]
    public static class ProyectileLoadPreservePatch
    {
        public sealed class ProyectileSavedState
        {
            public PointF Origin;
            public PointF Position;
            public PointF LastPosition;
            public float H;
            public float LastH;
            public float OriginH;
            public float Distance;
            public float DistanceH;
            public float DX;
            public float DY;
            public float DH;
            public float DTime;
            public float FactorTime;
            public float HFactor;
        }

        private static bool _metaInit;
        private static PropertyInfo _pOrigin, _pPosition, _pLastPosition;
        private static PropertyInfo _pH, _pLastH, _pOriginH, _pDistance, _pDistanceH;
        private static PropertyInfo _pDX, _pDY, _pDH, _pDTime, _pFactorTime, _pHFactor;

        private static void EnsureMeta(Type t)
        {
            if (_metaInit || t == null) return;
            _metaInit = true;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            _pOrigin = t.GetProperty("Origin", F);
            _pPosition = t.GetProperty("Position", F);
            _pLastPosition = t.GetProperty("LastPosition", F);
            _pH = t.GetProperty("H", F);
            _pLastH = t.GetProperty("LastH", F);
            _pOriginH = t.GetProperty("OriginH", F);
            _pDistance = t.GetProperty("Distance", F);
            _pDistanceH = t.GetProperty("DistanceH", F);
            _pDX = t.GetProperty("DX", F);
            _pDY = t.GetProperty("DY", F);
            _pDH = t.GetProperty("DH", F);
            _pDTime = t.GetProperty("DTime", F);
            _pFactorTime = t.GetProperty("FactorTime", F);
            _pHFactor = t.GetProperty("HFactor", F);
        }

        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType("ZX.Entities.Proyectile");
            EnsureMeta(t);
            return t != null ? AccessTools.Method(t, "OnSceneAdded", Type.EmptyTypes) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, ref ProyectileSavedState __state)
        {
            __state = null;
            if (!LockstepClock.IsDeterministicModeActive || __instance == null) return;
            EnsureMeta(__instance.GetType());
            if (_pDTime == null) return;

            float dTime = (float)_pDTime.GetValue(__instance);
            if (dTime <= 0f) return;

            __state = new ProyectileSavedState
            {
                Origin = _pOrigin != null ? (PointF)_pOrigin.GetValue(__instance) : PointF.Empty,
                Position = _pPosition != null ? (PointF)_pPosition.GetValue(__instance) : PointF.Empty,
                LastPosition = _pLastPosition != null ? (PointF)_pLastPosition.GetValue(__instance) : PointF.Empty,
                H = _pH != null ? (float)_pH.GetValue(__instance) : 0f,
                LastH = _pLastH != null ? (float)_pLastH.GetValue(__instance) : 0f,
                OriginH = _pOriginH != null ? (float)_pOriginH.GetValue(__instance) : 0f,
                Distance = _pDistance != null ? (float)_pDistance.GetValue(__instance) : 0f,
                DistanceH = _pDistanceH != null ? (float)_pDistanceH.GetValue(__instance) : 0f,
                DX = _pDX != null ? (float)_pDX.GetValue(__instance) : 0f,
                DY = _pDY != null ? (float)_pDY.GetValue(__instance) : 0f,
                DH = _pDH != null ? (float)_pDH.GetValue(__instance) : 0f,
                DTime = dTime,
                FactorTime = _pFactorTime != null ? (float)_pFactorTime.GetValue(__instance) : 0f,
                HFactor = _pHFactor != null ? (float)_pHFactor.GetValue(__instance) : 0f,
            };
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, ProyectileSavedState __state)
        {
            if (__state == null || __instance == null) return;

            _pOrigin?.SetValue(__instance, __state.Origin);
            _pLastPosition?.SetValue(__instance, __state.LastPosition);
            _pPosition?.SetValue(__instance, __state.Position);
            _pH?.SetValue(__instance, __state.H);
            _pLastH?.SetValue(__instance, __state.LastH);
            _pOriginH?.SetValue(__instance, __state.OriginH);
            _pDistance?.SetValue(__instance, __state.Distance);
            _pDistanceH?.SetValue(__instance, __state.DistanceH);
            _pDX?.SetValue(__instance, __state.DX);
            _pDY?.SetValue(__instance, __state.DY);
            _pDH?.SetValue(__instance, __state.DH);
            _pDTime?.SetValue(__instance, __state.DTime);
            _pFactorTime?.SetValue(__instance, __state.FactorTime);
            _pHFactor?.SetValue(__instance, __state.HFactor);
        }
    }

    /// <summary>
    /// Deterministically refreshes CSprite animation progress (#=zbBN4HvXVWuDv) right before
    /// CMovable logic/physics force computation. Vanilla only updates #=zbBN4HvXVWuDv inside
    /// UpdateSceneObjectParallel for entities visible in the local camera viewport, yet CMovable
    /// reads it for zombie gait acceleration and harpy jump timing on all entities.
    /// </summary>
    [HarmonyPatch]
    public static class CMovableSpriteProgressSyncPatch
    {
        private static bool _metaInit;
        private static MethodInfo _mGetSprite;
        private static FieldInfo _fCurAnim;
        private static PropertyInfo _pTimeAnimStart;
        private static FieldInfo _fAnimProgress;
        private static MethodInfo _mAnimProgress;

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            var asm = GameBridge.GameAssembly;
            if (asm == null) return;

            Type tEntity = GameBridge.TypeZXEntity;
            Type tSprite = asm.GetType("ZX.Components.CSprite");
            Type tSpriteAnim = asm.GetType("ZX.Components.SpriteAnimation");

            if (tEntity != null)
                _mGetSprite = AccessTools.Method(tEntity, "#=zbkLIvkCtqwewx0xa2A==", Type.EmptyTypes);
            if (tSprite != null)
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fCurAnim = tSprite.GetField("#=za7pUzvqbWjrs", F);
                _pTimeAnimStart = tSprite.GetProperty("TimeAnimationStart", F);
                _fAnimProgress = tSprite.GetField("#=zbBN4HvXVWuDv", F);
            }
            if (tSpriteAnim != null)
                _mAnimProgress = AccessTools.Method(tSpriteAnim, "#=z_y5dBgkM5Q4t", new[] { typeof(int) });
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            EnsureMeta();
            Type tMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
            if (tMovable == null) yield break;

            var mLogic = AccessTools.Method(tMovable, "#=z5GPRaBw=", Type.EmptyTypes);
            if (mLogic != null) yield return mLogic;

            var mPhysics = AccessTools.Method(tMovable, "#=zkgQp3Dx6LolarfpKKQ==", Type.EmptyTypes);
            if (mPhysics != null) yield return mPhysics;
        }

        [HarmonyPrefix]
        public static void Prefix(DXComponent __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive || __instance == null) return;
            var entity = __instance.Entity;
            var game = DXGame.Current;
            if (entity == null || game == null) return;

            EnsureMeta();
            if (_mGetSprite == null || _fCurAnim == null || _pTimeAnimStart == null || _fAnimProgress == null || _mAnimProgress == null)
                return;
            if (!_mGetSprite.DeclaringType.IsInstanceOfType(entity))
                return;

            object csprite = _mGetSprite.Invoke(entity, null);
            if (csprite == null) return;

            object animBox = _fCurAnim.GetValue(csprite);
            if (animBox == null) return;

            int animStart = (int)_pTimeAnimStart.GetValue(csprite);
            int elapsed = game.GameTimeInt - animStart;
            float progress = (float)_mAnimProgress.Invoke(animBox, new object[] { elapsed });
            _fAnimProgress.SetValue(csprite, progress);
        }
    }

    /// <summary>
    /// Redirects calls made directly on the static field DXScene.GlobalRandom (e.g. DXLinq.RandomElement,
    /// DXLinq.Shuffle, and main-thread DXHelper_Random) to a deterministic stream during simulation and
    /// loading, while routing render/UI draws to the isolated render stream.
    /// </summary>
    [HarmonyPatch(typeof(DXRandom), "genrand_uint32")]
    public static class SceneGlobalRandomInstancePatch
    {
        private static readonly DXRandom _sceneGlobalSentinel = DXScene.GlobalRandom;

        [HarmonyPrefix]
        public static bool Prefix(DXRandom __instance, ref uint __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            if (!object.ReferenceEquals(__instance, _sceneGlobalSentinel)) return true;

            __result = LockstepClock.ShouldUseSyncedSceneRandom()
                ? LockstepClock.GetSyncedSceneRandom().NextUInt()
                : LockstepClock.GetRenderGlobalRandom().NextUInt();
            return false;
        }
    }

    /// <summary>
    /// Ensures DXHelper_Random.Random follows the same deterministic vs render bifurcation as
    /// DXScene.GlobalRandom even on worker threads where [ThreadStatic] _Random is initially null.
    /// </summary>
    [HarmonyPatch(typeof(DXHelper_Random), "Random", MethodType.Getter)]
    public static class HelperRandomGetterPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref DXRandom __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;

            __result = LockstepClock.ShouldUseSyncedSceneRandom()
                ? LockstepClock.GetSyncedSceneRandom()
                : LockstepClock.GetRenderGlobalRandom();
            return false;
        }
    }

    /// <summary>
    /// Isolates synchronous ZXSystemAudio calls from the deterministic PRNG streams. Audio
    /// playback checks local camera frustum and singleton caches (sound-list shuffle dictionary),
    /// so letting it draw from DXRandom.Global or DXScene.GlobalRandom desyncs Host vs Client.
    /// </summary>
    [HarmonyPatch]
    public static class AudioRngIsolationPatch
    {
        private static readonly string[] TargetNames =
        {
            Core.ObfuscatedSymbols.Method_Audio_PickFromList,
            Core.ObfuscatedSymbols.Method_Audio_PickWeightedVoice,
            Core.ObfuscatedSymbols.Method_Audio_StartAmbient,
            Core.ObfuscatedSymbols.Method_Audio_PlayUnique,
            Core.ObfuscatedSymbols.Method_Audio_PlayEntitySound,
            Core.ObfuscatedSymbols.Method_Audio_PlayPosSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayPosSound3D,
            Core.ObfuscatedSymbols.Method_Audio_Play2DSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayNamed2DSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayNotification,
        };

        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var audioType = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXSystemAudio);
            if (audioType == null) yield break;

            var nameSet = new System.Collections.Generic.HashSet<string>(TargetNames, StringComparer.Ordinal);
            foreach (var m in AccessTools.GetDeclaredMethods(audioType))
            {
                if (m != null && !m.IsAbstract && nameSet.Contains(m.Name))
                    yield return m;
            }
        }

        [HarmonyPrefix]
        public static void Prefix() => LockstepClock.EnterAudioContext();

        [HarmonyFinalizer]
        public static void Finalizer() => LockstepClock.ExitAudioContext();
    }

    /// <summary>
    /// Replaces BHFarmer's farm-cell picker (#=zK2CGzIZdW6uI$Q9jPNxMDPs=) in deterministic mode so
    /// it selects grass cells around the owner Farm from deterministic terrain + building occupancy
    /// (TerrainResourceCells == Grass and !AnyEntityAtCell(..., 2048u)) rather than querying
    /// CFarmer's visual Wheat_A DXEntities (which spawn via InvokeOnStartFrame and fade out over
    /// 1000ms of render-frame animation time before removal from DXWorldGrid).
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicFarmerTargetPatch
    {
        private static bool _metaInit;
        private static MethodInfo _mBDataOwner;
        private static PropertyInfo _pBDataTargetPos;
        private static PropertyInfo _pRefBuildingOwner;
        private static PropertyInfo _pRefEntity;
        private static MethodInfo _mGetParams;
        private static PropertyInfo _pCollectRadius;
        private static MethodInfo _mTerrainGetCurrent;
        private static PropertyInfo _pTerrainCells;

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            var asm = GameBridge.GameAssembly;
            if (asm == null) return;

            var tBData = asm.GetType("ZX.Behaviours.BData");
            if (tBData != null)
            {
                _mBDataOwner = AccessTools.Method(tBData, Core.ObfuscatedSymbols.Method_BData_GetOwner, Type.EmptyTypes);
                _pBDataTargetPos = AccessTools.Property(tBData, "TargetPosition");
            }

            var tColonist = asm.GetType("ZX.Entities.Colonist");
            if (tColonist != null)
            {
                _pRefBuildingOwner = AccessTools.Property(tColonist, "RefBuildingOwner");
                if (_pRefBuildingOwner != null)
                    _pRefEntity = AccessTools.Property(_pRefBuildingOwner.PropertyType, "Entity");
            }

            var tZxEntity = asm.GetType("ZX.Entities.ZXEntity");
            if (tZxEntity != null)
                _mGetParams = AccessTools.Method(tZxEntity, Core.ObfuscatedSymbols.Method_ZXEntity_GetParams, Type.EmptyTypes);

            var tParams = _mGetParams?.ReturnType;
            if (tParams != null)
                _pCollectRadius = AccessTools.Property(tParams, "ResourceCollectionRadius");

            var tTerrain = asm.GetType(Core.ObfuscatedSymbols.Type_TerrainSystem);
            if (tTerrain != null)
            {
                _mTerrainGetCurrent = AccessTools.Method(tTerrain, Core.ObfuscatedSymbols.Method_TerrainSystem_GetCurrent, Type.EmptyTypes);
                _pTerrainCells = AccessTools.Property(tTerrain, Core.ObfuscatedSymbols.Prop_TerrainSystem_Cells);
            }
        }

        public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var tBeh = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXBehaviour);
            var fNode = tBeh != null
                ? tBeh.GetField(Core.ObfuscatedSymbols.Field_ZXBehaviour_PickFarmCell, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                : null;
            object node = fNode?.GetValue(null);
            if (node == null) yield break;

            var del = AccessTools.Property(node.GetType(), "OnExecute")?.GetValue(node) as Delegate;
            if (del?.Method != null) yield return del.Method;
        }

        [HarmonyPrefix]
        public static bool Prefix(object __0, ref IARes __result)
        {
            if (!LockstepClock.IsDeterministicModeActive || __0 == null) return true;
            EnsureMeta();
            if (_mBDataOwner == null || _pBDataTargetPos == null || _pRefBuildingOwner == null ||
                _pRefEntity == null || _mGetParams == null || _pCollectRadius == null ||
                _mTerrainGetCurrent == null || _pTerrainCells == null)
            {
                return true;
            }

            var colonist = _mBDataOwner.Invoke(__0, null);
            if (colonist == null) return true;

            var ownerRef = _pRefBuildingOwner.GetValue(colonist);
            var building = ownerRef != null ? _pRefEntity.GetValue(ownerRef) as DXEntity : null;
            if (building == null)
            {
                _pBDataTargetPos.SetValue(__0, null);
                __result = IARes.Fail;
                return false;
            }

            object terrainSys = _mTerrainGetCurrent.Invoke(null, null);
            var cells = terrainSys != null ? _pTerrainCells.GetValue(terrainSys) as Array : null;
            object defParams = _mGetParams.Invoke(building, null);
            if (cells == null || defParams == null) return true;

            int radius = (int)_pCollectRadius.GetValue(defParams);
            System.Drawing.Rectangle buildingArea = building.CellArea;
            System.Drawing.Rectangle searchArea = buildingArea.Enlarge(radius);
            int xMin = Math.Max(0, searchArea.X);
            int xMax = Math.Min(DXWorldGrid.NCells - 1, searchArea.Right - 1);
            int yMin = Math.Max(0, searchArea.Y);
            int yMax = Math.Min(DXWorldGrid.NCells - 1, searchArea.Bottom - 1);

            var candidates = new System.Collections.Generic.List<System.Drawing.Point>();
            for (int x = xMin; x <= xMax; x++)
            {
                for (int y = yMin; y <= yMax; y++)
                {
                    if (buildingArea.Contains(x, y)) continue;
                    // ZXTerrainResourceType.Grass == 30
                    if (Convert.ToByte(cells.GetValue(x, y)) != 30) continue;
                    // 2048u == Structure mask (same exclusion check used by CFarmer)
                    if (DXWorldGrid.AnyEntityAtCell(x, y, 2048u)) continue;
                    candidates.Add(new System.Drawing.Point(x, y));
                }
            }

            if (candidates.Count == 0)
            {
                _pBDataTargetPos.SetValue(__0, null);
                __result = IARes.Fail;
                return false;
            }

            System.Drawing.Point chosen = candidates.RandomElement();
            _pBDataTargetPos.SetValue(__0, DXWorldGrid.PointFromCell_Center(chosen));
            __result = IARes.Success;
            return false;
        }
    }

    /// <summary>
    /// Isolates camera-gated visual VFX helpers (#=zg10wm6mM9hhx: footsteps/dust puffs, blood
    /// splatters, hit sparks, explosions, screen shake, bullet tracers) from the synced PRNG
    /// and entity-ID streams. Callers like ZombieGiant.OnUpdate check CheckFastObjectVisibility()
    /// / IsPixelAreaVisible / VisibleCells before invoking these helpers during UpdateGameLogic;
    /// once Host and Client pan cameras independently, those RNG draws and transient DXEntities
    /// would diverge immediately.
    /// </summary>
    [HarmonyPatch]
    public static class VisualVfxIsolationPatch
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var vfxType = AccessTools.TypeByName("#=zg10wm6mM9hhx");
            if (vfxType == null) yield break;

            foreach (var m in AccessTools.GetDeclaredMethods(vfxType))
            {
                if (m != null && !m.IsAbstract && !m.ContainsGenericParameters)
                    yield return m;
            }
        }

        [HarmonyPrefix]
        public static void Prefix() => LockstepClock.EnterVisualVfxContext();

        [HarmonyFinalizer]
        public static void Finalizer() => LockstepClock.ExitVisualVfxContext();
    }

    /// <summary>
    /// Propagates deterministic simulation context into deferred callbacks queued via
    /// DXPostMethodManager.Invoke (OnStartFrame, OnFinishFrame, OnSafeLogicAndDrawing,
    /// DXPhysicWorld.PostMethods) when scheduled from a simulation context, so that
    /// callbacks executed later during DXGame.OnRender (e.g. ZXEntity zombie spawns)
    /// stay on the synced RNG stream and synced DXGuid track.
    /// </summary>
    [HarmonyPatch(typeof(DXPostMethodManager), nameof(DXPostMethodManager.Invoke))]
    public static class PostMethodInvokeContextPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref Action method)
        {
            if (method == null || !LockstepClock.IsDeterministicModeActive)
                return;
            if (LockstepClock.LoadingLevel || LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
                return;
            if (!LockstepClock.InDeterministicContext && !LockstepClock.LogicTickActive && !LockstepClock.InPhysicsContext)
                return;

            Action orig = method;
            method = () => LockstepClock.RunInDeterministicContext(orig);
        }
    }

    /// <summary>
    /// Isolates CSprite.#=zykGuoNdyOv10tsMpy8FzNkE=() (physics-push animation/movement mode sync)
    /// from render-time UpdateSceneObjectParallel, which only runs for entities inside the local
    /// camera viewport when their sprite atlas frame advances. Instead, we run it deterministically
    /// once per physics step for all entities with CanBePulled == true.
    /// </summary>
    [HarmonyPatch]
    public static class CSpritePullIsolationPatch
    {
        private static Type _cspriteType;
        private static int _cspriteTypeId;
        private static MethodInfo _pullMethod;
        private static PropertyInfo _paramsProp;
        private static PropertyInfo _canBePulledProp;
        private static readonly System.Collections.Generic.Dictionary<Type, bool> _canPullCache =
            new System.Collections.Generic.Dictionary<Type, bool>();

        private static MethodBase TargetMethod()
        {
            _cspriteType = AccessTools.TypeByName("ZX.Components.CSprite");
            if (_cspriteType == null) return null;
            _cspriteTypeId = DXComponent.GetIDFromType(_cspriteType);
            _paramsProp = AccessTools.Property(_cspriteType, "Params");
            _pullMethod = AccessTools.Method(_cspriteType, "#=zykGuoNdyOv10tsMpy8FzNkE=");
            return _pullMethod;
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !LockstepClock.IsDeterministicModeActive || LockstepClock.AllowCSpritePullUpdate;
        }

        public static void RunDeterministicPullUpdates(DXGame game)
        {
            if (game?.Entities == null || _pullMethod == null || _cspriteType == null)
                return;

            var entities = game.Entities;
            LockstepClock.AllowCSpritePullUpdate = true;
            try
            {
                foreach (var e in entities)
                {
                    if (e == null || e.Disposed || e.Body == null || e.PhysicalType == DXBodyType.Static)
                        continue;

                    Type entType = e.GetType();
                    bool knownCanPull;
                    if (_canPullCache.TryGetValue(entType, out knownCanPull) && !knownCanPull)
                        continue;

                    var csprite = e.GetComponent(_cspriteTypeId);
                    if (csprite == null)
                    {
                        _canPullCache[entType] = false;
                        continue;
                    }

                    if (!_canPullCache.ContainsKey(entType))
                    {
                        var p = _paramsProp?.GetValue(csprite);
                        if (p == null) continue;
                        if (_canBePulledProp == null)
                            _canBePulledProp = AccessTools.Property(p.GetType(), "CanBePulled");
                        knownCanPull = (bool)(_canBePulledProp?.GetValue(p) ?? false);
                        _canPullCache[entType] = knownCanPull;
                        if (!knownCanPull) continue;
                    }

                    _pullMethod.Invoke(csprite, null);
                }
            }
            finally
            {
                LockstepClock.AllowCSpritePullUpdate = false;
            }
        }
    }
}
