using System;
using Steamworks;
using TABCoopMod.Core;
using TABCoopMod.Networking;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Native ZXWindow-based Co-op Multiplayer Lobby Dialog.
    /// Uses the game's built-in steampunk list selector (#=zzi2cl4h1HYA5cX8aaw==.#=zYjeCpah9Jpv2S1i8lQ==).
    /// </summary>
    public static class CoopLobbyWindow
    {
        public static void ShowLobbyWindow()
        {
            try
            {
                string steamStatus = "离线 / 本地回环";
                try
                {
                    var steamId = SteamUser.GetSteamID();
                    string persona = SteamFriends.GetPersonaName();
                    steamStatus = $"{persona} ({steamId})";
                }
                catch
                {
                }

                string sessionInfo = CoopSessionManager.IsSessionActive
                    ? $"联机中（{(CoopSessionManager.Transport.IsHost ? "房主" : "客机")}，已连接 {CoopSessionManager.Transport.ConnectedPeers.Count} 人）"
                    : "未加入房间";

                string title = "多人合作大厅（执政官模式）";
                string text = $"玩家: {steamStatus}  |  状态: {sessionInfo}\n请选择下方操作后点击“好的”:";

                string[] options = new[]
                {
                    "1. [Steam 联机] 创建好友合作房间（2-3 人）",
                    "2. [Steam 联机] 邀请 Steam 好友加入当前房间（Shift+Tab）",
                    "3. [本地回环] 启动房主监听 127.0.0.1:27099（单机双开）",
                    "4. [本地回环] 加入本地房主 127.0.0.1:27099",
                    "5. [会话管理] 断开连接 / 退出合作会话"
                };

                GameBridge.ShowListSelectorMessageBox(title, text, options, OnOptionSelected, () => { });
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopLobbyWindow] Failed to display lobby window", ex);
            }
        }

        private static void OnOptionSelected(int index)
        {
            try
            {
                switch (index)
                {
                    case 0:
                        CoopSessionManager.StartHostSession(useLoopback: false);
                        GameBridge.ShowAlertMessageBox("Steam 合作房主已启动",
                            "已创建 Steam 仅好友可见合作大厅！\n\n" +
                            "1. 按 Shift+Tab（或重新打开合作大厅选择第 2 项）邀请好友。\n" +
                            "2. 随后正常开始新生存游戏或读取已有存档。\n" +
                            "3. 地图加载完成后会自动将当前存档（.zxsav）同步给所有好友！");
                        break;

                    case 1:
                        if (CoopSessionManager.Transport is SteamP2PTransport steamTrans)
                        {
                            steamTrans.OpenSteamInviteOverlay();
                        }
                        else
                        {
                            GameBridge.ShowAlertMessageBox("邀请好友", "请先选择第 1 项创建 Steam P2P 合作房间。");
                        }
                        break;

                    case 2:
                        CoopSessionManager.StartHostSession(useLoopback: true);
                        GameBridge.ShowAlertMessageBox("本地回环房主已就绪",
                            "正在监听 127.0.0.1:27099！\n请正常开局或读档，并打开第二个游戏窗口选择第 4 项加入。");
                        break;

                    case 3:
                        CoopSessionManager.JoinSession(useLoopback: true, "127.0.0.1:27099");
                        GameBridge.ShowAlertMessageBox("正在加入本地房主",
                            "已连接至 127.0.0.1:27099！\n正在等待房主进入地图或同步地图快照...");
                        break;

                    case 4:
                        CoopSessionManager.StopSession();
                        GameBridge.ShowAlertMessageBox("已断开联机", "多人合作会话已关闭。");
                        break;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CoopLobbyWindow] Error executing lobby option {index}", ex);
            }
        }
    }
}
