using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DXVision;
using Steamworks;
using TABCoopMod.Commands;
using TABCoopMod.Core;

namespace TABCoopMod.Networking
{
    public enum SendChannel : byte
    {
        ReliableOrdered = 0,
        UnreliableSequenced = 1
    }

    public enum PacketType : byte
    {
        HandshakeRequest = 0x01,
        HandshakeAccept = 0x02,
        SnapshotBegin = 0x10,
        SnapshotChunk = 0x11,
        SnapshotLoadedAck = 0x12,
        SessionStart = 0x13,
        ClientInputReport = 0x20,
        TurnInputBroadcast = 0x21,
        StateChecksumReport = 0x30,
        DesyncDetectedAlert = 0x31,
        PosDriftSyncChunk = 0x32,
        PosDriftStatsReport = 0x33,
        AllyCursorAndCamera = 0x40,
        TacticalPing = 0x41
    }

    public sealed class PeerInfo
    {
        public byte PlayerId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public ulong SteamId64 { get; set; }
        public bool IsReady { get; set; }
    }

    public interface INetworkTransport : IDisposable
    {
        bool IsActive { get; }
        bool IsHost { get; }
        byte LocalPlayerId { get; }
        IReadOnlyList<PeerInfo> ConnectedPeers { get; }

        event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        event Action<PeerInfo> OnPeerConnected;
        event Action<byte> OnPeerDisconnected;

        void StartHost(int maxPlayers = 3);
        void ConnectToHost(string targetAddressOrSteamId);
        void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void PollEvents();
    }

    /// <summary>
    /// Local TCP Loopback Transport (127.0.0.1:27099) for AI Agent automated dual-instance testing
    /// and local multi-window debugging without Steam Lobby overhead.
    /// </summary>
    public sealed class LocalTcpLoopbackTransport : INetworkTransport
    {
        public const int DefaultPort = 27099;

        private TcpListener _listener;
        private readonly List<(byte PlayerId, TcpClient Client, NetworkStream Stream)> _clients = new List<(byte, TcpClient, NetworkStream)>();
        private TcpClient _clientToHost;
        private NetworkStream _streamToHost;
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private byte _nextPlayerId = 2;

        public bool IsActive { get; private set; }
        public bool IsHost { get; private set; }
        public byte LocalPlayerId { get; private set; } = 1;
        public IReadOnlyList<PeerInfo> ConnectedPeers => _peers;

        public event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        public event Action<PeerInfo> OnPeerConnected;
        public event Action<byte> OnPeerDisconnected;

        public void StartHost(int maxPlayers = 3)
        {
            Dispose();
            IsHost = true;
            LocalPlayerId = 1;
            _nextPlayerId = 2;
            _peers.Clear();
            _peers.Add(new PeerInfo { PlayerId = 1, PlayerName = "HostPlayer", IsReady = true });

            _listener = new TcpListener(IPAddress.Loopback, DefaultPort);
            _listener.Start();
            IsActive = true;
            ModLogger.Info($"[LocalTcpLoopbackTransport] Host listening on 127.0.0.1:{DefaultPort}");
        }

        public void ConnectToHost(string targetAddressOrSteamId)
        {
            Dispose();
            IsHost = false;
            LocalPlayerId = 0; // Assigned by Host upon HandshakeAccept
            _peers.Clear();

            string host = "127.0.0.1";
            int port = DefaultPort;
            if (!string.IsNullOrEmpty(targetAddressOrSteamId) && targetAddressOrSteamId.Contains(":"))
            {
                var parts = targetAddressOrSteamId.Split(':');
                host = parts[0];
                int.TryParse(parts[1], out port);
            }

            _clientToHost = new TcpClient { NoDelay = true };
            _clientToHost.Connect(host, port);
            _streamToHost = _clientToHost.GetStream();
            IsActive = true;

            ModLogger.Info($"[LocalTcpLoopbackTransport] Connected to Host at {host}:{port}, sending HandshakeRequest...");
            SendToPeer(1, SendChannel.ReliableOrdered, PacketType.HandshakeRequest, w =>
            {
                w.Write(Environment.UserName ?? "ClientPlayer");
                w.Write("0.1.0");
            });
        }

        public void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            byte[] frame = BuildFrame(LocalPlayerId, type, writePayload);

            if (IsHost)
            {
                foreach (var c in _clients)
                {
                    if (c.PlayerId == targetPlayerId)
                    {
                        TryWriteFrame(c.Stream, frame);
                        break;
                    }
                }
            }
            else if (_streamToHost != null)
            {
                TryWriteFrame(_streamToHost, frame);
            }
        }

        public void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            byte[] frame = BuildFrame(LocalPlayerId, type, writePayload);

            if (IsHost)
            {
                foreach (var c in _clients)
                {
                    TryWriteFrame(c.Stream, frame);
                }
            }
            else if (_streamToHost != null)
            {
                TryWriteFrame(_streamToHost, frame);
            }
        }

        public void PollEvents()
        {
            if (!IsActive) return;

            try
            {
                if (IsHost)
                {
                    while (_listener != null && _listener.Pending())
                    {
                        var tcp = _listener.AcceptTcpClient();
                        tcp.NoDelay = true;
                        byte assignedId = _nextPlayerId++;
                        var stream = tcp.GetStream();
                        _clients.Add((assignedId, tcp, stream));
                        ModLogger.Info($"[LocalTcpLoopbackTransport] Accepted client connection -> Assigned PlayerId={assignedId}");
                    }

                    for (int i = _clients.Count - 1; i >= 0; i--)
                    {
                        var entry = _clients[i];
                        if (!ReadAvailableFrames(entry.Stream, entry.PlayerId))
                        {
                            _clients.RemoveAt(i);
                            _peers.RemoveAll(p => p.PlayerId == entry.PlayerId);
                            OnPeerDisconnected?.Invoke(entry.PlayerId);
                        }
                    }
                }
                else if (_streamToHost != null)
                {
                    if (!ReadAvailableFrames(_streamToHost, 1))
                    {
                        ModLogger.Warn("[LocalTcpLoopbackTransport] Disconnected from Host.");
                        Dispose();
                        OnPeerDisconnected?.Invoke(1);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[LocalTcpLoopbackTransport] Exception in PollEvents", ex);
            }
        }

        private bool ReadAvailableFrames(NetworkStream stream, byte defaultSenderId)
        {
            try
            {
                while (stream.DataAvailable)
                {
                    byte[] header = new byte[8];
                    int read = 0;
                    while (read < 8)
                    {
                        int n = stream.Read(header, read, 8 - read);
                        if (n <= 0) return false;
                        read += n;
                    }

                    if (header[0] != 0x54 || header[1] != 0x42)
                    {
                        ModLogger.Warn("[LocalTcpLoopbackTransport] Invalid frame magic!");
                        return false;
                    }

                    var pktType = (PacketType)header[2];
                    byte senderId = header[3] != 0 ? header[3] : defaultSenderId;
                    int payloadLen = BitConverter.ToInt32(header, 4);

                    byte[] payload = new byte[payloadLen];
                    int offset = 0;
                    while (offset < payloadLen)
                    {
                        int n = stream.Read(payload, offset, payloadLen - offset);
                        if (n <= 0) return false;
                        offset += n;
                    }

                    using (var ms = new MemoryStream(payload, false))
                    using (var reader = new BinaryReader(ms))
                    {
                        HandleInternalOrDispatch(senderId, pktType, reader);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void HandleInternalOrDispatch(byte senderId, PacketType pktType, BinaryReader reader)
        {
            if (IsHost && pktType == PacketType.HandshakeRequest)
            {
                string clientName = reader.ReadString();
                string version = reader.ReadString();
                var peer = new PeerInfo { PlayerId = senderId, PlayerName = $"{clientName}#{senderId}", IsReady = true };
                _peers.RemoveAll(p => p.PlayerId == senderId);
                _peers.Add(peer);

                SendToPeer(senderId, SendChannel.ReliableOrdered, PacketType.HandshakeAccept, w =>
                {
                    w.Write(senderId);
                    w.Write((byte)_peers.Count);
                    foreach (var p in _peers)
                    {
                        w.Write(p.PlayerId);
                        w.Write(p.PlayerName);
                    }
                });

                OnPeerConnected?.Invoke(peer);
                return;
            }

            if (!IsHost && pktType == PacketType.HandshakeAccept)
            {
                LocalPlayerId = reader.ReadByte();
                byte count = reader.ReadByte();
                _peers.Clear();
                for (int i = 0; i < count; i++)
                {
                    _peers.Add(new PeerInfo
                    {
                        PlayerId = reader.ReadByte(),
                        PlayerName = reader.ReadString(),
                        IsReady = true
                    });
                }
                ModLogger.Info($"[LocalTcpLoopbackTransport] HandshakeAccepted! LocalPlayerId={LocalPlayerId}, Peers={_peers.Count}");
                OnPeerConnected?.Invoke(_peers.Find(p => p.PlayerId == LocalPlayerId));
                return;
            }

            OnPacketReceived?.Invoke(senderId, pktType, reader);
        }

        internal static byte[] BuildFrame(byte senderId, PacketType type, Action<BinaryWriter> writePayload)
        {
            using (var payloadMs = new MemoryStream())
            using (var payloadWriter = new BinaryWriter(payloadMs))
            {
                writePayload?.Invoke(payloadWriter);
                payloadWriter.Flush();
                byte[] payloadBytes = payloadMs.ToArray();

                using (var frameMs = new MemoryStream(8 + payloadBytes.Length))
                using (var frameWriter = new BinaryWriter(frameMs))
                {
                    frameWriter.Write((byte)0x54); // 'T'
                    frameWriter.Write((byte)0x42); // 'B'
                    frameWriter.Write((byte)type);
                    frameWriter.Write(senderId);
                    frameWriter.Write(payloadBytes.Length);
                    frameWriter.Write(payloadBytes);
                    frameWriter.Flush();
                    return frameMs.ToArray();
                }
            }
        }

        private static void TryWriteFrame(NetworkStream stream, byte[] frame)
        {
            try
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            IsActive = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;

            foreach (var c in _clients)
            {
                try { c.Stream?.Close(); c.Client?.Close(); } catch { }
            }
            _clients.Clear();

            try { _streamToHost?.Close(); _clientToHost?.Close(); } catch { }
            _streamToHost = null;
            _clientToHost = null;
        }
    }
}
