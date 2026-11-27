using System;
using System.Drawing;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Renders a lightweight non-interactive performance overlay at the top-left of the in-game
    /// screen showing MSPT (milliseconds per tick), TPS (ticks per second), and Latency to Host.
    /// Attached directly to DXIsometricGridObject.Current in AreaInPixels mode and guarded by
    /// EnterVisualVfxContext so it never perturbs deterministic PRNG or GUID streams.
    /// </summary>
    public static class PerfOverlayRenderer
    {
        private const int ZOverlay = 2147483575;
        private static readonly Color BgColor = Color.FromArgb(196, 12, 18, 24);
        private static readonly Color BorderColor = Color.FromArgb(165, 86, 108, 124);

        private static DXObject _attachedParent;
        private static float _lastUnit = -1f;
        private static double _lastRefreshTicks = -1000.0;

        private static DXTextObject _bgBox;
        private static DXTextObject _msptText;
        private static DXTextObject _tpsText;
        private static DXTextObject _pingText;
        private static DXTextObject _bottleneckText;
        private static bool _bgExpandedForBottleneck;

        public static void Reset()
        {
            _lastRefreshTicks = -1000.0;
            DestroyUi();
        }

        public static void Tick()
        {
            bool shouldShow = GameBridge.CurrentLevelState != null
                && !LockstepClock.LoadingLevel
                && !GameBridge.IsLevelEnded()
                && (CoopSessionManager.IsInSyncedLevel || LockstepClock.IsDeterministicModeActive);

            if (!shouldShow)
            {
                if (_attachedParent != null) DestroyUi();
                return;
            }

            var grid = DXIsometricGridObject.Current;
            if (grid == null)
            {
                if (_attachedParent != null) DestroyUi();
                return;
            }

            double now = DXHelper_Environment.Ticks;
            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            bool needRebuild = !object.ReferenceEquals(_attachedParent, grid) || Math.Abs(_lastUnit - u) > 0.02f;
            if (!needRebuild && now - _lastRefreshTicks < 500.0 && now >= _lastRefreshTicks)
                return;
            _lastRefreshTicks = now;

            LockstepClock.EnterVisualVfxContext();
            try
            {
                if (needRebuild)
                {
                    DestroyUi();
                    BuildUi(grid, u);
                }

                DeterministicSandboxPatches.GetPerfMetrics(out double mspt, out double tps);

                bool paused = LockstepClock.PausedBySession || (DXGame.Current != null && DXGame.Current.Paused);
                double gameSpeed = DXGame.Current?.GameSpeed ?? 1.0;
                double targetTps = paused ? 0.0 : 30.0 * Math.Max(0.1, gameSpeed);

                Color msptColor = mspt <= 20.0
                    ? Color.LightGreen
                    : (mspt <= 33.3 ? Color.Gold : Color.OrangeRed);

                Color tpsColor;
                if (paused && tps < 0.05)
                {
                    tpsColor = Color.Silver;
                }
                else if (tps >= targetTps * 0.90)
                {
                    tpsColor = Color.LightGreen;
                }
                else if (tps >= targetTps * 0.65)
                {
                    tpsColor = Color.Gold;
                }
                else
                {
                    tpsColor = Color.OrangeRed;
                }

                string pingStr;
                Color pingColor;
                if (!CoopSessionManager.IsSessionActive || CoopSessionManager.Transport == null)
                {
                    pingStr = "主机延迟: 单机";
                    pingColor = Color.LightSteelBlue;
                }
                else if (CoopSessionManager.Transport.IsHost)
                {
                    pingStr = "主机延迟: 0ms(房主)";
                    pingColor = Color.LightGreen;
                }
                else
                {
                    double rttMs = CoopSessionManager.GetLocalEffectiveRttToHostMs();
                    if (rttMs >= 0.0)
                    {
                        pingStr = $"主机延迟: {rttMs:F0}ms";
                        pingColor = rttMs <= 60.0
                            ? Color.LightGreen
                            : (rttMs <= 120.0 ? Color.Gold : Color.OrangeRed);
                    }
                    else
                    {
                        pingStr = "主机延迟: --";
                        pingColor = Color.Silver;
                    }
                }

                string msptStr = $"MSPT: {mspt:F1}ms";
                string tpsStr = paused && tps < 0.05 ? "TPS: 0.0(暂停)" : $"TPS: {tps:F1}";
                string bottleneck = paused
                    ? string.Empty
                    : CoopSessionManager.ClassifyTpsBottleneck(
                        mspt, tps, targetTps, DeterministicSandboxPatches.DiagAvgFrameMs);
                bool hasBottleneck = !string.IsNullOrEmpty(bottleneck);

                if (hasBottleneck != _bgExpandedForBottleneck)
                {
                    _bgExpandedForBottleneck = hasBottleneck;
                    ResizeBgBox(u, hasBottleneck);
                }

                SetDynamicText(_msptText, msptStr, msptColor);
                SetDynamicText(_tpsText, tpsStr, tpsColor);
                SetDynamicText(_pingText, pingStr, pingColor);
                if (_bottleneckText != null)
                {
                    _bottleneckText.Visible = hasBottleneck;
                    if (hasBottleneck)
                    {
                        Color bColor = bottleneck == "算力瓶颈"
                            ? Color.OrangeRed
                            : (bottleneck == "网络瓶颈" ? Color.Orange : Color.Gold);
                        SetDynamicText(_bottleneckText, $"[{bottleneck}]", bColor);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[PerfOverlayRenderer] Tick failed: {ex.Message}");
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }

        private static void ResizeBgBox(float u, bool expanded)
        {
            if (_bgBox == null) return;
            float baseW = (108f + 104f + 142f + 14f) * u;
            float extraW = expanded ? 84f * u : 0f;
            _bgBox.State.Area = new RectangleF(10f * u, 8f * u, baseW + extraW, 24f * u);
            if (_bgBox.Image != null) { try { _bgBox.Image.Dispose(); } catch { } _bgBox.Image = null; }
            _bgBox.Text = expanded ? "  " : " ";
        }

        private static void BuildUi(DXObject grid, float u)
        {
            _attachedParent = grid;
            _lastUnit = u;
            _bgExpandedForBottleneck = false;

            float x = 10f * u;
            float y = 8f * u;
            float h = 24f * u;
            float w1 = 108f * u;
            float w2 = 104f * u;
            float w3 = 142f * u;
            float w4 = 84f * u;
            float totalW = w1 + w2 + w3 + 14f * u;
            float fontSize = Math.Max(8f, 9f * u);

            _bgBox = MakeTextBox(x, y, totalW, h, fontSize, FontStyle.Regular, Color.Transparent,
                ContentAlignment.MiddleLeft, BgColor, BorderColor, 1f, ZOverlay);
            _bgBox.Text = " ";
            grid.AddObject(_bgBox);

            _msptText = MakeTextBox(x + 6f * u, y, w1, h, fontSize, FontStyle.Bold, Color.LightGreen,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f, ZOverlay + 1);
            grid.AddObject(_msptText);

            _tpsText = MakeTextBox(x + 6f * u + w1, y, w2, h, fontSize, FontStyle.Bold, Color.LightGreen,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f, ZOverlay + 1);
            grid.AddObject(_tpsText);

            _pingText = MakeTextBox(x + 6f * u + w1 + w2, y, w3, h, fontSize, FontStyle.Bold, Color.LightGreen,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f, ZOverlay + 1);
            grid.AddObject(_pingText);

            _bottleneckText = MakeTextBox(x + 6f * u + w1 + w2 + w3, y, w4, h, fontSize, FontStyle.Bold, Color.Orange,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f, ZOverlay + 1);
            _bottleneckText.Visible = false;
            grid.AddObject(_bottleneckText);
        }

        private static DXTextObject MakeTextBox(float x, float y, float w, float h, float fontSize, FontStyle style,
            Color fg, ContentAlignment align, Color bg, Color border, float borderW, int z)
        {
            var t = new DXTextObject
            {
                Z = z,
                InheritImageAttributes = false,
                MouseClickInteractionEnabled = false
            };
            t.State.AreaStyle = DXObjectState.TAreaStyle.AreaInPixels;
            t.State.Area = new RectangleF(x, y, w, h);
            t.TextRenderer.Font = new Font("Microsoft YaHei", fontSize, style);
            t.TextRenderer.ColorText = fg;
            t.TextRenderer.TextAlign = align;
            t.TextRenderer.ColorBackground = bg;
            t.TextRenderer.ColorAreaBorder = border;
            t.TextRenderer.AreaBorderWidth = borderW;
            t.TextRenderer.MakeTransparent = bg == Color.Transparent;
            return t;
        }

        private static void SetDynamicText(DXTextObject obj, string text, Color textColor)
        {
            if (obj == null) return;
            string desired = text ?? string.Empty;
            bool colorChanged = obj.TextRenderer.ColorText != textColor;
            bool textChanged = !string.Equals(obj.Text, desired, StringComparison.Ordinal);
            if (!colorChanged && !textChanged) return;

            obj.TextRenderer.ColorText = textColor;
            if (!textChanged)
            {
                if (obj.Image != null) { try { obj.Image.Dispose(); } catch { } obj.Image = null; }
                obj.Text = desired == " " ? "  " : desired + " ";
            }
            if (obj.Image != null) { try { obj.Image.Dispose(); } catch { } obj.Image = null; }
            obj.Text = desired;
        }

        private static void DestroyUi()
        {
            try { _bgBox?.RemoveFromParent(); } catch { }
            try { _msptText?.RemoveFromParent(); } catch { }
            try { _tpsText?.RemoveFromParent(); } catch { }
            try { _pingText?.RemoveFromParent(); } catch { }
            try { _bottleneckText?.RemoveFromParent(); } catch { }
            _bgBox = null;
            _msptText = null;
            _tpsText = null;
            _pingText = null;
            _bottleneckText = null;
            _bgExpandedForBottleneck = false;
            _attachedParent = null;
        }
    }
}
