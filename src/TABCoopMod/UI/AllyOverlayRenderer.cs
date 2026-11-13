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

    /// <summary>
    /// Draws one colored viewport rectangle per ally on the minimap. Hooked on
    /// ZX.GUI.OMiniMap.OnRender (protected override); boxes are lazily created as children of the
    /// minimap and re-positioned each render from each ally's world-space camera area.
    /// </summary>
    [HarmonyPatch]
    public static class AllyMiniMapOverlayPatch
    {
        private static readonly Dictionary<byte, DXObject> _boxes = new Dictionary<byte, DXObject>();
        private static readonly object _boxesSync = new object();

        public static MethodBase TargetMethod()
        {
            var miniMapType = GameBridge.GameAssembly?.GetType("ZX.GUI.OMiniMap");
            return miniMapType == null ? null : AccessTools.Method(miniMapType, "OnRender", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix(DXObject __instance)
        {
            if (__instance == null || !CoopSessionManager.IsSessionActive || !CoopSessionManager.IsInSyncedLevel) return;
            if (DXLevel.Current == null || CoopSessionManager.AllyStates.Count == 0) return;

            RectangleF playable = DXLevel.Current.PlayableArea;
            RectangleF inner = __instance.InnerArea;
            if (playable.Width <= 0f || playable.Height <= 0f || inner.Width <= 0f || inner.Height <= 0f) return;

            lock (_boxesSync)
            {
                foreach (var kv in CoopSessionManager.AllyStates)
                {
                    byte playerId = kv.Key;
                    var state = kv.Value;
                    if (state == null || state.CameraWorldArea.Width <= 0f) continue;

                    // Create a fresh overlay box whenever the minimap instance changes across level reloads;
                    // reusing a DXObject whose Parent points to a disposed old OMiniMap throws in AddObject.
                    if (!_boxes.TryGetValue(playerId, out var box) || box == null || !object.ReferenceEquals(box.Parent, __instance))
                    {
                        box = new DXObject("CoopAllyView" + playerId, DXImage.FromColor(Color.Transparent))
                        {
                            Z = 1001,
                            PixelAligned = true,
                            InheritImageAttributes = false,
                            Border = new DXShaderEffect_Border(
                                AllyOverlayRenderer.GetPlayerColor(playerId),
                                2,
                                DXShaderEffect_Border.TBorderUnit.InPixels,
                                DXShaderEffect_Border.TBorderType.Frame)
                        };
                        _boxes[playerId] = box;
                        __instance.AddObject(box);
                        ModLogger.Info($"[AllyOverlayRenderer] Created minimap viewport box for Player {playerId}");
                    }

                    RectangleF rect = Rectangle.Truncate(DXHelper_Rectangle.ChangeCoordinates(state.CameraWorldArea, playable, inner));
                    box.Area = rect;
                    box.Visible = true;
                }
            }
        }
    }
}
