using System;
using System.Drawing;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Host-only top-center stall indicator.
    /// Shows which Client is slow when the local simulation tick stops advancing for >500ms.
    /// </summary>
    public static class HostWaitStatusOverlay
    {
        private const int Z = 2147483602;
        private static DXTextObject _text;
        private static DXObject _parent;
        private static double _lastRefreshTicks = -1000.0;

        public static void Tick()
        {
            bool visible = CoopSessionManager.HostWaitStatusActive
                && CoopSessionManager.IsSessionActive
                && CoopSessionManager.IsInSyncedLevel
                && CoopSessionManager.Transport != null
                && CoopSessionManager.Transport.IsHost
                && !LockstepClock.LoadingLevel
                && !GameBridge.IsGameLoading()
                && GameBridge.CurrentLevelState != null;

            if (!visible)
            {
                Destroy();
                return;
            }

            var grid = DXIsometricGridObject.Current;
            if (grid == null)
            {
                Destroy();
                return;
            }

            double now = DXHelper_Environment.Ticks;
            if (now - _lastRefreshTicks < 120.0 && now >= _lastRefreshTicks)
                return;
            _lastRefreshTicks = now;

            float sw = DXScene.ScreenArea.Width > 0 ? DXScene.ScreenArea.Width : 1920f;
            if (_text == null || !ReferenceEquals(_parent, grid))
            {
                Destroy();
                Build(grid, sw);
            }

            if (_text != null)
            {
                _text.Visible = true;
                float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
                float w = 400f * u;
                _text.State.Area = new RectangleF((sw - w) * 0.5f, 78f * u, w, 30f * u);
                SetText(CoopSessionManager.HostWaitStatusText);
            }
        }

        public static void Reset()
        {
            Destroy();
            _lastRefreshTicks = -1000.0;
        }

        private static void Build(DXObject grid, float sw)
        {
            LockstepClock.EnterVisualVfxContext();
            try
            {
                _parent = grid;
                _text = new DXTextObject
                {
                    Z = Z,
                    InheritImageAttributes = false
                };
                _text.State.AreaStyle = DXObjectState.TAreaStyle.AreaInPixels;
                _text.TextRenderer.Font = new Font("Microsoft YaHei", 10.5f, FontStyle.Bold);
                _text.TextRenderer.TextAlign = ContentAlignment.MiddleCenter;
                _text.TextRenderer.ColorText = Color.Gold;
                _text.TextRenderer.ColorBackground = Color.FromArgb(235, 62, 36, 10);
                _text.TextRenderer.ColorAreaBorder = Color.Orange;
                _text.TextRenderer.AreaBorderWidth = 1.5f;
                _text.TextRenderer.MakeTransparent = false;
                _text.Text = CoopSessionManager.HostWaitStatusText ?? string.Empty;
                float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
                float w = 400f * u;
                _text.State.Area = new RectangleF((sw - w) * 0.5f, 78f * u, w, 30f * u);
                grid.AddObject(_text);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[HostWaitStatusOverlay] Build failed: {ex.Message}");
                _text = null;
                _parent = null;
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }

        private static void SetText(string text)
        {
            if (_text == null) return;
            string desired = text ?? string.Empty;
            if (string.Equals(_text.Text, desired, StringComparison.Ordinal)) return;
            if (_text.Image != null)
            {
                try { _text.Image.Dispose(); } catch { }
                _text.Image = null;
            }
            _text.Text = desired;
        }

        private static void Destroy()
        {
            if (_text != null)
            {
                try { _text.RemoveFromParent(); } catch { }
                _text = null;
            }
            _parent = null;
        }
    }
}
