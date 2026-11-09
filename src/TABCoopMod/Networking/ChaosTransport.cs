using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DXVision;
using TABCoopMod.Core;

namespace TABCoopMod.Networking
{
    public struct DisconnectWindow
    {
        public int StartMs;
        public int EndMs;
    }

    /// <summary>
    /// Deterministic fault-injection parameters for the loopback transport.
    /// Spec example: "latency=100;jitter=25;loss=0.03;disconnect=5000-7000;seed=42".
    /// </summary>
    public sealed class ChaosConfig
    {
        public int Seed = 12345;
        public int LatencyMs;
        public int JitterMs;
        public double LossRate;
        public readonly List<DisconnectWindow> DisconnectWindows = new List<DisconnectWindow>();

        public static ChaosConfig FromString(string spec)
        {
            var cfg = new ChaosConfig();
            if (string.IsNullOrWhiteSpace(spec)) return cfg;

            foreach (string raw in spec.Split(';'))
            {
                string part = raw.Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim().ToLowerInvariant();
                string val = part.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "latency": cfg.LatencyMs = ParseInt(val, cfg.LatencyMs); break;
                    case "jitter": cfg.JitterMs = ParseInt(val, cfg.JitterMs); break;
                    case "loss": cfg.LossRate = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double l) ? l : cfg.LossRate; break;
                    case "seed": cfg.Seed = ParseInt(val, cfg.Seed); break;
                    case "disconnect":
                        foreach (string range in val.Split(','))
                        {
                            var dash = range.Split('-');
                            if (dash.Length == 2 &&
                                int.TryParse(dash[0], out int s) && int.TryParse(dash[1], out int e))
                            {
                                cfg.DisconnectWindows.Add(new DisconnectWindow { StartMs = s, EndMs = e });
                            }
                        }
                        break;
                }
            }
            return cfg;
        }

        private static int ParseInt(string s, int fallback) => int.TryParse(s, out int v) ? v : fallback;
    }

    /// <summary>
    /// INetworkTransport decorator that injects latency, jitter, packet loss and scheduled
    /// disconnect windows around an inner transport (typically LocalTcpLoopbackTransport).
    /// Timing uses the engine's monotonic Ticks clock; jitter/loss use a seeded xorshift PRNG so a
    /// given seed reproduces the same delivery pattern.
    /// </summary>
    public sealed class ChaosTransport : INetworkTransport
    {
        private sealed class PendingPacket
        {
            public double DeliverAt;
            public bool Broadcast;
            public bool Incoming;
            public byte Target;
            public byte Sender;
            public SendChannel Channel;
            public PacketType Type;
            public byte[] Payload;
        }

        private readonly INetworkTransport _inner;
        private readonly ChaosConfig _cfg;
        private readonly double _startTicks;
        private readonly List<PendingPacket> _queue = new List<PendingPacket>();
        private uint _rngState;

        public long Delivered { get; private set; }
        public long Dropped { get; private set; }
        public long Scheduled { get; private set; }

        public ChaosTransport(INetworkTransport inner, ChaosConfig cfg)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cfg = cfg ?? new ChaosConfig();
            _rngState = (uint)(_cfg.Seed == 0 ? 1 : _cfg.Seed);
            _startTicks = DXHelper_Environment.Ticks;

            _inner.OnPacketReceived += OnInnerPacketReceived;
            _inner.OnPeerConnected += p => OnPeerConnected?.Invoke(p);
            _inner.OnPeerDisconnected += id => OnPeerDisconnected?.Invoke(id);

            ModLogger.Info($"[ChaosTransport] enabled: latency={_cfg.LatencyMs}ms jitter={_cfg.JitterMs}ms loss={_cfg.LossRate:P1} windows={_cfg.DisconnectWindows.Count} seed={_cfg.Seed}");
        }

        public bool IsActive => _inner.IsActive;
        public bool IsHost => _inner.IsHost;
        public byte LocalPlayerId => _inner.LocalPlayerId;
        public IReadOnlyList<PeerInfo> ConnectedPeers => _inner.ConnectedPeers;

        public event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        public event Action<PeerInfo> OnPeerConnected;
        public event Action<byte> OnPeerDisconnected;

        public void StartHost(int maxPlayers = 3) => _inner.StartHost(maxPlayers);
        public void ConnectToHost(string targetAddressOrSteamId) => _inner.ConnectToHost(targetAddressOrSteamId);

        public void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            Schedule(false, false, targetPlayerId, 0, channel, type, Serialize(writePayload));
        }

        public void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            Schedule(true, false, 0, 0, channel, type, Serialize(writePayload));
        }

        public void PollEvents()
        {
            _inner.PollEvents();
            DeliverDue();
        }

        private void OnInnerPacketReceived(byte senderId, PacketType type, BinaryReader reader)
        {
            byte[] payload;
            try
            {
                int remaining = (int)Math.Max(0, reader.BaseStream.Length - reader.BaseStream.Position);
                payload = reader.ReadBytes(remaining);
            }
            catch { return; }

            Schedule(false, true, 0, senderId, SendChannel.ReliableOrdered, type, payload);
        }

        private void Schedule(bool broadcast, bool incoming, byte target, byte sender, SendChannel channel, PacketType type, byte[] payload)
        {
            if (IsDisconnectedNow()) { Dropped++; return; }
            if (_cfg.LossRate > 0.0 && NextFloat() < _cfg.LossRate) { Dropped++; return; }

            double delay = _cfg.LatencyMs + (_cfg.JitterMs > 0 ? NextFloat() * _cfg.JitterMs : 0);
            _queue.Add(new PendingPacket
            {
                DeliverAt = DXHelper_Environment.Ticks + delay,
                Broadcast = broadcast,
                Incoming = incoming,
                Target = target,
                Sender = sender,
                Channel = channel,
                Type = type,
                Payload = payload
            });
            Scheduled++;
        }

        private void DeliverDue()
        {
            double now = DXHelper_Environment.Ticks;
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var p = _queue[i];
                if (p.DeliverAt > now) continue;
                _queue.RemoveAt(i);

                if (IsDisconnectedNow()) { Dropped++; continue; }

                if (p.Incoming)
                {
                    using (var ms = new MemoryStream(p.Payload, false))
                    using (var reader = new BinaryReader(ms))
                    {
                        OnPacketReceived?.Invoke(p.Sender, p.Type, reader);
                    }
                }
                else
                {
                    if (p.Broadcast)
                        _inner.Broadcast(p.Channel, p.Type, w => w.Write(p.Payload));
                    else
                        _inner.SendToPeer(p.Target, p.Channel, p.Type, w => w.Write(p.Payload));
                }
                Delivered++;
            }
        }

        private bool IsDisconnectedNow()
        {
            if (_cfg.DisconnectWindows.Count == 0) return false;
            double elapsed = DXHelper_Environment.Ticks - _startTicks;
            for (int i = 0; i < _cfg.DisconnectWindows.Count; i++)
            {
                var w = _cfg.DisconnectWindows[i];
                if (elapsed >= w.StartMs && elapsed <= w.EndMs) return true;
            }
            return false;
        }

        private float NextFloat()
        {
            _rngState ^= _rngState << 13;
            _rngState ^= _rngState >> 17;
            _rngState ^= _rngState << 5;
            return (_rngState & 0x00FFFFFF) / 16777216f;
        }

        private static byte[] Serialize(Action<BinaryWriter> writePayload)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                writePayload?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public void Dispose()
        {
            try { _inner.OnPacketReceived -= OnInnerPacketReceived; } catch { }
            ModLogger.Info($"[ChaosTransport] stats: scheduled={Scheduled}, delivered={Delivered}, dropped={Dropped}");
            _inner.Dispose();
        }
    }
}
