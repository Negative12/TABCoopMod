using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using DXVision;
using DXVision.GUI;
using Steamworks;
using TABCoopMod.Core;
using TABCoopMod.Networking;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Native ZXWindow-based RTS Waiting Room UI for Archon Mode Co-op.
    /// Supports Entry view (Create Steam/Loopback room, auto-probe & join local 127.0.0.1:27099)
    /// and Room view (3-slot Archon roster with player colors & Ready state, Steam friend invite,
    /// Host single-player menu reuse for Load/New Survival/Custom Map, and Client Ready toggle).
    /// </summary>
    public static class CoopLobbyWindow
    {
        private sealed class PlayerSlotRow
        {
            public DXTextObject Background;
            public DXTextObject Swatch;
            public DXTextObject NameLabel;
            public DXTextObject RoleBadge;
        }

        private static readonly Color[] SlotColors =
        {
            Color.DodgerBlue,
            Color.SpringGreen,
            Color.Orange
        };

        private static readonly Color PanelBgColor = Color.FromArgb(205, 14, 19, 24);
        private static readonly Color SlotBgActiveColor = Color.FromArgb(220, 22, 30, 38);
        private static readonly Color SlotBgEmptyColor = Color.FromArgb(150, 14, 18, 22);
        private static readonly Color BorderGoldColor = Color.FromArgb(180, 168, 134, 68);
        private static readonly Color BorderDimColor = Color.FromArgb(130, 72, 86, 98);

        private static DXGUIObject _window;
        private static bool _isClosing;
        private static bool _needsRefresh;
        private static bool _subscribed;

        // Reopen tracking when Host closes waiting room temporarily to use singleplayer menus,
        // or when players return to Main Menu from a match.
        private static bool _reopenAfterSingleplayerMenu;
        private static double _reopenCooldownUntilTicks;
        private static double _noModalSinceTicks = -1.0;

        // Non-intrusive local loopback probe state
        private static LocalProbeResult _lastProbe = new LocalProbeResult { IsOnline = false };
        private static volatile bool _probeInFlight;
        private static double _lastProbeSentTicks = -100000.0;
        private static string _entryStatusMessage = string.Empty;

        // Top identity & shared view containers
        private static DXTextObject _identityLabel;
        private static DXObject _entryView;
        private static DXObject _roomView;

        // Entry View widgets
        private static DXButton _btnCreateSteam;
        private static DXButton _btnCreateLoopback;
        private static DXTextObject _probeCardText;
        private static DXButton _btnJoinLoopback;
        private static DXButton _btnRefreshProbe;
        private static DXTextObject _entryMessageText;

        // Room View widgets
        private static DXTextObject _rosterHeaderText;
        private static readonly PlayerSlotRow[] _slots = new PlayerSlotRow[3];
        private static DXTextObject _roomStatusBanner;
        private static DXButton _btnInviteSteam;
        private static DXButton _btnHostLoadSave;
        private static DXButton _btnHostNewSurvival;
        private static DXButton _btnHostCustomMap;
        private static DXButton _btnDisbandRoom;
        private static DXButton _btnClientReady;
        private static DXTextObject _clientHintText;
        private static DXButton _btnLeaveRoom;

        public static bool IsOpen => _window != null && !_isClosing;

        /// <summary>Executes an equivalent button action for automated UI verification via ControlServer.</summary>
        public static bool InvokeTestAction(string action)
        {
            switch ((action ?? string.Empty).ToLowerInvariant())
            {
                case "host_loopback":
                    CoopSessionManager.StartHostSession(useLoopback: true);
                    RefreshUI();
                    return true;
                case "join_loopback":
                    JoinLocalRoomClicked();
                    return true;
                case "toggle_ready":
                    ClientReadyClicked();
                    return true;
                case "leave":
                    LeaveOrDisbandClicked();
                    return true;
                case "open_load_save":
                    OpenHostSingleplayerMenu("房主正在选择存档...", GameBridge.OpenMainMenuLoadSave, rollSeed: false);
                    return true;
                case "open_new_survival":
                    OpenHostSingleplayerMenu("房主正在配置新生存地图...", GameBridge.OpenMainMenuNewSurvival, rollSeed: true);
                    return true;
                case "open_custom_map":
                    OpenHostSingleplayerMenu("房主正在选择自定义地图...", GameBridge.OpenMainMenuCustomMap, rollSeed: true);
                    return true;
                case "probe_now":
                    TriggerLocalProbeIfDue(force: true);
                    RefreshUI();
                    return true;
                default:
                    return false;
            }
        }

        public static void ShowLobbyWindow()
        {
            try
            {
                EnsureSubscribed();

                if (_window != null && !_isClosing && GameBridge.IsAnyModalOpen())
                {
                    RefreshUI();
                    return;
                }

                _window = null;
                _isClosing = false;
                _reopenAfterSingleplayerMenu = false;
                _noModalSinceTicks = -1.0;
                _entryStatusMessage = string.Empty;

                var win = GameBridge.CreateZXWindow("多人合作等候大厅 — 执政官模式", bigWindow: true);
                if (win == null)
                {
                    ModLogger.Error("[CoopLobbyWindow] CreateZXWindow returned null.");
                    return;
                }

                BuildWindowContents(win);
                _window = win;

                var closeBtn = GameBridge.AddZXWindowBottomButton(win, 2, "关闭窗口", DXKeys.Escape, -120);
                if (closeBtn != null)
                {
                    closeBtn.Activated += _ => CloseLobbyModalOnly(null);
                }

                RefreshUI();
                TriggerLocalProbeIfDue(force: true);

                GameBridge.ShowZXWindow(win, null, animType: 0);
                ModLogger.Info($"[CoopLobbyWindow] Opened RTS Waiting Room (Active={CoopSessionManager.IsSessionActive}, Phase={CoopSessionManager.RoomPhase}).");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopLobbyWindow] Failed to show waiting room", ex);
            }
        }

        /// <summary>Called on Main Menu completion after boot or ReturnToMainMenu.</summary>
        public static void OnMainMenuLoadComplete()
        {
            if (CoopSessionManager.ShouldOpenLobbyOnMainMenu || CoopSessionManager.IsSessionActive)
            {
                bool shouldOpen = CoopSessionManager.ShouldOpenLobbyOnMainMenu;
                CoopSessionManager.ShouldOpenLobbyOnMainMenu = false;
                if (shouldOpen && !GameBridge.IsAnyModalOpen() && !GameBridge.IsGameLoading())
                {
                    ShowLobbyWindow();
                }
            }
        }

        /// <summary>Called from ReturnToMainMenuPrefix so no dangling modal reference remains.</summary>
        public static void ForceHideWithoutStopping()
        {
            _reopenAfterSingleplayerMenu = false;
            _noModalSinceTicks = -1.0;
            if (_window != null && !_isClosing)
            {
                _isClosing = true;
                var w = _window;
                _window = null;
                GameBridge.CloseZXWindow(w, () => { _isClosing = false; });
            }
        }

        /// <summary>Called globally once per frame from CoopSessionManager.OnFrameTick.</summary>
        public static void Tick()
        {
            try
            {
                if (_window != null && !_isClosing)
                {
                    if (!CoopSessionManager.IsSessionActive)
                    {
                        TriggerLocalProbeIfDue(force: false);
                    }
                    if (_needsRefresh)
                    {
                        _needsRefresh = false;
                        RefreshUI();
                    }
                    return;
                }

                if (_reopenAfterSingleplayerMenu)
                {
                    if (GameBridge.IsGameLoading() || GameBridge.CurrentLevelState != null)
                    {
                        _reopenAfterSingleplayerMenu = false;
                        _noModalSinceTicks = -1.0;
                        return;
                    }

                    double now = DXHelper_Environment.Ticks;
                    if (now < _reopenCooldownUntilTicks) return;

                    if (GameBridge.IsAnyModalOpen() || !GameBridge.IsOnMainMenu())
                    {
                        _noModalSinceTicks = -1.0;
                        return;
                    }

                    if (_noModalSinceTicks < 0.0)
                    {
                        _noModalSinceTicks = now;
                        return;
                    }

                    // Wait 350ms with no active modal (filters brief transition gaps between nested menus).
                    if (now - _noModalSinceTicks >= 350.0)
                    {
                        _reopenAfterSingleplayerMenu = false;
                        _noModalSinceTicks = -1.0;
                        if (CoopSessionManager.IsSessionActive && CoopSessionManager.Transport.IsHost)
                        {
                            CoopSessionManager.SetHostRoomPhase(LobbyRoomPhase.InLobby, string.Empty);
                        }
                        ShowLobbyWindow();
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[CoopLobbyWindow] Tick warning: {ex.Message}");
            }
        }

        public static void HandleSteamInviteAccepted(string lobbyId)
        {
            if (string.IsNullOrEmpty(lobbyId)) return;
            try
            {
                ModLogger.Info($"[CoopLobbyWindow] Handling Steam Invite for Lobby {lobbyId}...");
                if (GameBridge.CurrentLevelState != null)
                {
                    CoopSessionManager.StopSession();
                    CoopSessionManager.JoinSession(false, lobbyId);
                    CoopSessionManager.ShouldOpenLobbyOnMainMenu = true;
                    GameBridge.ReturnToMainMenu();
                    return;
                }

                CoopSessionManager.JoinSession(false, lobbyId);
                CoopSessionManager.ShouldOpenLobbyOnMainMenu = true;
                if (GameBridge.IsOnMainMenu())
                {
                    ShowLobbyWindow();
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CoopLobbyWindow] HandleSteamInviteAccepted({lobbyId}) failed", ex);
            }
        }

        private static void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;
            CoopSessionManager.LobbyStateChanged += () => { _needsRefresh = true; };
        }

        private static void CloseLobbyModalOnly(Action onClosed)
        {
            if (_window == null)
            {
                onClosed?.Invoke();
                return;
            }
            if (_isClosing) return;

            _isClosing = true;
            var w = _window;
            _window = null;
            GameBridge.CloseZXWindow(w, () =>
            {
                _isClosing = false;
                onClosed?.Invoke();
            });
        }

        private static void BuildWindowContents(DXGUIObject win)
        {
            var content = GameBridge.GetZXWindowContent(win);
            if (content == null) return;

            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            float cw = content.Width > 50f ? content.Width : 1020f * u;
            float ch = content.Height > 50f ? content.Height : 540f * u;
            ModLogger.Info($"[CoopLobbyWindow] Layout OContent size: {cw:F1}x{ch:F1} (Unit={u:F3})");

            // All children inside OContent use parent-relative coordinates (never AreaInPixels!).
            _identityLabel = CreateTextBox(0f, 0f, cw, 28f * u, 11.5f * u, FontStyle.Bold, Color.Khaki,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            content.AddObject(_identityLabel);

            var divider = CreateSolidBox(0f, 32f * u, cw, Math.Max(1f, 2f * u), BorderGoldColor, Color.Transparent, 0f);
            content.AddObject(divider);

            float bodyY = 44f * u;
            float bodyH = ch - bodyY;
            float gap = 28f * u;
            float leftW = cw * 0.54f;
            float rightX = leftW + gap;
            float rightW = cw - rightX;

            _entryView = new DXObject
            {
                X = 0f,
                Y = bodyY,
                Width = cw,
                Height = bodyH
            };
            content.AddObject(_entryView);

            _roomView = new DXObject
            {
                X = 0f,
                Y = bodyY,
                Width = cw,
                Height = bodyH
            };
            content.AddObject(_roomView);

            BuildEntryView(leftW, rightX, rightW, bodyH, u);
            BuildRoomView(leftW, rightX, rightW, bodyH, u);
        }

        private static void BuildEntryView(float leftW, float rightX, float rightW, float bodyH, float u)
        {
            var leftTitle = CreateTextBox(0f, 0f, leftW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            leftTitle.Text = "创建合作房间（房主）";
            _entryView.AddObject(leftTitle);

            float padLeft = 10f * u;
            _btnCreateSteam = CreateNativeButton("创建 Steam 房间", padLeft, 40f * u, 0, () =>
            {
                _entryStatusMessage = string.Empty;
                try
                {
                    CoopSessionManager.StartHostSession(useLoopback: false);
                    RefreshUI();
                }
                catch (Exception ex)
                {
                    _entryStatusMessage = $"创建 Steam 房间失败: {ex.Message}";
                    CoopSessionManager.StopSession();
                    RefreshUI();
                }
            });
            _entryView.AddObject(_btnCreateSteam);

            var steamDesc = CreateTextBox(padLeft, 40f * u + _btnCreateSteam.Height + 6f * u, leftW - padLeft * 2f, 44f * u,
                10.5f * u, FontStyle.Regular, Color.Silver, ContentAlignment.TopLeft, Color.Transparent, Color.Transparent, 0f);
            steamDesc.Text = "创建仅 Steam 好友可见的 2~3 人合作大厅，\n进入等候房间后可通过 Shift+Tab 邀请好友。";
            _entryView.AddObject(steamDesc);

            float secondBtnY = steamDesc.Y + steamDesc.Height + 18f * u;
            _btnCreateLoopback = CreateNativeButton("创建本地测试房间", padLeft, secondBtnY, 0, () =>
            {
                _entryStatusMessage = string.Empty;
                try
                {
                    CoopSessionManager.StartHostSession(useLoopback: true);
                    RefreshUI();
                }
                catch (Exception ex)
                {
                    _entryStatusMessage = $"端口 27099 启动失败（可能已有房主在运行）: {ex.Message}";
                    CoopSessionManager.StopSession();
                    RefreshUI();
                }
            });
            _entryView.AddObject(_btnCreateLoopback);

            var localDesc = CreateTextBox(padLeft, secondBtnY + _btnCreateLoopback.Height + 6f * u, leftW - padLeft * 2f, 44f * u,
                10.5f * u, FontStyle.Regular, Color.Silver, ContentAlignment.TopLeft, Color.Transparent, Color.Transparent, 0f);
            localDesc.Text = "在 127.0.0.1:27099 开放本地房间，\n适合同机双开 / 多窗口联机调试。";
            _entryView.AddObject(localDesc);

            // Right column: Probe & Join
            var rightTitle = CreateTextBox(rightX, 0f, rightW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            rightTitle.Text = "加入已有房间（客机）";
            _entryView.AddObject(rightTitle);

            _probeCardText = CreateTextBox(rightX, 40f * u, rightW, 96f * u, 10.5f * u, FontStyle.Regular, Color.WhiteSmoke,
                ContentAlignment.MiddleLeft, PanelBgColor, BorderDimColor, 1.5f);
            _entryView.AddObject(_probeCardText);

            float joinBtnY = _probeCardText.Y + _probeCardText.Height + 14f * u;
            _btnJoinLoopback = CreateNativeButton("加入本地房间", rightX, joinBtnY, -30, JoinLocalRoomClicked);
            _entryView.AddObject(_btnJoinLoopback);

            float refreshBtnY = joinBtnY + _btnJoinLoopback.Height + 10f * u;
            _btnRefreshProbe = CreateNativeButton("刷新本地探测", rightX, refreshBtnY, 0, () =>
            {
                _entryStatusMessage = string.Empty;
                TriggerLocalProbeIfDue(force: true);
                RefreshUI();
            }, smallFont: true);
            _entryView.AddObject(_btnRefreshProbe);

            float tipY = refreshBtnY + _btnRefreshProbe.Height + 14f * u;
            var joinTip = CreateTextBox(rightX, tipY, rightW, 74f * u, 10f * u, FontStyle.Regular, Color.LightSteelBlue,
                ContentAlignment.MiddleLeft, PanelBgColor, BorderDimColor, 1f);
            joinTip.Text = "  Steam 好友联机：\n  直接接受好友发送的 Steam 邀请，\n  游戏将自动连接并进入等候房间。";
            _entryView.AddObject(joinTip);

            _entryMessageText = CreateTextBox(0f, bodyH - 32f * u, rightX + rightW, 28f * u,
                10.5f * u, FontStyle.Bold, Color.OrangeRed, ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _entryView.AddObject(_entryMessageText);
        }

        private static void BuildRoomView(float leftW, float rightX, float rightW, float bodyH, float u)
        {
            _rosterHeaderText = CreateTextBox(0f, 0f, leftW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _roomView.AddObject(_rosterHeaderText);

            float slotY = 40f * u;
            float slotH = 62f * u;
            float slotGap = 12f * u;

            for (int i = 0; i < 3; i++)
            {
                float y = slotY + i * (slotH + slotGap);
                var bg = CreateSolidBox(0f, y, leftW, slotH, SlotBgEmptyColor, BorderDimColor, 1.5f);
                _roomView.AddObject(bg);

                // Child coordinates are relative to bg so swatches/labels render in front of slot card bg.
                var swatch = CreateSolidBox(2f * u, 2f * u, 8f * u, slotH - 4f * u, SlotColors[i], Color.Transparent, 0f);
                bg.AddObject(swatch);

                var nameLbl = CreateTextBox(18f * u, 0f, leftW - 145f * u, slotH, 12f * u, FontStyle.Bold, Color.WhiteSmoke,
                    ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
                bg.AddObject(nameLbl);

                var badgeLbl = CreateTextBox(leftW - 125f * u, 0f, 115f * u, slotH, 11f * u, FontStyle.Bold, Color.Gray,
                    ContentAlignment.MiddleRight, Color.Transparent, Color.Transparent, 0f);
                bg.AddObject(badgeLbl);

                _slots[i] = new PlayerSlotRow
                {
                    Background = bg,
                    Swatch = swatch,
                    NameLabel = nameLbl,
                    RoleBadge = badgeLbl
                };
            }

            float bannerY = slotY + 3 * (slotH + slotGap) + 10f * u;
            float bannerH = Math.Min(72f * u, Math.Max(52f * u, bodyH - bannerY - 8f * u));
            _roomStatusBanner = CreateTextBox(0f, bannerY, leftW, bannerH,
                11f * u, FontStyle.Bold, Color.Gold, ContentAlignment.MiddleCenter, PanelBgColor, BorderGoldColor, 1.5f);
            _roomView.AddObject(_roomStatusBanner);

            // Right column: Host & Client actions using native ZXButtonSteam size
            var rightHeader = CreateTextBox(rightX, 0f, rightW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            rightHeader.Text = "房间操作";
            _roomView.AddObject(rightHeader);

            float ry = 40f * u;
            float gapY = 12f * u;

            _btnInviteSteam = CreateNativeButton("邀请 Steam 好友", rightX, ry, 0, InviteSteamClicked);
            _roomView.AddObject(_btnInviteSteam);

            ry += _btnInviteSteam.Height + gapY;
            _btnHostLoadSave = CreateNativeButton("从存档加载...", rightX, ry, -30, () =>
            {
                OpenHostSingleplayerMenu("房主正在选择存档...", GameBridge.OpenMainMenuLoadSave, rollSeed: false);
            });
            _roomView.AddObject(_btnHostLoadSave);

            ry += _btnHostLoadSave.Height + gapY;
            _btnHostNewSurvival = CreateNativeButton("新建生存游戏...", rightX, ry, -30, () =>
            {
                OpenHostSingleplayerMenu("房主正在配置新生存地图...", GameBridge.OpenMainMenuNewSurvival, rollSeed: true);
            });
            _roomView.AddObject(_btnHostNewSurvival);

            ry += _btnHostNewSurvival.Height + gapY;
            _btnHostCustomMap = CreateNativeButton("自定义地图...", rightX, ry, -30, () =>
            {
                OpenHostSingleplayerMenu("房主正在选择自定义地图...", GameBridge.OpenMainMenuCustomMap, rollSeed: true);
            });
            _roomView.AddObject(_btnHostCustomMap);

            float leaveY = bodyH - _btnInviteSteam.Height - 10f * u;
            _btnDisbandRoom = CreateNativeButton("解散房间", rightX, leaveY, -120, LeaveOrDisbandClicked);
            _roomView.AddObject(_btnDisbandRoom);

            // Client controls share the right column when !IsHost
            _btnClientReady = CreateNativeButton("进入准备状态", rightX, 40f * u, -30, ClientReadyClicked);
            _roomView.AddObject(_btnClientReady);

            float hintY = 40f * u + _btnClientReady.Height + 16f * u;
            _clientHintText = CreateTextBox(rightX, hintY, rightW, 130f * u, 10.5f * u, FontStyle.Regular, Color.LightGray,
                ContentAlignment.MiddleLeft, PanelBgColor, BorderDimColor, 1f);
            _roomView.AddObject(_clientHintText);

            _btnLeaveRoom = CreateNativeButton("退出房间", rightX, leaveY, -120, LeaveOrDisbandClicked);
            _roomView.AddObject(_btnLeaveRoom);
        }

        private static void RefreshUI()
        {
            if (_window == null || _isClosing) return;

            bool inSession = CoopSessionManager.IsSessionActive;
            SetDynamicText(_identityLabel, BuildIdentityLine(inSession),
                textColor: inSession ? Color.SpringGreen : Color.Khaki);

            _entryView.Visible = !inSession;
            _roomView.Visible = inSession;

            if (!inSession)
            {
                RefreshEntryView();
            }
            else
            {
                RefreshRoomView();
            }
        }

        private static void RefreshEntryView()
        {
            GameBridge.SetZXButtonSteamText(_btnCreateSteam, "创建 Steam 房间");
            GameBridge.SetZXButtonSteamText(_btnCreateLoopback, "创建本地测试房间");

            var probe = _lastProbe;
            if (probe != null && probe.IsOnline)
            {
                bool joinable = probe.PlayerCount < probe.MaxPlayers;
                string phaseLabel = DescribePhase((LobbyRoomPhase)probe.RoomPhase);
                SetDynamicText(
                    _probeCardText,
                    $"  检测到本地房主 (127.0.0.1:27099)\n" +
                    $"  房主: {probe.HostName}   |   人数: {probe.PlayerCount} / {probe.MaxPlayers}\n" +
                    $"  状态: {phaseLabel}",
                    textColor: joinable ? Color.LightGreen : Color.Orange,
                    borderColor: joinable ? Color.SpringGreen : Color.OrangeRed);
                _btnJoinLoopback.Enabled = joinable;
                GameBridge.SetZXButtonSteamText(_btnJoinLoopback, joinable ? "加入本地房间" : "本地房间已满");
                GameBridge.SetZXButtonSteamHue(_btnJoinLoopback, joinable ? -30 : 0);
            }
            else
            {
                SetDynamicText(
                    _probeCardText,
                    "  未检测到本地房主 (127.0.0.1:27099)\n" +
                    "  请先在另一窗口点击 [创建本地测试房间]，\n" +
                    "  或等待 1.5 秒自动探测。",
                    textColor: Color.Silver,
                    borderColor: BorderDimColor);
                _btnJoinLoopback.Enabled = false;
                GameBridge.SetZXButtonSteamText(_btnJoinLoopback, "暂未检测到主机");
                GameBridge.SetZXButtonSteamHue(_btnJoinLoopback, 0);
            }

            SetDynamicText(_entryMessageText, _entryStatusMessage ?? string.Empty);
        }

        private static void RefreshRoomView()
        {
            bool isHost = CoopSessionManager.Transport.IsHost;
            byte localId = CoopSessionManager.Transport.LocalPlayerId;
            var peers = CoopSessionManager.GetEffectivePeers();

            SetDynamicText(_rosterHeaderText, $"房间成员 ({peers.Count} / 3) — 执政官共控模式");

            for (int i = 0; i < 3; i++)
            {
                byte slotPlayerId = (byte)(i + 1);
                PeerInfo match = null;
                foreach (var p in peers)
                {
                    if (p.PlayerId == slotPlayerId) { match = p; break; }
                }

                var row = _slots[i];
                if (match != null)
                {
                    bool isMe = match.PlayerId == localId;
                    bool isSlotHost = match.PlayerId == 1;
                    SetSolidBoxColors(row.Background, SlotBgActiveColor, isMe ? SlotColors[i] : BorderGoldColor);
                    SetSolidBoxColors(row.Swatch, SlotColors[i], Color.Transparent);

                    SetDynamicText(row.NameLabel, $"P{slotPlayerId} · {match.PlayerName}{(isMe ? " (我)" : "")}",
                        textColor: Color.WhiteSmoke);

                    if (isSlotHost)
                    {
                        SetDynamicText(row.RoleBadge, "★ 房主", textColor: Color.Gold);
                    }
                    else if (match.IsReady)
                    {
                        SetDynamicText(row.RoleBadge, "✓ 已准备", textColor: Color.SpringGreen);
                    }
                    else
                    {
                        SetDynamicText(row.RoleBadge, "○ 未准备", textColor: Color.Orange);
                    }
                }
                else
                {
                    SetSolidBoxColors(row.Background, SlotBgEmptyColor, BorderDimColor);
                    SetSolidBoxColors(row.Swatch, Color.FromArgb(90, SlotColors[i]), Color.Transparent);

                    SetDynamicText(row.NameLabel, $"P{slotPlayerId} · 空位（等待玩家加入）",
                        textColor: Color.DimGray);
                    SetDynamicText(row.RoleBadge, "空位", textColor: Color.DimGray);
                }
            }

            UpdateRoomBanner(isHost, peers.Count);

            _btnInviteSteam.Visible = isHost;
            _btnHostLoadSave.Visible = isHost;
            _btnHostNewSurvival.Visible = isHost;
            _btnHostCustomMap.Visible = isHost;
            _btnDisbandRoom.Visible = isHost;

            _btnClientReady.Visible = !isHost;
            _clientHintText.Visible = !isHost;
            _btnLeaveRoom.Visible = !isHost;

            if (isHost)
            {
                bool isSteam = CoopSessionManager.Transport is SteamP2PTransport;
                bool lobbyValid = CoopSessionManager.Transport is SteamP2PTransport st && st.CurrentLobbyId.IsValid();
                _btnInviteSteam.Enabled = isSteam && lobbyValid;
                GameBridge.SetZXButtonSteamText(_btnInviteSteam,
                    (isSteam && !lobbyValid) ? "正在创建大厅..." : "邀请 Steam 好友");

                bool canStart = CoopSessionManager.CanHostStartGame;
                bool hasSaves = GameBridge.HasAnySaveGames();

                _btnHostLoadSave.Enabled = canStart && hasSaves;
                _btnHostNewSurvival.Enabled = canStart;
                _btnHostCustomMap.Enabled = canStart;

                GameBridge.SetZXButtonSteamHue(_btnHostLoadSave, (canStart && hasSaves) ? -30 : 0);
                GameBridge.SetZXButtonSteamHue(_btnHostNewSurvival, canStart ? -30 : 0);
                GameBridge.SetZXButtonSteamHue(_btnHostCustomMap, canStart ? -30 : 0);
            }
            else
            {
                var localPeer = CoopSessionManager.LocalPeer;
                bool ready = localPeer != null && localPeer.IsReady;
                bool joined = CoopSessionManager.Transport.LocalPlayerId != 0;
                _btnClientReady.Enabled = joined;
                GameBridge.SetZXButtonSteamText(_btnClientReady,
                    !joined ? "正在连接房主..." : (ready ? "取消准备" : "进入准备状态"));
                GameBridge.SetZXButtonSteamHue(_btnClientReady, ready ? 0 : -30);

                SetDynamicText(_clientHintText, CoopSessionManager.RoomPhase == LobbyRoomPhase.InGame
                    ? "  房主当前正在对局中，\n  等待房主返回大厅或同步重连。"
                    : "  点击上方按钮切换准备状态。\n  全员准备后房主即可选图或读档，\n  加载完毕后将自动同步并开始。");
            }
        }

        private static void UpdateRoomBanner(bool isHost, int peerCount)
        {
            if (isHost)
            {
                if (!CoopSessionManager.HasJoinedClient)
                {
                    SetDynamicText(_roomStatusBanner,
                        "等待至少 1 名客机加入房间后，方可开启或读取游戏",
                        textColor: Color.Orange, borderColor: Color.Orange);
                }
                else if (!CoopSessionManager.AreAllClientsReady)
                {
                    SetDynamicText(_roomStatusBanner,
                        $"已有 {peerCount} 名玩家在房间，等待所有客机进入准备状态...",
                        textColor: Color.Gold, borderColor: Color.Goldenrod);
                }
                else
                {
                    SetDynamicText(_roomStatusBanner,
                        "全员已准备就绪！请在右侧选择 [从存档加载] 或 [新建游戏]",
                        textColor: Color.SpringGreen, borderColor: Color.SpringGreen);
                }
                return;
            }

            if (CoopSessionManager.Transport.LocalPlayerId == 0)
            {
                SetDynamicText(_roomStatusBanner,
                    "正在与房主建立联机握手...",
                    textColor: Color.Gold, borderColor: Color.Goldenrod);
                return;
            }

            if (CoopSessionManager.RoomPhase == LobbyRoomPhase.HostSelectingMenu)
            {
                SetDynamicText(_roomStatusBanner,
                    string.IsNullOrEmpty(CoopSessionManager.HostActionHint)
                        ? "房主正在选择地图或存档..."
                        : CoopSessionManager.HostActionHint,
                    textColor: Color.Cyan, borderColor: Color.Cyan);
                return;
            }

            if (CoopSessionManager.RoomPhase == LobbyRoomPhase.HostLoadingLevel)
            {
                SetDynamicText(_roomStatusBanner,
                    "房主正在加载游戏，即将自动接收存档快照并同步进入...",
                    textColor: Color.Cyan, borderColor: Color.Cyan);
                return;
            }

            bool meReady = CoopSessionManager.LocalPeer != null && CoopSessionManager.LocalPeer.IsReady;
            SetDynamicText(_roomStatusBanner,
                meReady ? "您已准备就绪，等待房主开始游戏..." : "请点击右侧 [进入准备状态] 以通知房主开局",
                textColor: meReady ? Color.SpringGreen : Color.Orange,
                borderColor: meReady ? Color.SpringGreen : Color.Orange);
        }

        private static string BuildIdentityLine(bool inSession)
        {
            string persona = "本地玩家";
            string steamTag = "未连接 Steam";
            try
            {
                persona = SteamFriends.GetPersonaName() ?? Environment.UserName ?? "Player";
                var sid = SteamUser.GetSteamID();
                if (sid.IsValid()) steamTag = $"SteamID: {sid.m_SteamID}";
            }
            catch { }

            if (!inSession)
            {
                return $"当前玩家: {persona} ({steamTag})   |   状态: 未加入房间";
            }

            string mode = CoopSessionManager.Transport is LocalTcpLoopbackTransport ? "本地回环 (127.0.0.1:27099)" : "Steam P2P 大厅";
            string role = CoopSessionManager.Transport.IsHost ? "房主 P1" : $"客机 P{CoopSessionManager.Transport.LocalPlayerId}";
            return $"当前玩家: {persona}   |   角色: {role}   |   网络: {mode}";
        }

        private static void OpenHostSingleplayerMenu(string hostHint, Func<bool> openMenuAction, bool rollSeed)
        {
            if (!CoopSessionManager.CanHostStartGame) return;

            if (rollSeed)
            {
                CoopSessionManager.RollNewGameSeed();
            }

            CoopSessionManager.SetHostRoomPhase(LobbyRoomPhase.HostSelectingMenu, hostHint);
            _reopenAfterSingleplayerMenu = true;
            _reopenCooldownUntilTicks = DXHelper_Environment.Ticks + 600.0;
            _noModalSinceTicks = -1.0;

            CloseLobbyModalOnly(() =>
            {
                bool ok = openMenuAction != null && openMenuAction();
                if (!ok)
                {
                    ModLogger.Warn("[CoopLobbyWindow] Singleplayer menu action returned false; restoring waiting room.");
                    _reopenAfterSingleplayerMenu = false;
                    CoopSessionManager.SetHostRoomPhase(LobbyRoomPhase.InLobby, string.Empty);
                    ShowLobbyWindow();
                }
            });
        }

        private static void JoinLocalRoomClicked()
        {
            _entryStatusMessage = string.Empty;
            try
            {
                CoopSessionManager.JoinSession(useLoopback: true, "127.0.0.1");
                RefreshUI();
            }
            catch (Exception ex)
            {
                _entryStatusMessage = $"连接本地房主失败: {ex.Message}";
                CoopSessionManager.StopSession();
                RefreshUI();
            }
        }

        private static void InviteSteamClicked()
        {
            if (CoopSessionManager.Transport is SteamP2PTransport steamTrans && steamTrans.CurrentLobbyId.IsValid())
            {
                steamTrans.OpenSteamInviteOverlay();
            }
        }

        private static void ClientReadyClicked()
        {
            var localPeer = CoopSessionManager.LocalPeer;
            bool curReady = localPeer != null && localPeer.IsReady;
            CoopSessionManager.SetLocalReady(!curReady);
            RefreshUI();
        }

        private static void LeaveOrDisbandClicked()
        {
            _reopenAfterSingleplayerMenu = false;
            _entryStatusMessage = string.Empty;
            CoopSessionManager.StopSession();
            TriggerLocalProbeIfDue(force: true);
            RefreshUI();
        }

        private static void TriggerLocalProbeIfDue(bool force)
        {
            if (_probeInFlight) return;
            double now = DXHelper_Environment.Ticks;
            if (!force && now - _lastProbeSentTicks < 1500.0) return;

            _probeInFlight = true;
            _lastProbeSentTicks = now;

            Task.Run(() =>
            {
                try
                {
                    _lastProbe = LocalTcpLoopbackTransport.ProbeLocalHost("127.0.0.1", LocalTcpLoopbackTransport.DefaultPort, 250);
                }
                catch
                {
                    _lastProbe = new LocalProbeResult { IsOnline = false };
                }
                finally
                {
                    _probeInFlight = false;
                    _needsRefresh = true;
                }
            });
        }

        private static string DescribePhase(LobbyRoomPhase phase)
        {
            switch (phase)
            {
                case LobbyRoomPhase.InLobby: return "等候大厅中";
                case LobbyRoomPhase.HostSelectingMenu: return "房主正在选图";
                case LobbyRoomPhase.HostLoadingLevel: return "正在加载同步关卡";
                case LobbyRoomPhase.InGame: return "游戏中（支持重连/中途加入）";
                default: return "就绪";
            }
        }

        private static DXButton CreateNativeButton(string text, float x, float y, int hueRotation, Action onClick, bool smallFont = false)
        {
            var btn = GameBridge.CreateZXButtonSteam(text, DXKeys.None, hueRotation, smallFont);
            if (btn == null)
            {
                btn = new DXButton();
            }
            btn.X = x;
            btn.Y = y;
            btn.Activated += _ =>
            {
                try
                {
                    onClick?.Invoke();
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[CoopLobbyWindow] Button '{text}' action failed", ex);
                }
            };
            return btn;
        }

        private static void ResetTextSurface(DXTextObject obj)
        {
            if (obj == null || obj.Image == null) return;
            try { obj.Image.Dispose(); } catch { }
            obj.Image = null;
        }

        private static void SetSolidBoxColors(DXTextObject box, Color bgColor, Color borderColor)
        {
            if (box == null) return;
            if (box.TextRenderer.ColorBackground == bgColor &&
                box.TextRenderer.ColorAreaBorder == borderColor)
                return;

            box.TextRenderer.ColorBackground = bgColor;
            box.TextRenderer.MakeTransparent = bgColor == Color.Transparent;
            box.TextRenderer.ColorAreaBorder = borderColor;

            // Reset GPU image so DXTextObject creates a fresh surface instead of ReLoadInGPU().
            ResetTextSurface(box);
            box.Text = "  ";
            ResetTextSurface(box);
            box.Text = " ";
        }

        private static void SetDynamicText(
            DXTextObject obj,
            string text,
            Color? textColor = null,
            Color? borderColor = null)
        {
            if (obj == null) return;
            string desired = text ?? string.Empty;
            bool colorChanged =
                (textColor.HasValue && obj.TextRenderer.ColorText != textColor.Value) ||
                (borderColor.HasValue && obj.TextRenderer.ColorAreaBorder != borderColor.Value);
            bool textChanged = !string.Equals(obj.Text, desired, StringComparison.Ordinal);

            if (!colorChanged && !textChanged) return;

            if (textColor.HasValue) obj.TextRenderer.ColorText = textColor.Value;
            if (borderColor.HasValue) obj.TextRenderer.ColorAreaBorder = borderColor.Value;

            if (!textChanged)
            {
                ResetTextSurface(obj);
                obj.Text = desired == " " ? "  " : desired + " ";
            }

            ResetTextSurface(obj);
            obj.Text = desired;
        }

        private static DXTextObject CreateSolidBox(
            float x, float y, float w, float h,
            Color bgColor, Color borderColor, float borderWidth)
        {
            // Render a single space with transparent text so DXTextObject creates a properly
            // sized GPU surface (1x1 EmptyBitmap can streak when stretched across pixels).
            var obj = CreateTextBox(x, y, w, h, 8f, FontStyle.Regular, Color.Transparent,
                ContentAlignment.MiddleCenter, bgColor, borderColor, borderWidth);
            obj.Text = " ";
            return obj;
        }

        private static DXTextObject CreateTextBox(
            float x, float y, float w, float h,
            float fontSize,
            FontStyle style,
            Color textColor,
            ContentAlignment align,
            Color bgColor,
            Color borderColor,
            float borderWidth)
        {
            var obj = new DXTextObject
            {
                X = x,
                Y = y,
                Width = w,
                Height = h
            };
            obj.TextRenderer.Font = new Font("Microsoft YaHei", Math.Max(8f, fontSize), style);
            obj.TextRenderer.ColorText = textColor;
            obj.TextRenderer.TextAlign = align;
            obj.TextRenderer.ColorBackground = bgColor;
            obj.TextRenderer.ColorAreaBorder = borderColor;
            obj.TextRenderer.AreaBorderWidth = borderWidth;
            obj.TextRenderer.MakeTransparent = bgColor == Color.Transparent;
            return obj;
        }
    }
}
