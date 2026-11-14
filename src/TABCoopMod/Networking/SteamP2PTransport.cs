using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using TABCoopMod.Core;

namespace TABCoopMod.Networking
{
    /// <summary>
    /// Steamworks P2P Networking & Lobby Transport built on top of the game's embedded Steamworks.NET (v10.0.0.0).
    /// Supports Steam Lobby creation, Friend Invite overlay, and P2P packet relaying (Channel 0 Reliable, Channel 1 Unreliable).
    /// </summary>
    public sealed class SteamP2PTransport : INetworkTransport
    {
        private static Callback<GameLobbyJoinRequested_t> _globalCbLobbyJoinRequested;
        private static Action<string> _globalInviteHandler;
        private static bool _startupLobbyChecked;
        private static string _pendingInviteLobbyId;

        private Callback<LobbyCreated_t> _cbLobbyCreated;
        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> _cbLobbyChatUpdate;
        private Callback<P2PSessionRequest_t> _cbP2PSessionRequest;
        private Callback<P2PSessionConnectFail_t> _cbP2PConnectFail;

        public static void RegisterGlobalInviteListener(Action<string> onInviteAccepted)
        {
            _globalInviteHandler = onInviteAccepted;
            try
            {
                if (_globalCbLobbyJoinRequested == null)
                {
                    _globalCbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(param =>
                    {
                        string lobbyId = param.m_steamIDLobby.m_SteamID.ToString();
                        ModLogger.Info($"[SteamP2PTransport] Global Steam Invite accepted! LobbyID={lobbyId}");
                        _pendingInviteLobbyId = lobbyId;
                        _globalInviteHandler?.Invoke(lobbyId);
                    });
                    ModLogger.Info("[SteamP2PTransport] Global GameLobbyJoinRequested_t listener registered.");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[SteamP2PTransport] Failed to register global invite listener: {ex.Message}");
            }

            if (!_startupLobbyChecked)
            {
                _startupLobbyChecked = true;
                try
                {
                    string[] args = Environment.GetCommandLineArgs();
                    for (int i = 0; i < args.Length - 1; i++)
                    {
                        if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                            ulong.TryParse(args[i + 1], out ulong lobbyId) && lobbyId != 0)
                        {
                            ModLogger.Info($"[SteamP2PTransport] Detected startup +connect_lobby {lobbyId}");
                            _pendingInviteLobbyId = lobbyId.ToString();
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        public static string ConsumePendingInviteLobbyId()
        {
            string id = _pendingInviteLobbyId;
            _pendingInviteLobbyId = null;
            return id;
        }

        public static void PumpSteamCallbacksIfNeeded(INetworkTransport activeTransport)
        {
            if (activeTransport is SteamP2PTransport) return;
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch { }
        }

        private CSteamID _currentLobbyId = CSteamID.Nil;
        private CSteamID _hostSteamId = CSteamID.Nil;
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private readonly Dictionary<byte, CSteamID> _playerToSteamId = new Dictionary<byte, CSteamID>();
        private byte _nextPlayerId = 2;

        public bool IsActive { get; private set; }
        public bool IsHost { get; private set; }
        public byte LocalPlayerId { get; private set; } = 1;
        public CSteamID CurrentLobbyId => _currentLobbyId;
        public IReadOnlyList<PeerInfo> ConnectedPeers => _peers;

        public event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        public event Action<PeerInfo> OnPeerConnected;
        public event Action<byte> OnPeerDisconnected;

        public void EnsureCallbacksRegistered()
        {
            if (_cbLobbyCreated != null) return;

            _cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _cbP2PSessionRequest = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
            _cbP2PConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnP2PConnectFail);
        }

        public void StartHost(int maxPlayers = 3)
        {
            EnsureCallbacksRegistered();
            IsHost = true;
            LocalPlayerId = 1;
            _nextPlayerId = 2;
            _peers.Clear();
            _playerToSteamId.Clear();

            var mySteamId = SteamUser.GetSteamID();
            string myName = SteamFriends.GetPersonaName() ?? "Host";
            _hostSteamId = mySteamId;
            _playerToSteamId[1] = mySteamId;
            _peers.Add(new PeerInfo { PlayerId = 1, PlayerName = myName, SteamId64 = mySteamId.m_SteamID, IsReady = true });

            SteamNetworking.AllowP2PPacketRelay(true);
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            IsActive = true;
            ModLogger.Info($"[SteamP2PTransport] Creating Steam Friends-Only Lobby for up to {maxPlayers} players...");
        }

        public void ConnectToHost(string targetAddressOrSteamId)
        {
            EnsureCallbacksRegistered();
            IsHost = false;
            LocalPlayerId = 0;
            _peers.Clear();
            _playerToSteamId.Clear();
            SteamNetworking.AllowP2PPacketRelay(true);

            if (ulong.TryParse(targetAddressOrSteamId, out ulong steamOrLobbyId))
            {
                var targetId = new CSteamID(steamOrLobbyId);
                if (targetId.IsLobby())
                {
                    ModLogger.Info($"[SteamP2PTransport] Joining Steam Lobby {steamOrLobbyId}...");
                    SteamMatchmaking.JoinLobby(targetId);
                    IsActive = true;
                }
                else
                {
                    _hostSteamId = targetId;
                    _playerToSteamId[1] = _hostSteamId;
                    IsActive = true;
                    SendHandshakeToHost();
                }
            }
        }

        public void OpenSteamInviteOverlay()
        {
            if (_currentLobbyId.IsValid())
            {
                SteamFriends.ActivateGameOverlayInviteDialog(_currentLobbyId);
            }
        }

        private void OnLobbyCreated(LobbyCreated_t param)
        {
            if (param.m_eResult != EResult.k_EResultOK)
            {
                ModLogger.Error($"[SteamP2PTransport] CreateLobby failed: {param.m_eResult}");
                return;
            }
            _currentLobbyId = new CSteamID(param.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(_currentLobbyId, "tab_coop_host", SteamUser.GetSteamID().m_SteamID.ToString());
            SteamMatchmaking.SetLobbyData(_currentLobbyId, "tab_coop_ver", "0.1.0");
            ModLogger.Info($"[SteamP2PTransport] Steam Lobby created! LobbyID={_currentLobbyId.m_SteamID}");
        }

        private void OnLobbyEntered(LobbyEnter_t param)
        {
            _currentLobbyId = new CSteamID(param.m_ulSteamIDLobby);
            if (IsHost) return;

            var ownerId = SteamMatchmaking.GetLobbyOwner(_currentLobbyId);
            _hostSteamId = ownerId;
            _playerToSteamId[1] = ownerId;
            ModLogger.Info($"[SteamP2PTransport] Entered Lobby {_currentLobbyId.m_SteamID}, Host SteamID={ownerId.m_SteamID}. Sending Handshake...");
            SendHandshakeToHost();
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t param)
        {
            if (!_currentLobbyId.IsValid() || param.m_ulSteamIDLobby != _currentLobbyId.m_SteamID) return;
            uint change = param.m_rgfChatMemberStateChange;
            uint leftFlags = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft |
                                    EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                                    EChatMemberStateChange.k_EChatMemberStateChangeKicked |
                                    EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if ((change & leftFlags) == 0) return;

            var leftSteamId = new CSteamID(param.m_ulSteamIDUserChanged);
            byte disconnectedPlayerId = 0;
            foreach (var kv in _playerToSteamId)
            {
                if (kv.Value == leftSteamId)
                {
                    disconnectedPlayerId = kv.Key;
                    break;
                }
            }

            if (disconnectedPlayerId != 0)
            {
                _playerToSteamId.Remove(disconnectedPlayerId);
                _peers.RemoveAll(p => p.PlayerId == disconnectedPlayerId);
                ModLogger.Info($"[SteamP2PTransport] Steam lobby member left: SteamID={leftSteamId.m_SteamID} -> PlayerId={disconnectedPlayerId}");
                OnPeerDisconnected?.Invoke(disconnectedPlayerId);
            }
        }

        private void SendHandshakeToHost()
        {
            SendToSteamUser(_hostSteamId, SendChannel.ReliableOrdered, PacketType.HandshakeRequest, w =>
            {
                w.Write(SteamFriends.GetPersonaName() ?? "Client");
                w.Write("0.1.0");
            });
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t param)
        {
            ModLogger.Info($"[SteamP2PTransport] Accepting P2P session from {param.m_steamIDRemote.m_SteamID}");
            SteamNetworking.AcceptP2PSessionWithUser(param.m_steamIDRemote);
        }

        private void OnP2PConnectFail(P2PSessionConnectFail_t param)
        {
            ModLogger.Warn($"[SteamP2PTransport] P2P session connect fail with {param.m_steamIDRemote.m_SteamID}: Error={param.m_eP2PSessionError}");
            foreach (var kv in _playerToSteamId)
            {
                if (kv.Value == param.m_steamIDRemote)
                {
                    OnPeerDisconnected?.Invoke(kv.Key);
                    break;
                }
            }
        }

        public void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            if (_playerToSteamId.TryGetValue(targetPlayerId, out var remoteSteamId))
            {
                SendToSteamUser(remoteSteamId, channel, type, writePayload);
            }
        }

        public void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            if (IsHost)
            {
                foreach (var kv in _playerToSteamId)
                {
                    if (kv.Key != LocalPlayerId)
                    {
                        SendToSteamUser(kv.Value, channel, type, writePayload);
                    }
                }
            }
            else if (_hostSteamId.IsValid())
            {
                SendToSteamUser(_hostSteamId, channel, type, writePayload);
            }
        }

        private void SendToSteamUser(CSteamID targetSteamId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            byte[] frame = LocalTcpLoopbackTransport.BuildFrame(LocalPlayerId, type, writePayload);
            var sendType = channel == SendChannel.ReliableOrdered
                ? EP2PSend.k_EP2PSendReliable
                : EP2PSend.k_EP2PSendUnreliableNoDelay;

            SteamNetworking.SendP2PPacket(targetSteamId, frame, (uint)frame.Length, sendType, (int)channel);
        }

        public void PollEvents()
        {
            if (!IsActive) return;

            try
            {
                SteamAPI.RunCallbacks();

                for (int ch = 0; ch <= 1; ch++)
                {
                    while (SteamNetworking.IsP2PPacketAvailable(out uint msgSize, ch))
                    {
                        byte[] buf = new byte[msgSize];
                        if (SteamNetworking.ReadP2PPacket(buf, msgSize, out uint bytesRead, out CSteamID remoteId, ch) && bytesRead >= 8)
                        {
                            if (buf[0] != 0x54 || buf[1] != 0x42) continue;

                            var pktType = (PacketType)buf[2];
                            byte senderId = buf[3];
                            int payloadLen = BitConverter.ToInt32(buf, 4);
                            if (8 + payloadLen > bytesRead) continue;

                            using (var ms = new MemoryStream(buf, 8, payloadLen, false))
                            using (var reader = new BinaryReader(ms))
                            {
                                HandleSteamPacket(remoteId, senderId, pktType, reader);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[SteamP2PTransport] Exception in PollEvents", ex);
            }
        }

        private void HandleSteamPacket(CSteamID remoteSteamId, byte senderId, PacketType pktType, BinaryReader reader)
        {
            if (IsHost && pktType == PacketType.HandshakeRequest)
            {
                string clientName = reader.ReadString();
                string ver = reader.ReadString();
                byte assignedId = _nextPlayerId++;
                _playerToSteamId[assignedId] = remoteSteamId;

                var peer = new PeerInfo
                {
                    PlayerId = assignedId,
                    PlayerName = clientName,
                    SteamId64 = remoteSteamId.m_SteamID,
                    IsReady = false
                };
                _peers.RemoveAll(p => p.PlayerId == assignedId);
                _peers.Add(peer);

                SendToPeer(assignedId, SendChannel.ReliableOrdered, PacketType.HandshakeAccept, w =>
                {
                    w.Write(assignedId);
                    w.Write((byte)_peers.Count);
                    foreach (var p in _peers)
                    {
                        w.Write(p.PlayerId);
                        w.Write(p.PlayerName);
                        w.Write(p.SteamId64);
                        w.Write(p.IsReady);
                    }
                });

                ModLogger.Info($"[SteamP2PTransport] Accepted Steam peer {clientName} ({remoteSteamId.m_SteamID}) -> PlayerId={assignedId}");
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
                    var p = new PeerInfo
                    {
                        PlayerId = reader.ReadByte(),
                        PlayerName = reader.ReadString(),
                        SteamId64 = reader.ReadUInt64(),
                        IsReady = reader.ReadBoolean()
                    };
                    _peers.Add(p);
                    if (p.SteamId64 != 0)
                        _playerToSteamId[p.PlayerId] = new CSteamID(p.SteamId64);
                }
                ModLogger.Info($"[SteamP2PTransport] Joined Host Lobby! Assigned LocalPlayerId={LocalPlayerId}");
                OnPeerConnected?.Invoke(_peers.Find(p => p.PlayerId == LocalPlayerId));
                return;
            }

            OnPacketReceived?.Invoke(senderId, pktType, reader);
        }

        public void SyncPeers(IEnumerable<PeerInfo> peers)
        {
            _peers.Clear();
            if (peers == null) return;
            foreach (var p in peers)
            {
                if (p == null) continue;
                _peers.Add(new PeerInfo
                {
                    PlayerId = p.PlayerId,
                    PlayerName = p.PlayerName ?? string.Empty,
                    SteamId64 = p.SteamId64,
                    IsReady = p.IsReady
                });
                if (p.SteamId64 != 0)
                    _playerToSteamId[p.PlayerId] = new CSteamID(p.SteamId64);
            }
        }

        public void Dispose()
        {
            IsActive = false;
            if (_currentLobbyId.IsValid())
            {
                try { SteamMatchmaking.LeaveLobby(_currentLobbyId); } catch { }
                _currentLobbyId = CSteamID.Nil;
            }
            _peers.Clear();
            _playerToSteamId.Clear();
        }
    }
}
