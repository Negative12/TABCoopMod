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

        public static INetworkTransport Transport { get; private set; }
        public static bool IsSessionActive => Transport != null && Transport.IsActive;
        public static bool IsInSyncedLevel { get; private set; }

        /// <summary>Auto-resync is opt-in until in-level reloads stop crashing the engine scene update.</summary>
        public static bool EnableAutoResync = false;

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
        private static readonly Dictionary<int, ulong> _hostTickHashes = new Dictionary<int, ulong>();

        public static void StartHostSession(bool useLoopback)
        {
            StopSession();
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
            LockstepClock.DisableDeterministicSandbox();
            AllyStates.Clear();
            _localOutgoingQueue.Clear();
            _hostTurnBuffer.Clear();
            _pendingTurnCommands.Clear();
            _hostLastBroadcastSimTick = -1;
            _gateWaitStartTicks = -1.0;
            _awaitingSnapshotLoad = false;
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
                if (Transport.IsHost && GameBridge.CurrentLevelState != null)
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
                int syncSeed = 20261109;

                LockstepClock.CalibrateEntityIdCounterFromLoadedLevel(syncSeed);
                EnterSyncedLevel();

                int totalChunks = (savBytes.Length + ChunkSizeBytes - 1) / ChunkSizeBytes;
                ModLogger.Info($"[CoopSessionManager] Broadcasting Snapshot ({savBytes.Length:N0} bytes in {totalChunks} chunks)...");

                Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotBegin, w =>
                {
                    w.Write(savBytes.Length);
                    w.Write(totalChunks);
                    w.Write(chkText);
                    w.Write(syncSeed);
                    w.Write(LockstepClock.CurrentSyncedEntityIdCounter);
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

                GameBridge.ShowTopBanner($"Synced Map Snapshot to Peers ({savBytes.Length / 1024} KB)", Color.Cyan, 2500);
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to broadcast level snapshot", ex);
            }
            finally
            {
                // Never restore a zero speed: that would look like an engine pause and stall the
                // host heartbeat. Session pauses are restarted explicitly via SetPaused commands.
                try { DXGame.Current.GameSpeed = wasGameSpeed > 0.0 ? wasGameSpeed : 1.0; } catch { }
            }
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
                // Only now is the level fully restored: exporting earlier (from InitLevel) captured a
                // half-deserialized save and entered the synced level before the host was ready.
                if (!IsInSyncedLevel)
                {
                    EnterSyncedLevel();
                    RequestSnapshotBroadcast();
                }
                return;
            }

            if (!_awaitingSnapshotLoad) return;

            _awaitingSnapshotLoad = false;
            EnterSyncedLevel();
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

            // 4. Every 60 SimTicks (2 seconds), compute and verify deterministic StateHash
            int curTick = DXGame.Current?.IAIterationInt ?? 0;
            if (curTick > 0 && curTick % 60 == 0 && curTick != _lastCheckedSimTick)
            {
                _lastCheckedSimTick = curTick;

                // Prune stale host hashes (keep ~20s of history) so the dictionary cannot grow unbounded.
                if (Transport.IsHost && _hostTickHashes.Count > 0)
                {
                    var stale = new List<int>();
                    foreach (var k in _hostTickHashes.Keys)
                        if (k < curTick - 600) stale.Add(k);
                    foreach (var k in stale) _hostTickHashes.Remove(k);
                }

                var digest = StateHasher.ComputeDigest();

                if (Transport.IsHost)
                {
                    _hostTickHashes[curTick] = digest.FullHash;
                }
                else
                {
                    Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.StateChecksumReport, w =>
                    {
                        w.Write(digest.SimTick);
                        w.Write(digest.FullHash);
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
                    if (_hostTickHashes.TryGetValue(clientTick, out ulong hostHash) && hostHash != clientHash)
                    {
                        ModLogger.Warn($"[CoopSessionManager] DESYNC DETECTED at Tick {clientTick}! Host=0x{hostHash:X16}, Client({senderId})=0x{clientHash:X16}.");
                        GameBridge.ShowTopBanner("State Drift Detected (see log)", Color.Orange, 2500);
                        // Auto-resync is disabled: re-loading a save while a level is already running
                        // crashes the engine's scene update (CShadow NRE) and, with the known `pos`
                        // residual, would loop forever. Re-enable once position determinism is fixed.
                        if (EnableAutoResync) RequestSnapshotBroadcast();
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
                    AllyOverlayRenderer.TriggerMinimapAndGroundPing(pid, new Point(cellX, cellY), label);
                    break;
                }
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

            if (!_awaitingSnapshotLoad && !LockstepClock.LoadingLevel)
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
