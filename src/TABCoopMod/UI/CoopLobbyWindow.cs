using System;
using System.Drawing;
using System.Reflection;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using Steamworks;
using TABCoopMod.Core;
using TABCoopMod.Networking;
using TABCoopMod.Sync;
using ZX;
using ZX.Editor;
using ZX.GUI;

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
                string steamStatus = "Offline / Local Loopback";
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
                    ? $"ACTIVE ({(CoopSessionManager.Transport.IsHost ? "Host" : "Client")}, Peers={CoopSessionManager.Transport.ConnectedPeers.Count})"
                    : "IDLE (Not in a Co-op room)";

                var msg = new ZXWindowMessage
                {
                    Title = "CO-OP MULTIPLAYER LOBBY (ARCHON MODE)",
                    Text = $"Player: {steamStatus}  |  Status: {sessionInfo}\nSelect an action below and click OK:",
                    AnimationType = ZXWindowAnimationType.Blend
                };

                string[] options = new[]
                {
                    "1. [Steam P2P] Host Friends-Only Co-op Room (2-3 Players)",
                    "2. [Steam P2P] Invite Steam Friends to Current Room (Shift+Tab)",
                    "3. [Local Loopback] Start Host on 127.0.0.1:27099 (Single-PC Dual Test)",
                    "4. [Local Loopback] Join Host at 127.0.0.1:27099",
                    "5. [Session] Disconnect / Leave Co-op Session"
                };

                var showListMethod = AccessTools.Method(
                    GameBridge.TypeZXMessageBoxHelper,
                    "#=zYjeCpah9Jpv2S1i8lQ==",
                    new[] { typeof(ZXWindowMessage), typeof(Action<int>), typeof(Action), typeof(string[]) });

                if (showListMethod != null)
                {
                    showListMethod.Invoke(null, new object[]
                    {
                        msg,
                        (Action<int>)OnOptionSelected,
                        (Action)(() => { }),
                        options
                    });
                }
                else
                {
                    GameBridge.ShowAlertMessageBox(msg.Title, msg.Text);
                }
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
                        GameBridge.ShowAlertMessageBox("STEAM CO-OP HOST STARTED",
                            "Steam P2P Lobby created!\n\n" +
                            "1. Press Shift+Tab (or reopen Co-op Lobby -> Option 2) to invite your friends.\n" +
                            "2. Start a New Survival Game or Load a Save Game as normal.\n" +
                            "3. Once your map loads, it will automatically stream (.zxsav) to all connected friends!");
                        break;

                    case 1:
                        if (CoopSessionManager.Transport is SteamP2PTransport steamTrans)
                        {
                            steamTrans.OpenSteamInviteOverlay();
                        }
                        else
                        {
                            GameBridge.ShowAlertMessageBox("INVITE FRIENDS", "Please start a Steam P2P Host Room (Option 1) first.");
                        }
                        break;

                    case 2:
                        CoopSessionManager.StartHostSession(useLoopback: true);
                        GameBridge.ShowAlertMessageBox("LOCAL LOOPBACK HOST READY",
                            "Listening on 127.0.0.1:27099!\nNow start a Survival Game or connect a second instance.");
                        break;

                    case 3:
                        CoopSessionManager.JoinSession(useLoopback: true, "127.0.0.1:27099");
                        GameBridge.ShowAlertMessageBox("JOINING LOCAL HOST",
                            "Connected to 127.0.0.1:27099!\nWaiting for Host to enter or sync map snapshot...");
                        break;

                    case 4:
                        CoopSessionManager.StopSession();
                        GameBridge.ShowAlertMessageBox("DISCONNECTED", "Co-op session closed.");
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
