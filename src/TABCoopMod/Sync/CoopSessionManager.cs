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

        private static readonly List<NetCommand> _localOutgoingQueue = new List<NetCommand>();
        private static readonly List<NetCommand> _hostTurnBuffer = new List<NetCommand>();

        // Snapshot receive buffer on Client
        private static MemoryStream _incomingSaveStream;
        private static int _expectedSaveBytes;
        private static string _incomingCheckContent = string.Empty;
        private static int _incomingRandomSeed = 1337;

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
                    BroadcastCurrentLevelSnapshot();
                }
            };

            Transport.OnPeerDisconnected += playerId =>
            {
                AllyStates.Remove(playerId);
                GameBridge.ShowTopBanner($"Player {playerId} Disconnected", Color.OrangeRed, 3000);
            };

            Transport.OnPacketReceived += OnPacketReceived;
        }

        /// <summary>
        /// Exports the Host's current level state into a temporary .zxsav + .zxcheck pair
        /// and streams it to all connected peers in 32KB chunks.
        /// </summary>
        public static void BroadcastCurrentLevelSnapshot()
        {
            if (Transport == null || !Transport.IsHost || GameBridge.CurrentLevelState == null)
                return;

            try
            {
                bool wasPaused = DXGame.Current.Paused;
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
                IsInSyncedLevel = true;
                CommandInterceptor.IsInterceptionEnabled = true;

                int totalChunks = (savBytes.Length + ChunkSizeBytes - 1) / ChunkSizeBytes;
                ModLogger.Info($"[CoopSessionManager] Broadcasting Snapshot ({savBytes.Length:N0} bytes in {totalChunks} chunks)...");

                Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotBegin, w =>
                {
                    w.Write(savBytes.Length);
                    w.Write(totalChunks);
                    w.Write(chkText);
                    w.Write(syncSeed);
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

        public static void OnFrameTick()
        {
            if (!IsSessionActive) return;

            Transport.PollEvents();

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

            // 2. Host broadcasts TurnInputBroadcast (including commands queued while Paused!)
            if (Transport.IsHost)
            {
                List<NetCommand> turnCmds = null;
                lock (_hostTurnBuffer)
                {
                    if (_hostTurnBuffer.Count > 0)
                    {
                        turnCmds = new List<NetCommand>(_hostTurnBuffer);
                        _hostTurnBuffer.Clear();
                    }
                }

                if (turnCmds != null && turnCmds.Count > 0)
                {
                    int simTick = DXGame.Current?.IAIterationInt ?? 0;
                    Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.TurnInputBroadcast, w =>
                    {
                        w.Write(simTick);
                        w.Write((ushort)turnCmds.Count);
                        foreach (var c in turnCmds) c.WriteTo(w);
                    });

                    foreach (var c in turnCmds)
                    {
                        CommandExecutor.Execute(c);
                    }
                }
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
                    int totalChunks = r.ReadInt32();
                    _incomingCheckContent = r.ReadString();
                    _incomingRandomSeed = r.ReadInt32();
                    _incomingSaveStream = new MemoryStream(_expectedSaveBytes);
                    ModLogger.Info($"[CoopSessionManager] Receiving Snapshot from Host ({_expectedSaveBytes:N0} bytes, {totalChunks} chunks)...");
                    GameBridge.ShowTopBanner("Receiving Map Snapshot from Host...", Color.Cyan, 3000);
                    break;
                }

                case PacketType.SnapshotChunk:
                {
                    int chunkIdx = r.ReadInt32();
                    int len = r.ReadInt32();
                    byte[] data = r.ReadBytes(len);
                    _incomingSaveStream?.Write(data, 0, data.Length);

                    if (_incomingSaveStream != null && _incomingSaveStream.Length >= _expectedSaveBytes)
                    {
                        ApplyReceivedSnapshotOnClient(_incomingSaveStream.ToArray(), _incomingCheckContent, _incomingRandomSeed);
                        _incomingSaveStream.Dispose();
                        _incomingSaveStream = null;
                    }
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
                    int simTick = r.ReadInt32();
                    ushort count = r.ReadUInt16();
                    for (int i = 0; i < count; i++)
                    {
                        var cmd = NetCommand.ReadFrom(r);
                        CommandExecutor.Execute(cmd);
                    }
                    break;
                }

                case PacketType.StateChecksumReport:
                {
                    if (!Transport.IsHost) break;
                    int clientTick = r.ReadInt32();
                    ulong clientHash = r.ReadUInt64();
                    if (_hostTickHashes.TryGetValue(clientTick, out ulong hostHash) && hostHash != clientHash)
                    {
                        ModLogger.Warn($"[CoopSessionManager] DESYNC DETECTED at Tick {clientTick}! Host=0x{hostHash:X16}, Client({senderId})=0x{clientHash:X16}. Triggering Auto-Resync...");
                        GameBridge.ShowTopBanner("State Drift Detected — Auto-Syncing World...", Color.Orange, 2500);
                        BroadcastCurrentLevelSnapshot();
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

        private static void ApplyReceivedSnapshotOnClient(byte[] savBytes, string chkContent, int randomSeed)
        {
            try
            {
                string savesDir = GameBridge.GetSavesDirectory();
                string clientSaveName = "coop_client_sync";
                string savPath = Path.Combine(savesDir, clientSaveName + ".zxsav");
                string chkPath = Path.Combine(savesDir, clientSaveName + ".zxcheck");

                File.WriteAllBytes(savPath, savBytes);
                File.WriteAllText(chkPath, chkContent);

                ModLogger.Info($"[CoopSessionManager] Saved received snapshot to {savPath} ({savBytes.Length:N0} bytes). Loading level...");

                DXGame.Current.InvokeOnStartFrame(() =>
                {
                    GameBridge.LoadSaveFileAndEnter(savPath);
                    LockstepClock.CalibrateEntityIdCounterFromLoadedLevel(randomSeed);
                    IsInSyncedLevel = true;
                    CommandInterceptor.IsInterceptionEnabled = true;
                });
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to apply received snapshot on Client", ex);
            }
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
                    ModLogger.Info("[SessionLifecyclePatches] Host level initialized! Scheduling initial snapshot broadcast...");
                    DXGame.Current.InvokeOnFinishFrame(() =>
                    {
                        CoopSessionManager.BroadcastCurrentLevelSnapshot();
                    });
                }
                else
                {
                    LockstepClock.CalibrateEntityIdCounterFromLoadedLevel(20261109);
                    CoopSessionManager.OnFrameTick();
                    var digest = StateHasher.ComputeDigest();
                    ModLogger.Info($"[SessionLifecyclePatches] Client level loaded! Initial {digest}");
                }
            }
        }
    }
}
