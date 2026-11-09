using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Networking;
using TABCoopMod.Sync;
using ZX;
using ZX.GUI;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Renders ally viewport rectangles on OMiniMap and handles Tactical Pings (Alt + Left Click).
    /// </summary>
    public static class AllyOverlayRenderer
    {
        public static Color GetPlayerColor(byte playerId)
        {
            switch (playerId)
            {
                case 1: return Color.DodgerBlue;
                case 2: return Color.SpringGreen;
                case 3: return Color.Orange;
                default: return Color.Cyan;
            }
        }

        public static void TriggerMinimapAndGroundPing(byte playerId, Point cell, string label)
        {
            try
            {
                var levelSys = GameBridge.LevelSystemInstance;
                if (levelSys == null) return;

                Color color = GetPlayerColor(playerId);
                PointF worldPos = DXWorldGrid.PointFromCell_Center(cell);

                // Show ground indicator ring
                AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowGroundIndicator)
                    ?.Invoke(levelSys, new object[] { worldPos, ZXCommandTargetIndicatorColor.Green, 1.5f });

                // Show top banner alert
                GameBridge.ShowTopBanner($"[PING] {label} ({cell.X}, {cell.Y})", color, 2500);
            }
            catch (Exception ex)
            {
                ModLogger.Error("[AllyOverlayRenderer] Error in TriggerMinimapAndGroundPing", ex);
            }
        }
    }
}
