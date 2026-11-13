using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Networking;
using TABCoopMod.Patches;
using TABCoopMod.Testing;
using TABCoopMod.UI;
using ZX.GUI;

namespace TABCoopMod.Sync
{
    /// <summary>
    /// Orchestrates the Archon Mode Co-op Session:
    /// 1. Initial & Resync Save-Stream Transfer (.zxsav + .zxcheck chunked over Reliable channel)
    /// 2. 30Hz Lockstep Command Turn Synchronization (works both when Running and when Paused!)
    /// 3. Periodic 60-Tick StateHash verification & Auto-Resync trigger
    /// 4. 20Hz Unreliable Ally Cursor & Minimap Camera Viewport broadcast
    /// </summary>
    public static class CoopSessionManager
    {
        public const int ChunkSizeBytes = 32768; // 32 KB per reliable packet
        public const int SyncSeed = 20261109;

        public static INetworkTransport Transport { get; private set; }
        public static bool IsSessionActive => Transport != null && Transport.IsActive;
        public static bool IsInSyncedLevel { get; private set; }

        public static bool EnableAutoResync = true;
        public const float MaxAsyncTeleportCells = 3.0f;
        public const double AsyncPosAlignCooldownMs = 4000.0;
        private static double _lastAutoResyncTicks = -100000.0;
        private static double _lastPosSyncSentTicks = -100000.0;
        private static bool _desyncDumped;

        private static string EffectiveResyncMode =>
            string.IsNullOrEmpty(TestHarnessConfig.ResyncMode) ? "async_align" : TestHarnessConfig.ResyncMode.ToLowerInvariant();
        private static bool IsStrictMode => EffectiveResyncMode == "strict";
        private static bool EnableAsyncPosAlign => EffectiveResyncMode == "async_align";
        private static double AutoResyncCooldownMs =>
            TestHarnessConfig.ResyncCooldownMs > 0
                ? TestHarnessConfig.ResyncCooldownMs
                : (IsStrictMode ? 15000.0 : 30000.0);

        private static readonly List<NetCommand> _localOutgoingQueue = new List<NetCommand>();
        private static readonly List<NetCommand> _hostTurnBuffer = new List<NetCommand>();

        // True network lockstep: Host stamps every command with TargetSimTick = simTick + InputDelay
        // and broadcasts a heartbeat for EVERY tick (even empty), so all peers execute the same
        // commands on the same tick regardless of network latency.
        public const int InputDelayTicks = 2;
        private static readonly SortedDictionary<int, List<NetCommand>> _pendingTurnCommands =
            new SortedDictionary<int, List<NetCommand>>();
        private static TurnInputTickGate _networkGate = new TurnInputTickGate();
        private static int _hostLastBroadcastSimTick = -1;
        private static int _gateLastObservedTick = -1;
        private static double _gateWaitStartTicks = -1.0;
        private static double _gateLastWarnTicks = -1.0;
        private const double GateTimeoutMs = 3000.0;

        private static bool _awaitingSnapshotLoad;
        private static bool _awaitingClientSnapshotAck;
        private static double _awaitingClientAckStartTicks = -1.0;
        private static bool _hostSnapshotReloadPending;
        private static bool _hostWaitingOwnReload;
        private static bool _clientSnapshotAckReceived;
        public static bool IsHostSnapshotReloadPending => _hostSnapshotReloadPending;

        // Deferred work: never export/load from inside a deep engine callback (LevelLoadComplete
        // postfix / network PollEvents). Doing the ZIP save from that stack exhausted the 1 MB main
        // thread stack (0xc00000fd) and re-entered the session callbacks.
        private static bool _pendingHostSnapshotExport;
        private static string _pendingClientSnapshotLoadPath;
        private static bool _kickingLoadFade;
        private static int _loadFadeKickFrames;
        private static double _loadFadeKickStartTicks = -1.0;
        private const double LoadFadeTimeoutMs = 30000.0;

        // Snapshot receive buffer on Client. Chaos/jitter can deliver chunks out of order (even
        // before SnapshotBegin), so buffer by index and assemble in order once all are present.
        private static readonly Dictionary<int, byte[]> _incomingChunks = new Dictionary<int, byte[]>();
        private static int _expectedChunks;
        private static int _expectedSaveBytes;
        private static string _incomingCheckContent = string.Empty;
        private static int _incomingRandomSeed = 1337;
        private static long _incomingEntityIdCounter;

        // Ally Cursor & Camera State
        public sealed class AllyVisualState
        {
            public byte PlayerId;
            public PointF MouseWorldPosition;
            public RectangleF CameraWorldArea;
            public DateTime LastUpdatedUtc;
        }

        public sealed class TickDigestRecord
        {
            public StateHasher.LevelStateSnapshotDigest Digest;
            public string Summary;
        }

        private sealed class PosSyncReceiveBuffer
        {
            public int SimTick;
            public bool ApplyCorrection;
            public int TotalChunks;
            public int CompressedTotalLen;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
        }

        public static readonly Dictionary<byte, AllyVisualState> AllyStates = new Dictionary<byte, AllyVisualState>();
        private static double _lastCursorBroadcastTicks;
        private static int _lastCheckedSimTick = -1;
        private static bool _firstChecksumPosDumped;
        private static int _consecutiveProjOnlyMismatch;
        private static int _posSyncSeq;
        private static readonly Dictionary<int, TickDigestRecord> _hostTickDigests = new Dictionary<int, TickDigestRecord>();
        private static readonly Dictionary<int, Dictionary<byte, TickDigestRecord>> _clientTickDigests =
            new Dictionary<int, Dictionary<byte, TickDigestRecord>>();
        private static readonly Dictionary<int, List<StateHasher.EntityPosSample>> _tickPosCache =
            new Dictionary<int, List<StateHasher.EntityPosSample>>();
        private static readonly Queue<int> _tickPosCacheOrder = new Queue<int>();
        private static readonly Dictionary<int, PosSyncReceiveBuffer> _posSyncIncoming =
            new Dictionary<int, PosSyncReceiveBuffer>();

        public static void StartHostSession(bool useLoopback)
        {
            StopSession();
            LockstepClock.EnableDeterministicSandbox(SyncSeed);
            LockstepClock.RequestPostLoadCalibration(SyncSeed);
            LockstepClock.ResetBehaviourRandom(SyncSeed);

            Transport = CreateTransport(useLoopback);
            BindTransportEvents();
            Transport.StartHost(3);

            CommandInterceptor.LocalPlayerId = 1;
            CommandInterceptor.OnLocalCommandCaptured = OnLocalCommandCaptured;
            ModLogger.Info($"[CoopSessionManager] Started Host Session (Mode={(useLoopback ? "LocalLoopback" : "SteamP2P")}).");
        }

        public static void JoinSession(bool useLoopback, string targetAddressOrSteamId)
        {
            StopSession();
            LockstepClock.EnableDeterministicSandbox(SyncSeed);
            LockstepClock.RequestPostLoadCalibration(SyncSeed);
            LockstepClock.ResetBehaviourRandom(SyncSeed);

            Transport = CreateTransport(useLoopback);
            BindTransportEvents();
            Transport.ConnectToHost(targetAddressOrSteamId);

            CommandInterceptor.OnLocalCommandCaptured = OnLocalCommandCaptured;
            ModLogger.Info($"[CoopSessionManager] Joining Session at '{targetAddressOrSteamId}' (Mode={(useLoopback ? "LocalLoopback" : "SteamP2P")}).");
        }

        public static void StopSession()
        {
            CommandInterceptor.IsInterceptionEnabled = false;
            CommandInterceptor.OnLocalCommandCaptured = null;
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            CommandExecutor.IsReplayingCommand = false;
            IsInSyncedLevel = false;
            _lastCheckedSimTick = -1;
            _consecutiveProjOnlyMismatch = 0;
            _hostTickDigests.Clear();
            _clientTickDigests.Clear();
            _tickPosCache.Clear();
            _tickPosCacheOrder.Clear();
            _posSyncIncoming.Clear();
            LockstepClock.DisableDeterministicSandbox();
            AllyStates.Clear();
            _localOutgoingQueue.Clear();
            _hostTurnBuffer.Clear();
            _pendingTurnCommands.Clear();
            _hostLastBroadcastSimTick = -1;
            _gateLastObservedTick = -1;
            _gateWaitStartTicks = -1.0;
            _awaitingSnapshotLoad = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _hostSnapshotReloadPending = false;
            _hostWaitingOwnReload = false;
            _clientSnapshotAckReceived = false;
            _lastAutoResyncTicks = -100000.0;
            _lastPosSyncSentTicks = -100000.0;
            _pendingHostSnapshotExport = false;
            _pendingClientSnapshotLoadPath = null;
            _kickingLoadFade = false;
            _loadFadeKickStartTicks = -1.0;
            _incomingChunks.Clear();
            _expectedChunks = 0;
            _networkGate = new TurnInputTickGate();

            if (Transport != null)
            {
                Transport.Dispose();
                Transport = null;
            }
        }

        private static INetworkTransport CreateTransport(bool useLoopback)
        {
            INetworkTransport transport = useLoopback
                ? (INetworkTransport)new LocalTcpLoopbackTransport()
                : new SteamP2PTransport();

            if (TestHarnessConfig.HasChaos)
            {
                transport = new ChaosTransport(transport, ChaosConfig.FromString(TestHarnessConfig.ChaosSpec));
                ModLogger.Info($"[CoopSessionManager] Chaos injection active: {TestHarnessConfig.ChaosSpec}");
            }
            return transport;
        }

        private static void BindTransportEvents()
        {
            Transport.OnPeerConnected += peer =>
            {
                CommandInterceptor.LocalPlayerId = Transport.LocalPlayerId;
                ModLogger.Info($"[CoopSessionManager] Peer connected: {peer?.PlayerName} (PlayerId={peer?.PlayerId}, LocalId={Transport.LocalPlayerId})");
                GameBridge.ShowTopBanner($"Co-op Peer Connected: {peer?.PlayerName}", Color.SpringGreen, 3000);

                // If Host is already inside a level when a peer connects, push snapshot immediately!
                if (Transport.IsHost && GameBridge.CurrentLevelState != null && !_hostSnapshotReloadPending)
                {
                    RequestSnapshotBroadcast();
                }
            };

            Transport.OnPeerDisconnected += playerId =>
            {
                AllyStates.Remove(playerId);
                // Never leave the turn gate permanently blocked because a peer vanished.
                if (Transport != null && !Transport.IsHost)
                {
                    _networkGate.MarkReady(int.MaxValue);
                    IsInSyncedLevel = false;
                }
                GameBridge.ShowTopBanner($"Player {playerId} Disconnected", Color.OrangeRed, 3000);
            };

            Transport.OnPacketReceived += OnPacketReceived;
        }

        /// <summary>
        /// Defers a Host snapshot export to the next OnFrameTick top-level. Callers may sit on a deep
        /// engine/network stack (LevelLoadComplete postfix, packet delivery), where the recursive ZIP
        /// serializer would blow the main-thread stack.
        /// </summary>
        public static void RequestSnapshotBroadcast()
        {
            if (Transport == null || !Transport.IsHost || GameBridge.CurrentLevelState == null)
                return;
            _pendingHostSnapshotExport = true;
        }

        /// <summary>
        /// Exports the Host's current level state into a temporary .zxsav + .zxcheck pair
        /// and streams it to all connected peers in 32KB chunks.
        /// </summary>
        public static void BroadcastCurrentLevelSnapshot()
        {
            if (Transport == null || !Transport.IsHost || GameBridge.CurrentLevelState == null)
                return;

            double wasGameSpeed = DXGame.Current.GameSpeed;
            try
            {
                DXGame.Current.Paused = true;

                string savesDir = GameBridge.GetSavesDirectory();
                string tempBaseName = "coop_host_snapshot";
                string savPath = Path.Combine(savesDir, tempBaseName + ".zxsav");
                string chkPath = Path.Combine(savesDir, tempBaseName + ".zxcheck");

                ModLogger.Info($"[CoopSessionManager] Exporting Host snapshot to {savPath}...");
                GameBridge.WriteSaveFile(savPath);

                byte[] savBytes = File.ReadAllBytes(savPath);
                string chkText = File.Exists(chkPath) ? File.ReadAllText(chkPath) : string.Empty;
                int syncSeed = SyncSeed;
                long idCounter = LockstepClock.CurrentSyncedEntityIdCounter;

                _hostTickDigests.Clear();
                _clientTickDigests.Clear();
                _tickPosCache.Clear();
                _tickPosCacheOrder.Clear();
                _posSyncIncoming.Clear();
                _consecutiveProjOnlyMismatch = 0;
                _lastCheckedSimTick = -1;
                _pendingTurnCommands.Clear();
                _hostTurnBuffer.Clear();
                _localOutgoingQueue.Clear();

                bool hasRemotePeers = Transport.ConnectedPeers != null && Transport.ConnectedPeers.Count > 1;
                if (!hasRemotePeers)
                {
                    LockstepClock.ApplyDeterministicLevelBaseline(syncSeed, idCounter);
                    EnterSyncedLevel();
                    _awaitingClientSnapshotAck = false;
                    _awaitingClientAckStartTicks = -1.0;
                    _hostSnapshotReloadPending = false;
                    _hostWaitingOwnReload = false;
                    _clientSnapshotAckReceived = false;
                    return;
                }

                // Exit synced-level state during symmetric reload so neither peer runs ticks/inputs early.
                IsInSyncedLevel = false;
                CommandInterceptor.IsInterceptionEnabled = false;
                LockstepClock.SetSessionPause(true);

                _hostSnapshotReloadPending = true;
                _hostWaitingOwnReload = true;
                _clientSnapshotAckReceived = false;
                _awaitingClientSnapshotAck = true;
                _awaitingClientAckStartTicks = DXHelper_Environment.Ticks;

                int totalChunks = (savBytes.Length + ChunkSizeBytes - 1) / ChunkSizeBytes;
                ModLogger.Info($"[CoopSessionManager] Broadcasting Snapshot ({savBytes.Length:N0} bytes in {totalChunks} chunks, seed={syncSeed}, idCounter={idCounter})...");

                Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotBegin, w =>
                {
                    w.Write(savBytes.Length);
                    w.Write(totalChunks);
                    w.Write(chkText);
                    w.Write(syncSeed);
                    w.Write(idCounter);
                });

                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * ChunkSizeBytes;
                    int len = Math.Min(ChunkSizeBytes, savBytes.Length - offset);
                    int chunkIndex = i;
                    Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotChunk, w =>
                    {
                        w.Write(chunkIndex);
                        w.Write(len);
                        w.Write(savBytes, offset, len);
                    });
                }

                // Reload a Host-only save file so loopback Client reads/writes to coop_host_snapshot.zxsav
                // never collide on Windows file locks while Host's async loader is opening it.
                string hostSelfSavPath = Path.Combine(savesDir, "coop_host_self.zxsav");
                ModLogger.Info($"[CoopSessionManager] Exporting Host self-reload snapshot to {hostSelfSavPath}...");
                GameBridge.WriteSaveFile(hostSelfSavPath);

                ModLogger.Info($"[CoopSessionManager] Host starting symmetric reload of {hostSelfSavPath}...");
                LockstepClock.EnableDeterministicSandbox(syncSeed);
                LockstepClock.RequestPostLoadCalibration(syncSeed, idCounter);
                if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false;
                GameBridge.PrepareForProgrammaticLoad();
                GameBridge.LoadSaveFileAndEnter(hostSelfSavPath);
                _kickingLoadFade = true;
                _loadFadeKickFrames = 0;
                _loadFadeKickStartTicks = -1.0;

                GameBridge.ShowTopBanner($"Synced Map Snapshot to Peers ({savBytes.Length / 1024} KB)", Color.Cyan, 2500);
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to broadcast level snapshot", ex);
            }
            finally
            {
                // Never restore a zero speed: that would look like an engine pause and stall the
                // host heartbeat. Session pauses are handled separately via LockstepClock.PausedBySession.
                try { DXGame.Current.GameSpeed = wasGameSpeed > 0.0 ? wasGameSpeed : 1.0; } catch { }
                try { if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false; } catch { }
                if (_awaitingClientSnapshotAck)
                {
                    LockstepClock.SetSessionPause(true);
                }
            }
        }

        private static void TryCompleteHostSyncBarrier()
        {
            if (Transport == null || !Transport.IsHost) return;
            if (_hostWaitingOwnReload || !_clientSnapshotAckReceived) return;

            _hostSnapshotReloadPending = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _clientSnapshotAckReceived = false;

            _hostTickDigests.Clear();
            _clientTickDigests.Clear();
            _tickPosCache.Clear();
            _tickPosCacheOrder.Clear();
            _posSyncIncoming.Clear();
            _consecutiveProjOnlyMismatch = 0;
            _lastCheckedSimTick = -1;

            EnterSyncedLevel();

            int startTick = DXGame.Current?.IAIterationInt ?? 0;
            long idCounter = LockstepClock.CurrentSyncedEntityIdCounter;
            Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SessionStart, w =>
            {
                w.Write(startTick);
                w.Write(SyncSeed);
                w.Write(idCounter);
            });
            ModLogger.Info($"[CoopSessionManager] Host+Client barrier satisfied; entered synced level and sent SessionStart at tick={startTick}!");
        }

        private static void OnLocalCommandCaptured(NetCommand cmd)
        {
            if (!IsSessionActive || !IsInSyncedLevel)
            {
                // Single-player deterministic test mode: execute immediately
                CommandExecutor.Execute(cmd);
                return;
            }

            if (Transport.IsHost)
            {
                lock (_hostTurnBuffer)
                {
                    _hostTurnBuffer.Add(cmd);
                }
            }
            else
            {
                lock (_localOutgoingQueue)
                {
                    _localOutgoingQueue.Add(cmd);
                }
            }
        }

        /// <summary>
        /// Call once both peers are inside the same synced level. Installs the turn-input gate and
        /// lets the simulation run the first InputDelayTicks immediately while broadcasts catch up.
        /// </summary>
        public static void EnterSyncedLevel()
        {
            if (!IsSessionActive) return;

            _pendingTurnCommands.Clear();
            _hostLastBroadcastSimTick = -1;
            _gateWaitStartTicks = -1.0;
            _desyncDumped = false;
            _firstChecksumPosDumped = false;
            _networkGate = new TurnInputTickGate();
            LockstepClock.TickGate = _networkGate;

            int cur = DXGame.Current?.IAIterationInt ?? 0;
            _networkGate.MarkReady(cur + InputDelayTicks);
            IsInSyncedLevel = true;
            CommandInterceptor.IsInterceptionEnabled = true;

            // The save loader leaves DXGame.Paused/GameSpeed at 0; clear it so engine animations and
            // the fixed-step clock start running. Session pauses are tracked separately.
            LockstepClock.SetSessionPause(false);
            LockstepClock.ResetHostTickPacing();
            if (DXGame.Current != null)
            {
                if (DXGame.Current.Paused) DXGame.Current.Paused = false;
                if (DXGame.Current.GameSpeed <= 0.0) DXGame.Current.GameSpeed = 1.0;
            }
            GameBridge.ClearWaitCursor();

            ModLogger.Info($"[CoopSessionManager] Entered synced level at tick {cur} (inputDelay={InputDelayTicks}, role={(Transport.IsHost ? "Host" : "Client")}).");
        }

        /// <summary>
        /// Executes every buffered turn command for ticks &lt;= nextSimTick, in ascending tick order.
        /// Called from the deterministic clock prefix at the moment a tick is allowed to advance.
        /// Must not touch local selection state (see CommandExecutor).
        /// </summary>
        public static void FlushDueTurnCommands(int nextSimTick)
        {
            while (true)
            {
                List<NetCommand> due = null;
                lock (_pendingTurnCommands)
                {
                    if (_pendingTurnCommands.Count == 0) return;
                    var first = _pendingTurnCommands.GetEnumerator();
                    if (!first.MoveNext() || first.Current.Key > nextSimTick) return;
                    due = first.Current.Value;
                    _pendingTurnCommands.Remove(first.Current.Key);
                }

                foreach (var cmd in due)
                    CommandExecutor.Execute(cmd);
            }
        }

        private static void MonitorGateWait()
        {
            int cur = DXGame.Current?.IAIterationInt ?? 0;
            double now = DXHelper_Environment.Ticks;

            if (cur != _gateLastObservedTick)
            {
                _gateLastObservedTick = cur;
                _gateWaitStartTicks = -1.0;
            }

            if (_networkGate.IsTickReady(cur + 1))
            {
                _gateWaitStartTicks = -1.0;
                return;
            }

            if (_gateWaitStartTicks < 0.0)
            {
                _gateWaitStartTicks = now;
                return;
            }

            if (now - _gateWaitStartTicks >= GateTimeoutMs && now - _gateLastWarnTicks >= 1000.0)
            {
                _gateLastWarnTicks = now;
                double waited = (now - _gateWaitStartTicks) / 1000.0;
                ModLogger.Warn($"[CoopSessionManager] Tick gate waiting {waited:F1}s at tick {cur} — packets from Host are delayed or lost.");
                if (waited >= GateTimeoutMs / 1000.0)
                    GameBridge.ShowTopBanner("Waiting for Host network...", Color.Orange, 1200);
            }
        }

        /// <summary>Called from LevelLoadCompletePatch on the main thread after a level fully loads.</summary>
        public static void OnLevelLoadComplete()
        {
            if (!IsSessionActive) return;

            if (Transport.IsHost)
            {
                // Freeze immediately at 0 elapsed ticks until snapshot export and client handshake complete.
                LockstepClock.SetSessionPause(true);
                if (_hostSnapshotReloadPending && _hostWaitingOwnReload)
                {
                    _hostWaitingOwnReload = false;
                    ModLogger.Info($"[CoopSessionManager] Host symmetric snapshot reload finished at tick={DXGame.Current?.IAIterationInt ?? 0} (clientAck={_clientSnapshotAckReceived}).");
                    TryCompleteHostSyncBarrier();
                    return;
                }
                if (!IsInSyncedLevel && !_hostSnapshotReloadPending)
                {
                    RequestSnapshotBroadcast();
                }
                return;
            }

            if (!_awaitingSnapshotLoad) return;

            _awaitingSnapshotLoad = false;
            IsInSyncedLevel = false;
            LockstepClock.SetSessionPause(true);
            int clientTick = DXGame.Current?.IAIterationInt ?? 0;
            Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.SnapshotLoadedAck, w =>
            {
                w.Write(clientTick);
            });
            ModLogger.Info($"[CoopSessionManager] Client finished loading snapshot at tick={clientTick}; sent SnapshotLoadedAck and waiting for SessionStart.");
        }

        /// <summary>Records the snapshot seed so the post-load calibration uses the Host's seed.</summary>
        public static void RequestSnapshotCalibration(int seed, long entityIdCounter = 0)
        {
            _awaitingSnapshotLoad = true;
            LockstepClock.EnableDeterministicSandbox(seed);
            LockstepClock.RequestPostLoadCalibration(seed, entityIdCounter);
        }

        public static void OnFrameTick()
        {
            if (!IsSessionActive) return;

            // Deferred, top-of-frame (shallow stack) work: export never runs inside the loader
            // postfix; client load never runs inside packet delivery.
            if (_pendingHostSnapshotExport)
            {
                _pendingHostSnapshotExport = false;
                BroadcastCurrentLevelSnapshot();
            }
            if (_pendingClientSnapshotLoadPath != null)
            {
                string path = _pendingClientSnapshotLoadPath;
                _pendingClientSnapshotLoadPath = null;
                BeginClientSnapshotLoad(path);
            }

            Transport.PollEvents();

            // Keep the engine's loader fades advancing while the Client waits to enter the level.
            TickLoadingFade();

            // Keep Host paused and hold turn broadcasts while waiting for Host reload + Client ack.
            if (Transport.IsHost && (_hostWaitingOwnReload || _awaitingClientSnapshotAck))
            {
                LockstepClock.SetSessionPause(true);
                if (!_hostWaitingOwnReload && _awaitingClientAckStartTicks > 0.0 && DXHelper_Environment.Ticks - _awaitingClientAckStartTicks > 60000.0)
                {
                    ModLogger.Warn("[CoopSessionManager] Timed out (60s) waiting for Client SnapshotLoadedAck; resuming Host.");
                    _hostSnapshotReloadPending = false;
                    _awaitingClientSnapshotAck = false;
                    _awaitingClientAckStartTicks = -1.0;
                    _clientSnapshotAckReceived = false;
                    EnterSyncedLevel();
                }
                else
                {
                    return;
                }
            }

            if (!IsInSyncedLevel || GameBridge.CurrentLevelState == null)
                return;

            // 1. Flush Client outgoing commands to Host
            if (!Transport.IsHost)
            {
                List<NetCommand> toSend = null;
                lock (_localOutgoingQueue)
                {
                    if (_localOutgoingQueue.Count > 0)
                    {
                        toSend = new List<NetCommand>(_localOutgoingQueue);
                        _localOutgoingQueue.Clear();
                    }
                }

                if (toSend != null)
                {
                    Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.ClientInputReport, w =>
                    {
                        w.Write((ushort)toSend.Count);
                        foreach (var c in toSend) c.WriteTo(w);
                    });
                }
            }

            // 2. Host broadcasts TurnInputBroadcast for EVERY advanced sim tick (even empty heartbeat)
            //    and enqueues the commands for local execution on TargetSimTick. Nothing is executed
            //    on receipt, so both peers apply the same turn on the same tick. While paused the
            //    tick does not advance, so a new turn (pause-time build orders / SetPaused(false))
            //    is broadcast stamped on the frozen tick instead.
            if (Transport.IsHost)
            {
                int cur = DXGame.Current?.IAIterationInt ?? 0;
                // Use the synchronized pause flag, not DXGame.Paused: the engine's save loader leaves
                // DXGame.Paused set, which would stop the host from emitting heartbeat turns and
                // deadlock the client's gate at the very first tick.
                bool paused = LockstepClock.PausedBySession;
                bool hasTurn = false;
                lock (_hostTurnBuffer) hasTurn = _hostTurnBuffer.Count > 0;
                bool shouldBroadcast = paused ? hasTurn : cur > _hostLastBroadcastSimTick;

                if (shouldBroadcast)
                {
                    if (cur > _hostLastBroadcastSimTick) _hostLastBroadcastSimTick = cur;
                    int targetTick = paused ? cur : cur + InputDelayTicks;

                    List<NetCommand> turnCmds = null;
                    lock (_hostTurnBuffer)
                    {
                        if (_hostTurnBuffer.Count > 0)
                        {
                            turnCmds = new List<NetCommand>(_hostTurnBuffer);
                            _hostTurnBuffer.Clear();
                        }
                    }

                    if (turnCmds != null)
                    {
                        foreach (var c in turnCmds) c.TargetSimTick = targetTick;
                        lock (_pendingTurnCommands)
                        {
                            if (!_pendingTurnCommands.TryGetValue(targetTick, out var slot))
                                _pendingTurnCommands[targetTick] = slot = new List<NetCommand>();
                            slot.AddRange(turnCmds);
                        }
                    }

                    _networkGate.MarkReady(targetTick);

                    Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.TurnInputBroadcast, w =>
                    {
                        w.Write(targetTick);
                        w.Write((ushort)(turnCmds?.Count ?? 0));
                        if (turnCmds != null)
                            foreach (var c in turnCmds) c.WriteTo(w);
                    });
                }
            }
            else
            {
                MonitorGateWait();
            }

            // While paused by session, UpdateGameLogic may not fire every tick, so flush any
            // authorized turn commands stamped for the frozen tick directly on the main thread.
            if (LockstepClock.PausedBySession)
            {
                int pausedTick = DXGame.Current?.IAIterationInt ?? 0;
                if (_networkGate == null || _networkGate.IsTickReady(pausedTick))
                    LockstepClock.RunInDeterministicContext(() => FlushDueTurnCommands(pausedTick));
            }

            // 3. Broadcast Ally Cursor & Camera Viewport at 20Hz (every 50ms)
            double nowTicks = DXHelper_Environment.Ticks;
            if (nowTicks - _lastCursorBroadcastTicks >= 50.0 && DXScene.Current != null)
            {
                _lastCursorBroadcastTicks = nowTicks;
                var camArea = DXScene.Current.CameraArea;
                var mouseWorldPos = GetLocalMouseWorldPos();

                Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.AllyCursorAndCamera, w =>
                {
                    w.Write(Transport.LocalPlayerId);
                    w.Write(mouseWorldPos.X);
                    w.Write(mouseWorldPos.Y);
                    w.Write(camArea.X);
                    w.Write(camArea.Y);
                    w.Write(camArea.Width);
                    w.Write(camArea.Height);
                });
            }

            // 4. At initial SessionStart frame and every 60 SimTicks (2 seconds), compute and verify StateHash
            int curTick = DXGame.Current?.IAIterationInt ?? 0;
            if (curTick > 0 && (curTick % 60 == 0 || _lastCheckedSimTick < 0) && curTick != _lastCheckedSimTick)
            {
                _lastCheckedSimTick = curTick;

                // Prune stale digests (keep ~20s of history) so dictionaries cannot grow unbounded.
                if (Transport.IsHost)
                {
                    if (_hostTickDigests.Count > 0)
                    {
                        var stale = new List<int>();
                        foreach (var k in _hostTickDigests.Keys)
                            if (k < curTick - 600) stale.Add(k);
                        foreach (var k in stale) _hostTickDigests.Remove(k);
                    }
                    if (_clientTickDigests.Count > 0)
                    {
                        var stale = new List<int>();
                        foreach (var k in _clientTickDigests.Keys)
                            if (k < curTick - 600) stale.Add(k);
                        foreach (var k in stale) _clientTickDigests.Remove(k);
                    }
                }

                var dynamicPositions = new List<StateHasher.EntityPosSample>(2048);
                var digest = StateHasher.ComputeDigest(dynamicPositions);
                CacheTickPositions(digest.SimTick, dynamicPositions);

                string digestSummary =
                    $"Pos={digest.PosChecksum:X16} Coarse={digest.CoarsePosChecksum:X16} Life={digest.LifeChecksum:X16} " +
                    $"Aggro={digest.ZombieAggroChecksum:X16}({digest.AggroZombieCount}) Proj={digest.ProjectileChecksum:X16} " +
                    $"Prod={digest.ProductionChecksum:X16} Resv={digest.ReservationChecksum:X16} " +
                    $"(entities={digest.EntityCount} proj={digest.ProjectileCount} queued={digest.QueuedOrderCount} " +
                    $"res={digest.Gold}/{digest.Wood}/{digest.Stone}/{digest.Iron}/{digest.Oil} time={digest.GameTimeMs})";

                if (Transport.IsHost)
                {
                    _hostTickDigests[digest.SimTick] = new TickDigestRecord { Digest = digest, Summary = digestSummary };
                    VerifyTickChecksum(digest.SimTick);
                }
                else
                {
                    Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.StateChecksumReport, w =>
                    {
                        WriteDigestReport(w, in digest, digestSummary);
                    });
                }
            }
        }

        private static PointF GetLocalMouseWorldPos()
        {
            var levelSys = GameBridge.LevelSystemInstance;
            if (levelSys == null) return PointF.Empty;
            var field = AccessTools.Field(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_MouseWorldPos);
            return field != null ? (PointF)field.GetValue(levelSys) : PointF.Empty;
        }

        /// <summary>
        /// Triggers a tactical ping locally and broadcasts it to all session peers.
        /// Pings are purely visual UI signals (not deterministic world mutations) so they travel over
        /// PacketType.TacticalPing immediately without consuming a lockstep command slot.
        /// </summary>
        public static void SendTacticalPing(Point cell, string label = null)
        {
            byte pid = Transport != null ? Transport.LocalPlayerId : CommandInterceptor.LocalPlayerId;
            string effectiveLabel = string.IsNullOrEmpty(label) ? $"Player {pid}" : label;

            AllyOverlayRenderer.TriggerMinimapAndGroundPing(pid, cell, effectiveLabel);

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;

            Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.TacticalPing, w =>
            {
                w.Write(pid);
                w.Write(cell.X);
                w.Write(cell.Y);
                w.Write(effectiveLabel);
            });
        }

        private static void OnPacketReceived(byte senderId, PacketType pktType, BinaryReader r)
        {
            switch (pktType)
            {
                case PacketType.SnapshotBegin:
                {
                    _expectedSaveBytes = r.ReadInt32();
                    _expectedChunks = r.ReadInt32();
                    _incomingCheckContent = r.ReadString();
                    _incomingRandomSeed = r.ReadInt32();
                    _incomingEntityIdCounter = r.ReadInt64();
                    ModLogger.Info($"[CoopSessionManager] Receiving Snapshot from Host ({_expectedSaveBytes:N0} bytes, {_expectedChunks} chunks, seed={_incomingRandomSeed}, idCounter={_incomingEntityIdCounter}); buffered={_incomingChunks.Count}.");
                    GameBridge.ShowTopBanner("Receiving Map Snapshot from Host...", Color.Cyan, 3000);
                    TryAssembleSnapshot();
                    break;
                }

                case PacketType.SnapshotChunk:
                {
                    int chunkIdx = r.ReadInt32();
                    int len = r.ReadInt32();
                    byte[] data = r.ReadBytes(len);
                    // Chunks may arrive before SnapshotBegin and out of order (ChaosTransport jitter
                    // reorders even ReliableOrdered). Buffer by index; assemble once complete.
                    if (chunkIdx >= 0) _incomingChunks[chunkIdx] = data;
                    ModLogger.Info($"[CoopSessionManager] Snapshot chunk {chunkIdx} received ({len:N0} bytes, buffered={_incomingChunks.Count}/{_expectedChunks}).");
                    TryAssembleSnapshot();
                    break;
                }

                case PacketType.SnapshotLoadedAck:
                {
                    if (!Transport.IsHost) break;
                    int clientTick = r.ReadInt32();
                    _clientSnapshotAckReceived = true;
                    ModLogger.Info($"[CoopSessionManager] Received SnapshotLoadedAck from Client({senderId}) at tick={clientTick} (hostWaitingOwnReload={_hostWaitingOwnReload}).");
                    TryCompleteHostSyncBarrier();
                    break;
                }

                case PacketType.SessionStart:
                {
                    if (Transport.IsHost) break;
                    int startTick = r.ReadInt32();
                    int seed = r.ReadInt32();
                    long idCounter = r.ReadInt64();
                    _hostTickDigests.Clear();
                    _clientTickDigests.Clear();
                    _tickPosCache.Clear();
                    _tickPosCacheOrder.Clear();
                    _posSyncIncoming.Clear();
                    _lastCheckedSimTick = -1;
                    _consecutiveProjOnlyMismatch = 0;
                    EnterSyncedLevel();
                    ModLogger.Info($"[CoopSessionManager] Received SessionStart from Host (tick={startTick}, localTick={DXGame.Current?.IAIterationInt ?? 0}, seed={seed}, idCounter={idCounter}, mode={EffectiveResyncMode}). Unpausing in lockstep!");
                    break;
                }

                case PacketType.ClientInputReport:
                {
                    if (!Transport.IsHost) break;
                    ushort count = r.ReadUInt16();
                    lock (_hostTurnBuffer)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var cmd = NetCommand.ReadFrom(r);
                            cmd.PlayerId = senderId;
                            _hostTurnBuffer.Add(cmd);
                        }
                    }
                    break;
                }

                case PacketType.TurnInputBroadcast:
                {
                    if (Transport.IsHost) break;
                    int targetTick = r.ReadInt32();
                    ushort count = r.ReadUInt16();
                    List<NetCommand> cmds = null;
                    for (int i = 0; i < count; i++)
                    {
                        var cmd = NetCommand.ReadFrom(r);
                        (cmds ?? (cmds = new List<NetCommand>())).Add(cmd);
                    }

                    if (cmds != null)
                    {
                        lock (_pendingTurnCommands)
                        {
                            if (!_pendingTurnCommands.TryGetValue(targetTick, out var slot))
                                _pendingTurnCommands[targetTick] = slot = new List<NetCommand>();
                            slot.AddRange(cmds);
                        }
                    }

                    // The heartbeat arrived: allow the simulation to advance up to this tick. The
                    // commands themselves are executed by FlushDueTurnCommands at the tick boundary.
                    _networkGate.MarkReady(targetTick);
                    break;
                }

                case PacketType.StateChecksumReport:
                {
                    if (!Transport.IsHost) break;
                    TickDigestRecord rec = ReadDigestReport(r);
                    int clientTick = rec.Digest.SimTick;
                    if (!_clientTickDigests.TryGetValue(clientTick, out var byPeer))
                        _clientTickDigests[clientTick] = byPeer = new Dictionary<byte, TickDigestRecord>();
                    byPeer[senderId] = rec;
                    VerifyTickChecksum(clientTick);
                    break;
                }

                case PacketType.PosDriftSyncChunk:
                {
                    if (Transport.IsHost) break;
                    OnPosDriftSyncChunkReceived(r);
                    break;
                }

                case PacketType.PosDriftStatsReport:
                {
                    if (!Transport.IsHost) break;
                    int pTick = r.ReadInt32();
                    bool wasApplied = r.ReadBoolean();
                    int appliedCount = r.ReadInt32();
                    int comparedCount = r.ReadInt32();
                    int drifted001 = r.ReadInt32();
                    int drifted01 = r.ReadInt32();
                    int drifted1 = r.ReadInt32();
                    int drifted5 = r.ReadInt32();
                    float maxDelta = r.ReadSingle();
                    float rmsDelta = r.ReadSingle();
                    bool exceededThreshold = r.ReadBoolean();
                    string maxType = r.ReadString();

                    ModLogger.Info(
                        $"[PosDriftStats] Client({senderId}) Tick={pTick} Applied={wasApplied}({appliedCount}/{comparedCount}) " +
                        $"Drifted(>0.001={drifted001}, >0.01={drifted01}, >0.1={drifted1}, >0.5={drifted5}) " +
                        $"MaxDelta={maxDelta:F4} RmsDelta={rmsDelta:F4} Type={maxType} Exceeded={exceededThreshold}");

                    if (exceededThreshold && EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport)
                    {
                        double now = DXHelper_Environment.Ticks;
                        if (now - _lastAutoResyncTicks >= AutoResyncCooldownMs)
                        {
                            _lastAutoResyncTicks = now;
                            ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after PosDrift threshold exceeded at Tick {pTick} (MaxDelta={maxDelta:F4} >= {MaxAsyncTeleportCells:F2}).");
                            RequestSnapshotBroadcast();
                        }
                    }
                    break;
                }

                case PacketType.AllyCursorAndCamera:
                {
                    byte pid = r.ReadByte();
                    if (pid == Transport.LocalPlayerId) break;

                    var state = new AllyVisualState
                    {
                        PlayerId = pid,
                        MouseWorldPosition = new PointF(r.ReadSingle(), r.ReadSingle()),
                        CameraWorldArea = new RectangleF(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    AllyStates[pid] = state;

                    // If Host, relay to other clients in 3-player session
                    if (Transport.IsHost && Transport.ConnectedPeers.Count > 2)
                    {
                        Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.AllyCursorAndCamera, w =>
                        {
                            w.Write(state.PlayerId);
                            w.Write(state.MouseWorldPosition.X);
                            w.Write(state.MouseWorldPosition.Y);
                            w.Write(state.CameraWorldArea.X);
                            w.Write(state.CameraWorldArea.Y);
                            w.Write(state.CameraWorldArea.Width);
                            w.Write(state.CameraWorldArea.Height);
                        });
                    }
                    break;
                }

                case PacketType.TacticalPing:
                {
                    byte pid = r.ReadByte();
                    int cellX = r.ReadInt32();
                    int cellY = r.ReadInt32();
                    string label = r.ReadString();
                    if (Transport != null && pid == Transport.LocalPlayerId) break;

                    AllyOverlayRenderer.TriggerMinimapAndGroundPing(pid, new Point(cellX, cellY), label);

                    // If Host, relay to other clients in 3-player session
                    if (Transport != null && Transport.IsHost && Transport.ConnectedPeers.Count > 2)
                    {
                        Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.TacticalPing, w =>
                        {
                            w.Write(pid);
                            w.Write(cellX);
                            w.Write(cellY);
                            w.Write(label);
                        });
                    }
                    break;
                }
            }
        }

        private static void CacheTickPositions(int tick, List<StateHasher.EntityPosSample> samples)
        {
            if (samples == null) return;
            _tickPosCache[tick] = samples;
            _tickPosCacheOrder.Enqueue(tick);
            while (_tickPosCacheOrder.Count > 6)
            {
                int old = _tickPosCacheOrder.Dequeue();
                _tickPosCache.Remove(old);
            }
        }

        private static void WriteDigestReport(BinaryWriter w, in StateHasher.LevelStateSnapshotDigest d, string summary)
        {
            w.Write(d.SimTick);
            w.Write(d.GameTimeMs);
            w.Write(d.Gold);
            w.Write(d.Wood);
            w.Write(d.Stone);
            w.Write(d.Iron);
            w.Write(d.Oil);
            w.Write(d.EntityCount);
            w.Write(d.ProjectileCount);
            w.Write(d.QueuedOrderCount);
            w.Write(d.AggroZombieCount);
            w.Write(d.PosChecksum);
            w.Write(d.CoarsePosChecksum);
            w.Write(d.LifeChecksum);
            w.Write(d.ZombieAggroChecksum);
            w.Write(d.ProjectileChecksum);
            w.Write(d.ProductionChecksum);
            w.Write(d.ReservationChecksum);
            w.Write(d.HardHash);
            w.Write(d.FullHash);
            w.Write(summary ?? string.Empty);
        }

        private static TickDigestRecord ReadDigestReport(BinaryReader r)
        {
            var d = new StateHasher.LevelStateSnapshotDigest
            {
                SimTick = r.ReadInt32(),
                GameTimeMs = r.ReadInt32(),
                Gold = r.ReadInt32(),
                Wood = r.ReadInt32(),
                Stone = r.ReadInt32(),
                Iron = r.ReadInt32(),
                Oil = r.ReadInt32(),
                EntityCount = r.ReadInt32(),
                ProjectileCount = r.ReadInt32(),
                QueuedOrderCount = r.ReadInt32(),
                AggroZombieCount = r.ReadInt32(),
                PosChecksum = r.ReadInt64(),
                CoarsePosChecksum = r.ReadInt64(),
                LifeChecksum = r.ReadInt64(),
                ZombieAggroChecksum = r.ReadInt64(),
                ProjectileChecksum = r.ReadInt64(),
                ProductionChecksum = r.ReadInt64(),
                ReservationChecksum = r.ReadInt64(),
                HardHash = r.ReadUInt64(),
                FullHash = r.ReadUInt64(),
            };
            string summary = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : string.Empty;
            return new TickDigestRecord { Digest = d, Summary = summary };
        }

        private static void VerifyTickChecksum(int tick)
        {
            if (!_hostTickDigests.TryGetValue(tick, out TickDigestRecord hostRec)) return;
            if (!_clientTickDigests.TryGetValue(tick, out var byPeer) || byPeer.Count == 0) return;

            var host = hostRec.Digest;
            string hostSummary = hostRec.Summary;
            var resolvedPeers = new List<byte>();

            foreach (var kv in byPeer)
            {
                byte senderId = kv.Key;
                var client = kv.Value.Digest;
                string clientSummary = kv.Value.Summary;
                resolvedPeers.Add(senderId);

                if (host.FullHash == client.FullHash)
                {
                    _consecutiveProjOnlyMismatch = 0;
                    ModLogger.Info($"[StateChecksum] Tick {tick} MATCH (0x{host.FullHash:X16}) with Client({senderId}).");
                    continue;
                }

                bool lifeDiff = host.LifeChecksum != client.LifeChecksum;
                bool aggroDiff = host.ZombieAggroChecksum != client.ZombieAggroChecksum || host.AggroZombieCount != client.AggroZombieCount;
                bool entityDiff = host.EntityCount != client.EntityCount;
                bool econDiff = host.Gold != client.Gold || host.Wood != client.Wood || host.Stone != client.Stone
                    || host.Iron != client.Iron || host.Oil != client.Oil
                    || host.ReservationChecksum != client.ReservationChecksum
                    || host.ProductionChecksum != client.ProductionChecksum
                    || host.QueuedOrderCount != client.QueuedOrderCount
                    || host.GameTimeMs != client.GameTimeMs;
                bool projDiff = host.ProjectileCount != client.ProjectileCount || host.ProjectileChecksum != client.ProjectileChecksum;
                bool posDiff = host.PosChecksum != client.PosChecksum;
                bool coarsePosDiff = host.CoarsePosChecksum != client.CoarsePosChecksum;

                bool hardCoreDiff = lifeDiff || aggroDiff || entityDiff || econDiff;
                double now = DXHelper_Environment.Ticks;

                if (hardCoreDiff || (projDiff && (IsStrictMode || _consecutiveProjOnlyMismatch >= 1)))
                {
                    if (projDiff && !hardCoreDiff) _consecutiveProjOnlyMismatch++;
                    else _consecutiveProjOnlyMismatch = 0;

                    ModLogger.Error($"[StateChecksum] HARD DESYNC DETECTED at Tick {tick}! Life={lifeDiff} Aggro={aggroDiff} Entity={entityDiff} Econ={econDiff} Proj={projDiff} Pos={posDiff} (Host=0x{host.FullHash:X16}, Client({senderId})=0x{client.FullHash:X16})");
                    ModLogger.Error($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                    ModLogger.Error($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                    if (!_desyncDumped)
                    {
                        _desyncDumped = true;
                        DumpHostEntitiesOnDesync(tick);
                    }
                    TrySendPosDriftSync(senderId, tick, applyCorrection: false);
                    GameBridge.ShowTopBanner("State Drift Detected (see log)", Color.Orange, 2500);

                    if (EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport && (now - _lastAutoResyncTicks >= AutoResyncCooldownMs))
                    {
                        _lastAutoResyncTicks = now;
                        ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after HARD DESYNC at Tick {tick} (Life={lifeDiff}, Aggro={aggroDiff}, Entity={entityDiff}, Econ={econDiff}, Proj={projDiff}).");
                        RequestSnapshotBroadcast();
                    }
                    continue;
                }

                if (projDiff)
                {
                    // Single transient projectile difference with identical HP, Zombie Aggro, EntityCount and Economy:
                    // tolerate one check unless it persists on the next verification tick.
                    _consecutiveProjOnlyMismatch++;
                    ModLogger.Warn($"[StateChecksum] TRANSIENT PROJ DIFF at Tick {tick} (Life/Aggro/Entity/Econ MATCH; PosDiff={posDiff}, streak={_consecutiveProjOnlyMismatch}).");
                    if (posDiff && (now - _lastPosSyncSentTicks >= AsyncPosAlignCooldownMs))
                    {
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(senderId, tick, applyCorrection: EnableAsyncPosAlign);
                    }
                    continue;
                }

                // Pure position drift: NO zombie aggro divergence, NO unit damage divergence, NO combat/economy divergence!
                _consecutiveProjOnlyMismatch = 0;
                ModLogger.Warn($"[StateChecksum] SOFT POS DRIFT at Tick {tick} (Life/Aggro/Entity/Proj/Econ MATCH! CoarseDiff={coarsePosDiff}, mode={EffectiveResyncMode}).");
                ModLogger.Info($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                ModLogger.Info($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");

                if (IsStrictMode)
                {
                    if (now - _lastPosSyncSentTicks >= 2000.0)
                    {
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(senderId, tick, applyCorrection: false);
                    }
                    if (EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport && (now - _lastAutoResyncTicks >= AutoResyncCooldownMs))
                    {
                        _lastAutoResyncTicks = now;
                        ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after DESYNC (strict pos) at Tick {tick}.");
                        RequestSnapshotBroadcast();
                    }
                }
                else
                {
                    if (now - _lastPosSyncSentTicks >= AsyncPosAlignCooldownMs)
                    {
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(senderId, tick, applyCorrection: EnableAsyncPosAlign);
                    }
                }
            }

            foreach (var pid in resolvedPeers)
                byPeer.Remove(pid);
            if (byPeer.Count == 0)
                _clientTickDigests.Remove(tick);
        }

        private static void TrySendPosDriftSync(byte targetPlayerId, int tick, bool applyCorrection)
        {
            if (Transport == null || !Transport.IsHost) return;
            if (!_tickPosCache.TryGetValue(tick, out var samples) || samples == null || samples.Count == 0)
                return;

            try
            {
                byte[] compressed;
                using (var rawMs = new MemoryStream(samples.Count * 16 + 8))
                using (var bw = new BinaryWriter(rawMs))
                {
                    bw.Write(samples.Count);
                    for (int i = 0; i < samples.Count; i++)
                    {
                        bw.Write(samples[i].Id);
                        bw.Write(samples[i].X);
                        bw.Write(samples[i].Y);
                    }
                    bw.Flush();
                    byte[] rawBytes = rawMs.ToArray();
                    using (var outMs = new MemoryStream(rawBytes.Length / 2))
                    {
                        using (var ds = new DeflateStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
                            ds.Write(rawBytes, 0, rawBytes.Length);
                        compressed = outMs.ToArray();
                    }
                }

                int seq = ++_posSyncSeq;
                int totalLen = compressed.Length;
                int totalChunks = Math.Max(1, (totalLen + ChunkSizeBytes - 1) / ChunkSizeBytes);

                for (int idx = 0; idx < totalChunks; idx++)
                {
                    int offset = idx * ChunkSizeBytes;
                    int len = Math.Min(ChunkSizeBytes, totalLen - offset);
                    Transport.SendToPeer(targetPlayerId, SendChannel.ReliableOrdered, PacketType.PosDriftSyncChunk, w =>
                    {
                        w.Write(seq);
                        w.Write(tick);
                        w.Write(applyCorrection);
                        w.Write(totalChunks);
                        w.Write(idx);
                        w.Write(totalLen);
                        w.Write(len);
                        w.Write(compressed, offset, len);
                    });
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[AsyncPosAlign] TrySendPosDriftSync failed: {ex.Message}");
            }
        }

        private static void OnPosDriftSyncChunkReceived(BinaryReader r)
        {
            int seq = r.ReadInt32();
            int tick = r.ReadInt32();
            bool applyCorrection = r.ReadBoolean();
            int totalChunks = r.ReadInt32();
            int chunkIdx = r.ReadInt32();
            int compressedTotalLen = r.ReadInt32();
            int len = r.ReadInt32();
            byte[] part = r.ReadBytes(len);

            if (!_posSyncIncoming.TryGetValue(seq, out var buf))
            {
                buf = new PosSyncReceiveBuffer
                {
                    SimTick = tick,
                    ApplyCorrection = applyCorrection,
                    TotalChunks = totalChunks,
                    CompressedTotalLen = compressedTotalLen
                };
                _posSyncIncoming[seq] = buf;
            }
            buf.Chunks[chunkIdx] = part;
            if (buf.Chunks.Count < buf.TotalChunks) return;

            _posSyncIncoming.Remove(seq);
            // Prune stale incomplete sequences if any
            if (_posSyncIncoming.Count > 4) _posSyncIncoming.Clear();

            byte[] compressed = new byte[buf.CompressedTotalLen];
            int offset = 0;
            for (int i = 0; i < buf.TotalChunks; i++)
            {
                if (!buf.Chunks.TryGetValue(i, out byte[] p)) return;
                Buffer.BlockCopy(p, 0, compressed, offset, p.Length);
                offset += p.Length;
            }

            if (!_tickPosCache.TryGetValue(buf.SimTick, out var clientSamples) || clientSamples == null)
            {
                ModLogger.Warn($"[AsyncPosAlign] No cached client positions for Tick {buf.SimTick}; cannot measure/apply delta.");
                return;
            }

            var clientMap = new Dictionary<ulong, PointF>(clientSamples.Count);
            for (int i = 0; i < clientSamples.Count; i++)
                clientMap[clientSamples[i].Id] = new PointF(clientSamples[i].X, clientSamples[i].Y);

            int count;
            var hostSamples = new List<StateHasher.EntityPosSample>(clientSamples.Count);
            using (var inMs = new MemoryStream(compressed))
            using (var ds = new DeflateStream(inMs, CompressionMode.Decompress))
            using (var br = new BinaryReader(ds))
            {
                count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    hostSamples.Add(new StateHasher.EntityPosSample
                    {
                        Id = br.ReadUInt64(),
                        X = br.ReadSingle(),
                        Y = br.ReadSingle()
                    });
                }
            }

            int comparedCount = 0;
            int drifted001 = 0, drifted01 = 0, drifted1 = 0, drifted5 = 0;
            int appliedCount = 0;
            float maxDelta = 0f;
            double sumSq = 0.0;
            string maxType = "None";

            var game = DXGame.Current;
            int nCells = DXWorldGrid.NCells;
            float maxCoord = nCells > 1 ? nCells - 0.5f : 10000f;

            for (int i = 0; i < hostSamples.Count; i++)
            {
                var hs = hostSamples[i];
                if (!clientMap.TryGetValue(hs.Id, out PointF cp)) continue;
                comparedCount++;

                float dx = hs.X - cp.X;
                float dy = hs.Y - cp.Y;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist > 0.001f)
                {
                    drifted001++;
                    sumSq += dist * dist;
                }
                if (dist > 0.01f) drifted01++;
                if (dist > 0.1f) drifted1++;
                if (dist > 0.5f) drifted5++;

                if (dist > maxDelta)
                {
                    maxDelta = dist;
                    DXEntity maxEnt = game?.GetEntityByID(hs.Id);
                    maxType = maxEnt != null ? maxEnt.GetType().Name : "Unknown";
                }

                if (buf.ApplyCorrection && dist > 0.0005f && dist <= MaxAsyncTeleportCells && game != null)
                {
                    DXEntity e = game.GetEntityByID(hs.Id);
                    if (e != null && !e.Disposed && e.PhysicalType == DXBodyType.Dynamic)
                    {
                        float nx = e.Position.X + dx;
                        float ny = e.Position.Y + dy;
                        if (!float.IsNaN(nx) && !float.IsInfinity(nx) && !float.IsNaN(ny) && !float.IsInfinity(ny))
                        {
                            nx = Math.Max(0.5f, Math.Min(maxCoord, nx));
                            ny = Math.Max(0.5f, Math.Min(maxCoord, ny));
                            var newPos = new PointF(nx, ny);
                            e.LastPosition = new PointF(e.LastPosition.X + dx, e.LastPosition.Y + dy);
                            e.Position = newPos;
                            var body = e.Body;
                            if (body != null && body.Handle != IntPtr.Zero)
                            {
                                body.LastPositionComputed = new PointF(body.LastPositionComputed.X + dx, body.LastPositionComputed.Y + dy);
                                body.PositionComputed = newPos;
                                body.PhysicPosition = newPos;
                            }
                            appliedCount++;
                        }
                    }
                }
            }

            float rmsDelta = drifted001 > 0 ? (float)Math.Sqrt(sumSq / drifted001) : 0f;
            bool exceededThreshold = maxDelta >= MaxAsyncTeleportCells;

            ModLogger.Info(
                $"[AsyncPosAlign] Tick={buf.SimTick} Apply={buf.ApplyCorrection}({appliedCount}/{comparedCount}) " +
                $"Drifted(>0.001={drifted001}, >0.01={drifted01}, >0.1={drifted1}, >0.5={drifted5}) " +
                $"MaxDelta={maxDelta:F4} Rms={rmsDelta:F4} MaxType={maxType} Exceeded={exceededThreshold}");

            Transport?.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.PosDriftStatsReport, w =>
            {
                w.Write(buf.SimTick);
                w.Write(buf.ApplyCorrection);
                w.Write(appliedCount);
                w.Write(comparedCount);
                w.Write(drifted001);
                w.Write(drifted01);
                w.Write(drifted1);
                w.Write(drifted5);
                w.Write(maxDelta);
                w.Write(rmsDelta);
                w.Write(exceededThreshold);
                w.Write(maxType ?? "Unknown");
            });
        }

        private static void DumpHostEntitiesOnDesync(int tick)
        {
            if (_firstChecksumPosDumped) return;
            _firstChecksumPosDumped = true;
            DumpPeerEntities(tick, "coop_desync_pos_host.txt");
        }

        private static void DumpPeerEntities(int tick, string fileName)
        {
            try
            {
                var game = DXGame.Current;
                if (game?.Entities == null) return;
                string savesDir = GameBridge.GetSavesDirectory();
                if (string.IsNullOrEmpty(savesDir)) return;
                string path = Path.Combine(savesDir, fileName);
                var sb = new System.Text.StringBuilder(1 << 18);
                sb.Append("# tick=").Append(tick).Append('\n');
                int count = 0;
                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    if ((e.ID & 0x8000000000000000UL) != 0) continue;
                    if (e.GetType() == typeof(DXEntity) || string.Equals(e.GetType().Name, "Raven", StringComparison.Ordinal)) continue;
                    var b = e.Body;
                    float bx = b != null ? b.PositionComputed.X : float.NaN;
                    float by = b != null ? b.PositionComputed.Y : float.NaN;
                    sb.Append(e.ID).Append('|')
                      .Append(e.Position.X.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(e.Position.Y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(bx.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(by.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(e.GetType().Name).Append('\n');
                    count++;
                }
                File.WriteAllText(path, sb.ToString());
                ModLogger.Info($"[StateChecksum] Dumped {count} entity positions (tick {tick}) -> {path}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[StateChecksum] DumpPeerEntities failed: {ex.Message}");
            }
        }

        private static void TryAssembleSnapshot()
        {
            if (_expectedChunks <= 0 || _incomingChunks.Count < _expectedChunks) return;

            for (int i = 0; i < _expectedChunks; i++)
                if (!_incomingChunks.ContainsKey(i)) return;

            int total = 0;
            for (int i = 0; i < _expectedChunks; i++) total += _incomingChunks[i].Length;
            byte[] savBytes = new byte[total];
            int offset = 0;
            for (int i = 0; i < _expectedChunks; i++)
            {
                byte[] part = _incomingChunks[i];
                Buffer.BlockCopy(part, 0, savBytes, offset, part.Length);
                offset += part.Length;
            }
            _incomingChunks.Clear();
            _expectedChunks = 0;

            ApplyReceivedSnapshotOnClient(savBytes, _incomingCheckContent, _incomingRandomSeed, _incomingEntityIdCounter);
        }

        private static void ApplyReceivedSnapshotOnClient(byte[] savBytes, string chkContent, int randomSeed,
            long hostEntityIdCounter)
        {
            try
            {
                string savesDir = GameBridge.GetSavesDirectory();
                // Reuse the Host's exact file stem: the engine's .zxcheck sidecar appears to be bound
                // to the save name (renaming it made ReadSaveFile fail with "error while loading").
                string clientSaveName = "coop_host_snapshot";
                string savPath = Path.Combine(savesDir, clientSaveName + ".zxsav");
                string chkPath = Path.Combine(savesDir, clientSaveName + ".zxcheck");

                File.WriteAllBytes(savPath, savBytes);
                // ZXGame validates saves via Path.ChangeExtension(path, ".zxcheck") and the sidecar
                // is bound to the save name, hence we reuse the Host's file stem above.
                File.WriteAllText(chkPath, chkContent);

                ModLogger.Info($"[CoopSessionManager] Saved received snapshot to {savPath} ({savBytes.Length:N0} bytes, check={chkContent.Length} chars). Deferring load to next frame...");

                // Defer: this runs inside network packet delivery. Loading here re-enters the engine
                // and previously blew the main-thread stack under Chaos.
                _incomingRandomSeed = randomSeed;
                _incomingEntityIdCounter = hostEntityIdCounter;
                _pendingClientSnapshotLoadPath = savPath;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to apply received snapshot on Client", ex);
            }
        }

        /// <summary>Client-side snapshot load, executed from OnFrameTick before packet polling.</summary>
        private static void BeginClientSnapshotLoad(string savPath)
        {
            try
            {
                IsInSyncedLevel = false;
                LockstepClock.SetSessionPause(true);

                if (!GameBridge.IsSaveLoadable(savPath))
                {
                    ModLogger.Error($"[CoopSessionManager] ZXGame rejected the received snapshot as unloadable: {savPath}");
                    GameBridge.ShowTopBanner("Received snapshot failed the save-file validity check", Color.OrangeRed, 4000);
                    return;
                }

                // Enable the deterministic sandbox and defer ID calibration to the loader completion
                // postfix — the level is not restored when LoadSaveFileAndEnter returns.
                RequestSnapshotCalibration(_incomingRandomSeed, _incomingEntityIdCounter);
                GameBridge.PrepareForProgrammaticLoad();

                ModLogger.Info($"[CoopSessionManager] Client calling LoadSaveFileAndEnter('{savPath}')...");
                GameBridge.LoadSaveFileAndEnter(savPath);
                ModLogger.Info("[CoopSessionManager] Client LoadSaveFileAndEnter returned; driving loader fades.");

                _kickingLoadFade = true;
                _loadFadeKickFrames = 0;
                _loadFadeKickStartTicks = -1.0;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Client snapshot load threw", ex);
            }
        }

        /// <summary>
        /// Advances the engine's black loading-fade animation while the Client waits to enter the
        /// level. LoadGameAndEnter gates the real load behind a DXAnimation chain that never runs if
        /// the background window's scene does not advance it.
        /// </summary>
        private static void TickLoadingFade()
        {
            if (!_kickingLoadFade) return;

            if (!_awaitingSnapshotLoad && !_hostWaitingOwnReload && !LockstepClock.LoadingLevel)
            {
                _kickingLoadFade = false;
                return;
            }

            double now = DXHelper_Environment.Ticks;
            if (_loadFadeKickStartTicks < 0.0) _loadFadeKickStartTicks = now;
            else if (now - _loadFadeKickStartTicks >= LoadFadeTimeoutMs)
            {
                _kickingLoadFade = false;
                ModLogger.Error("[CoopSessionManager] Client snapshot load did not complete within 30s of driving the loader fades.");
                return;
            }

            GameBridge.KickLoadingFade(700.0);

            if ((++_loadFadeKickFrames % 60) == 0)
                ModLogger.Info($"[LoadFade] frames={_loadFadeKickFrames} awaited={_awaitingSnapshotLoad} loading={LockstepClock.LoadingLevel} zxLoading={GameBridge.IsGameLoading()} tick={DXGame.Current?.IAIterationInt ?? -1}");
        }
    }

    /// <summary>
    /// Hooks ZXGame.OnStartFrame and ZXSystemGameLevel.#=zmTU4kueQctVr (InitLevel)
    /// to drive CoopSessionManager every frame and auto-sync when Host enters a level.
    /// </summary>
    [HarmonyPatch]
    public static class SessionLifecyclePatches
    {
        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPostfix]
        public static void DXGame_StartFrame_Postfix()
        {
            CoopSessionManager.OnFrameTick();
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_InitLevel
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_InitLevel);
            }

            [HarmonyPrefix]
            public static void Prefix()
            {
                if (Determinism.LockstepClock.IsDeterministicModeActive)
                    Determinism.LockstepClock.ResetSyncedRandomStreamsForInitLevel();
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                if (!CoopSessionManager.IsSessionActive) return;

                if (CoopSessionManager.Transport.IsHost)
                {
                    // Snapshot export is triggered from CoopSessionManager.OnLevelLoadComplete, not
                    // here: InitLevel fires while the save is still deserializing.
                    ModLogger.Info("[SessionLifecyclePatches] Host level initialized; snapshot will be sent after load completes.");
                }
                else
                {
                    // Do NOT calibrate here: InitLevel runs on the loader's background thread and
                    // the level is not fully restored yet (see LevelLoadCompletePatch).
                    ModLogger.Info("[SessionLifecyclePatches] Client InitLevel fired; waiting for loader completion.");
                }
            }
        }
    }
}
