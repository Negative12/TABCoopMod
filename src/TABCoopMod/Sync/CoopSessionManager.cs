using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
        private static double _lastAutoResyncTicks = -100000.0;
        private static bool _desyncDumped;

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

        public static readonly Dictionary<byte, AllyVisualState> AllyStates = new Dictionary<byte, AllyVisualState>();
        private static double _lastCursorBroadcastTicks;
        private static int _lastCheckedSimTick = -1;
        private static bool _firstChecksumPosDumped;
        private static readonly Dictionary<int, ulong> _hostTickHashes = new Dictionary<int, ulong>();
        private static readonly Dictionary<int, Dictionary<byte, ulong>> _clientTickHashes =
            new Dictionary<int, Dictionary<byte, ulong>>();
        private static readonly Dictionary<int, string> _hostTickDigestStrings = new Dictionary<int, string>();
        private static readonly Dictionary<int, Dictionary<byte, string>> _clientTickDigestStrings =
            new Dictionary<int, Dictionary<byte, string>>();

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
            _hostTickHashes.Clear();
            _clientTickHashes.Clear();
            _hostTickDigestStrings.Clear();
            _clientTickDigestStrings.Clear();
            LockstepClock.DisableDeterministicSandbox();
            AllyStates.Clear();
            _localOutgoingQueue.Clear();
            _hostTurnBuffer.Clear();
            _pendingTurnCommands.Clear();
            _hostLastBroadcastSimTick = -1;
            _gateWaitStartTicks = -1.0;
            _awaitingSnapshotLoad = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _hostSnapshotReloadPending = false;
            _hostWaitingOwnReload = false;
            _clientSnapshotAckReceived = false;
            _lastAutoResyncTicks = -100000.0;
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

                _hostTickHashes.Clear();
                _clientTickHashes.Clear();
                _hostTickDigestStrings.Clear();
                _clientTickDigestStrings.Clear();
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

                // Reload the exact exported snapshot on Host too so MapDrawer, Colonist.OnSceneAdded,
                // and Zombie.UseFastSerializing run the identical load pipeline on both peers.
                ModLogger.Info($"[CoopSessionManager] Host starting symmetric reload of {savPath}...");
                LockstepClock.EnableDeterministicSandbox(syncSeed);
                LockstepClock.RequestPostLoadCalibration(syncSeed, idCounter);
                if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false;
                GameBridge.PrepareForProgrammaticLoad();
                GameBridge.LoadSaveFileAndEnter(savPath);
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

            _hostTickHashes.Clear();
            _clientTickHashes.Clear();
            _hostTickDigestStrings.Clear();
            _clientTickDigestStrings.Clear();
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
            if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false;

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

                // Prune stale hashes (keep ~20s of history) so dictionaries cannot grow unbounded.
                if (Transport.IsHost)
                {
                    if (_hostTickHashes.Count > 0)
                    {
                        var stale = new List<int>();
                        foreach (var k in _hostTickHashes.Keys)
                            if (k < curTick - 600) stale.Add(k);
                        foreach (var k in stale) _hostTickHashes.Remove(k);
                    }
                    if (_clientTickHashes.Count > 0)
                    {
                        var stale = new List<int>();
                        foreach (var k in _clientTickHashes.Keys)
                            if (k < curTick - 600) stale.Add(k);
                        foreach (var k in stale) _clientTickHashes.Remove(k);
                    }
                    if (_hostTickDigestStrings.Count > 0)
                    {
                        var stale = new List<int>();
                        foreach (var k in _hostTickDigestStrings.Keys)
                            if (k < curTick - 600) stale.Add(k);
                        foreach (var k in stale) _hostTickDigestStrings.Remove(k);
                    }
                    if (_clientTickDigestStrings.Count > 0)
                    {
                        var stale = new List<int>();
                        foreach (var k in _clientTickDigestStrings.Keys)
                            if (k < curTick - 600) stale.Add(k);
                        foreach (var k in stale) _clientTickDigestStrings.Remove(k);
                    }
                }

                var digest = StateHasher.ComputeDigest();
                string digestSummary =
                    $"Pos={digest.PosChecksum:X16} Life={digest.LifeChecksum:X16} Proj={digest.ProjectileChecksum:X16} " +
                    $"Prod={digest.ProductionChecksum:X16} Resv={digest.ReservationChecksum:X16} " +
                    $"(entities={digest.EntityCount} proj={digest.ProjectileCount} queued={digest.QueuedOrderCount} " +
                    $"res={digest.Gold}/{digest.Wood}/{digest.Stone}/{digest.Iron}/{digest.Oil} time={digest.GameTimeMs})";

                if (Transport.IsHost)
                {
                    _hostTickHashes[digest.SimTick] = digest.FullHash;
                    _hostTickDigestStrings[digest.SimTick] = digestSummary;
                    VerifyTickChecksum(digest.SimTick);
                }
                else
                {
                    Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.StateChecksumReport, w =>
                    {
                        w.Write(digest.SimTick);
                        w.Write(digest.FullHash);
                        w.Write(digestSummary);
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
                    _hostTickHashes.Clear();
                    _clientTickHashes.Clear();
                    _hostTickDigestStrings.Clear();
                    _clientTickDigestStrings.Clear();
                    _lastCheckedSimTick = -1;
                    EnterSyncedLevel();
                    ModLogger.Info($"[CoopSessionManager] Received SessionStart from Host (tick={startTick}, localTick={DXGame.Current?.IAIterationInt ?? 0}, seed={seed}, idCounter={idCounter}). Unpausing in lockstep!");
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
                    int clientTick = r.ReadInt32();
                    ulong clientHash = r.ReadUInt64();
                    string clientSummary = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : string.Empty;
                    if (!_clientTickHashes.TryGetValue(clientTick, out var byPeer))
                        _clientTickHashes[clientTick] = byPeer = new Dictionary<byte, ulong>();
                    byPeer[senderId] = clientHash;
                    if (!_clientTickDigestStrings.TryGetValue(clientTick, out var summaryByPeer))
                        _clientTickDigestStrings[clientTick] = summaryByPeer = new Dictionary<byte, string>();
                    summaryByPeer[senderId] = clientSummary;
                    VerifyTickChecksum(clientTick);
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

        private static void VerifyTickChecksum(int tick)
        {
            if (!_hostTickHashes.TryGetValue(tick, out ulong hostHash)) return;
            if (!_clientTickHashes.TryGetValue(tick, out var byPeer) || byPeer.Count == 0) return;

            var resolvedPeers = new List<byte>();
            foreach (var kv in byPeer)
            {
                byte senderId = kv.Key;
                ulong clientHash = kv.Value;
                if (hostHash == clientHash)
                {
                    ModLogger.Info($"[StateChecksum] Tick {tick} MATCH (0x{hostHash:X16}) with Client({senderId}).");
                    resolvedPeers.Add(senderId);
                }
                else
                {
                    _hostTickDigestStrings.TryGetValue(tick, out string hostSummary);
                    string clientSummary = null;
                    if (_clientTickDigestStrings.TryGetValue(tick, out var sByPeer))
                        sByPeer.TryGetValue(senderId, out clientSummary);

                    ModLogger.Error($"[StateChecksum] DESYNC DETECTED at Tick {tick}! Host=0x{hostHash:X16}, Client({senderId})=0x{clientHash:X16}");
                    ModLogger.Error($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                    ModLogger.Error($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                    if (!_desyncDumped)
                    {
                        _desyncDumped = true;
                        DumpHostEntitiesOnDesync(tick);
                    }
                    GameBridge.ShowTopBanner("State Drift Detected (see log)", Color.Orange, 2500);
                    resolvedPeers.Add(senderId);
                    double now = DXHelper_Environment.Ticks;
                    if (EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport && (now - _lastAutoResyncTicks >= 15000.0))
                    {
                        _lastAutoResyncTicks = now;
                        ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after DESYNC at Tick {tick}.");
                        RequestSnapshotBroadcast();
                    }
                }
            }
            foreach (var pid in resolvedPeers)
            {
                byPeer.Remove(pid);
                if (_clientTickDigestStrings.TryGetValue(tick, out var sByPeer)) sByPeer.Remove(pid);
            }
            if (byPeer.Count == 0)
            {
                _clientTickHashes.Remove(tick);
                _clientTickDigestStrings.Remove(tick);
            }
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
