using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using DXVision;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Reads the DXVision camera transform so world coordinates can be mapped to screenshot pixels:
    ///   px = (worldX - cam.X) / cam.W * imageW
    ///   py = (worldY - cam.Y) / cam.H * imageH
    /// (DXScene.CameraArea sets RootObject scaling = ScreenArea.Size / CameraArea.Size.)
    /// </summary>
    public static class CameraProbe
    {
        public struct Snapshot
        {
            public bool Valid;
            public float X, Y, W, H;
            public int ScreenW, ScreenH;
            public int WindowW, WindowH;
        }

        public static Snapshot Capture()
        {
            var s = new Snapshot();
            try
            {
                var scene = DXScene.Current;
                if (scene == null) return s;

                RectangleF cam = scene.CameraArea;
                Rectangle screen = DXScene.ScreenArea;
                Size window = DXScene.WindowSize;

                s.X = cam.X; s.Y = cam.Y; s.W = cam.Width; s.H = cam.Height;
                s.ScreenW = screen.Width; s.ScreenH = screen.Height;
                s.WindowW = window.Width; s.WindowH = window.Height;
                s.Valid = cam.Width > 0f && cam.Height > 0f && screen.Width > 0 && screen.Height > 0;
            }
            catch
            {
                s.Valid = false;
            }
            return s;
        }

        public static string ToJson(Snapshot c, int imageW, int imageH)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{{\"valid\":{0},\"x\":{1},\"y\":{2},\"w\":{3},\"h\":{4},\"screenW\":{5},\"screenH\":{6},\"windowW\":{7},\"windowH\":{8},\"imageW\":{9},\"imageH\":{10}}}",
                c.Valid ? "true" : "false", c.X, c.Y, c.W, c.H, c.ScreenW, c.ScreenH, c.WindowW, c.WindowH, imageW, imageH);
        }

        public static void WriteJson(string path, int imageW, int imageH)
        {
            try
            {
                File.WriteAllText(path, ToJson(Capture(), imageW, imageH));
            }
            catch (Exception ex)
            {
                Core.ModLogger.Warn($"[CameraProbe] write failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies a camera override from "cx,cy" or "cx,cy,w,h" (world units). Returns false on bad input.
        /// </summary>
        public static bool ApplySpec(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return false;
            string[] parts = spec.Split(',');
            if (parts.Length < 2) return false;

            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float cx)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float cy)) return false;

            var cur = Capture();
            float w = cur.Valid ? cur.W : 64f;
            float h = cur.Valid ? cur.H : 40f;
            if (parts.Length >= 4)
            {
                if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return false;
                if (!float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return false;
            }

            var scene = DXScene.Current;
            if (scene == null || w <= 0f || h <= 0f) return false;

            scene.CameraArea = new RectangleF(cx - w / 2f, cy - h / 2f, w, h);

            // In deterministic replay the per-frame camera pass may not run, so the renderer keeps the
            // initial view even though CameraArea changed. Force one propagation pass.
            try { HarmonyLib.AccessTools.Method(typeof(DXGame), "SetCamera")?.Invoke(DXGame.Current, null); } catch { }

            Core.ModLogger.Info($"[CameraProbe] applied camera override center=({cx},{cy}) size=({w},{h})");
            return true;
        }
    }
}
