using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DXVision;
using HarmonyLib;
using SlimDX;
using SlimDX.Direct3D9;
using TABCoopMod.Core;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Saves PNG screenshots of the live render target via DXVision's DXGame.CaptureScreen(),
    /// and supports low-overhead real-time continuous video frame recording via GPU StretchRectangle.
    /// </summary>
    public static class ScreenshotService
    {
        private sealed class PendingShot
        {
            public int DueFrame;
            public string Path;
            public string Tag;
        }

        private sealed class RawVideoFrame
        {
            public int Index;
            public long ElapsedMs;
            public int SimTick;
            public int Width;
            public int Height;
            public byte[] Bgra;
        }

        private static readonly Queue<PendingShot> _pending = new Queue<PendingShot>();
        private static readonly object _lock = new object();
        private static int _frame;
        private static int _seq;
        private static string _dir;

        // Real-time continuous recording state
        private static volatile bool _recording;
        private static string _recordDir;
        private static int _recordW = 1440;
        private static int _recordH = 900;
        private static int _recordMinIntervalMs = 32;
        private static Stopwatch _recordSw;
        private static long _lastRecordCaptureMs = -999;
        private static int _recordFrameSeq;
        private static int _lastRenderCountFrame;
        private static long _lastRenderFpsCheckMs;
        private static float _measuredRenderFps = 30f;
        private static Surface _rtSmall;
        private static Surface _offscreenSmall;
        private static FieldInfo _fiD3DDevice;
        private static readonly ConcurrentQueue<RawVideoFrame> _recordQueue = new ConcurrentQueue<RawVideoFrame>();
        private static readonly ConcurrentQueue<byte[]> _bufferPool = new ConcurrentQueue<byte[]>();
        private static Thread _recordWorker;
        private static readonly List<string> _recordedFrameMeta = new List<string>();

        public static bool Enabled => TestHarnessConfig.AutoShot;

        public static string Directory_
        {
            get
            {
                if (_dir == null)
                {
                    // Prefer the workspace folder supplied by the launcher (TABCOOP_SHOT_DIR), so
                    // screenshots land in the repo for review. Fall back to <game dir>\tabcoop_shots.
                    string configured = TestHarnessConfig.ShotDir;
                    if (!string.IsNullOrEmpty(configured))
                    {
                        _dir = configured;
                    }
                    else
                    {
                        string baseDir = TestHarnessConfig.OutputDirectory;
                        if (string.IsNullOrEmpty(baseDir)) baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        _dir = Path.Combine(baseDir, "tabcoop_shots");
                    }
                }
                return _dir;
            }
        }

        /// <summary>Called once per render frame from ControlServer.Tick.</summary>
        public static void Tick()
        {
            _frame++;
            if (_recording)
            {
                TryCaptureRecordingFrame();
            }

            if (!Enabled) return;

            while (true)
            {
                PendingShot due = null;
                lock (_lock)
                {
                    if (_pending.Count > 0 && _pending.Peek().DueFrame <= _frame)
                        due = _pending.Dequeue();
                }
                if (due == null) break;
                Capture(due.Path, due.Tag);
            }
        }

        public static Dictionary<string, object> StartRecording(string outDir, int width = 1440, int height = 900, int maxFps = 30)
        {
            if (_recording) StopRecording();

            _recordDir = string.IsNullOrEmpty(outDir) ? Path.Combine(Directory_, "rec_" + DateTime.Now.ToString("HHmmss")) : outDir;
            Directory.CreateDirectory(_recordDir);

            _recordW = Math.Max(640, Math.Min(1920, width));
            _recordH = Math.Max(360, Math.Min(1200, height));
            _recordMinIntervalMs = Math.Max(15, 1000 / Math.Max(5, Math.Min(60, maxFps)));
            _recordFrameSeq = 0;
            _lastRecordCaptureMs = -999;
            lock (_recordedFrameMeta) { _recordedFrameMeta.Clear(); }
            while (_recordQueue.TryDequeue(out _)) { }
            while (_bufferPool.TryDequeue(out _)) { }

            _recordSw = Stopwatch.StartNew();
            _lastRenderCountFrame = _frame;
            _lastRenderFpsCheckMs = 0;
            _measuredRenderFps = 30f;
            _recording = true;

            _recordWorker = new Thread(RecordEncoderLoop)
            {
                IsBackground = true,
                Name = "TABCoop.VideoRecorder",
                Priority = ThreadPriority.BelowNormal
            };
            _recordWorker.Start();

            ModLogger.Info($"[VideoRecorder] Started recording to {_recordDir} ({_recordW}x{_recordH} @ max {maxFps}fps)");
            return new Dictionary<string, object>
            {
                ["recording"] = true,
                ["dir"] = _recordDir,
                ["width"] = _recordW,
                ["height"] = _recordH
            };
        }

        public static Dictionary<string, object> StopRecording()
        {
            _recording = false;
            try { _recordWorker?.Join(5000); } catch { }
            _recordWorker = null;

            DisposeRecSurfaces();

            string metaPath = Path.Combine(_recordDir ?? Directory_, "frames_meta.jsonl");
            string concatPath = Path.Combine(_recordDir ?? Directory_, "ffconcat.txt");
            int count = 0;

            lock (_recordedFrameMeta)
            {
                count = _recordedFrameMeta.Count;
                File.WriteAllLines(metaPath, _recordedFrameMeta, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.AppendLine("ffconcat version 1.0");
                for (int i = 0; i < _recordedFrameMeta.Count; i++)
                {
                    // Parse file and elapsedMs from simple pipe format: index|elapsedMs|simTick|renderFps|fileName
                    string[] parts = _recordedFrameMeta[i].Split('|');
                    if (parts.Length < 5) continue;
                    string fname = parts[4];
                    long curMs = long.Parse(parts[1]);
                    long nextMs = (i + 1 < _recordedFrameMeta.Count) ? long.Parse(_recordedFrameMeta[i + 1].Split('|')[1]) : (curMs + 33);
                    double durSec = Math.Max(0.016, Math.Min(0.500, (nextMs - curMs) / 1000.0));
                    sb.AppendLine($"file '{fname}'");
                    sb.AppendLine($"duration {durSec:F4}");
                }
                if (_recordedFrameMeta.Count > 0)
                {
                    string lastFname = _recordedFrameMeta[_recordedFrameMeta.Count - 1].Split('|')[4];
                    sb.AppendLine($"file '{lastFname}'");
                }
                File.WriteAllText(concatPath, sb.ToString(), new UTF8Encoding(false));
            }

            long totalMs = _recordSw?.ElapsedMilliseconds ?? 0;
            ModLogger.Info($"[VideoRecorder] Stopped recording: {count} frames in {totalMs}ms -> {concatPath}");
            return new Dictionary<string, object>
            {
                ["recording"] = false,
                ["frames"] = count,
                ["elapsedMs"] = totalMs,
                ["dir"] = _recordDir,
                ["concatPath"] = concatPath,
                ["metaPath"] = metaPath
            };
        }

        private static void DisposeRecSurfaces()
        {
            try { _rtSmall?.Dispose(); } catch { }
            try { _offscreenSmall?.Dispose(); } catch { }
            _rtSmall = null;
            _offscreenSmall = null;
        }

        private static Device GetD3DDevice()
        {
            var renderer = DXGame.Current?.Platform?.GetRenderer();
            if (renderer == null) return null;
            if (_fiD3DDevice == null)
                _fiD3DDevice = AccessTools.Field(renderer.GetType(), "_D3DDevice");
            return _fiD3DDevice?.GetValue(renderer) as Device;
        }

        private static void TryCaptureRecordingFrame()
        {
            try
            {
                long nowMs = _recordSw.ElapsedMilliseconds;
                if (nowMs - _lastRenderFpsCheckMs >= 400)
                {
                    int df = _frame - _lastRenderCountFrame;
                    long dt = nowMs - _lastRenderFpsCheckMs;
                    if (dt > 0) _measuredRenderFps = (df * 1000f) / dt;
                    _lastRenderCountFrame = _frame;
                    _lastRenderFpsCheckMs = nowMs;
                }

                if (nowMs - _lastRecordCaptureMs < _recordMinIntervalMs) return;
                if (_recordQueue.Count > 36) return; // backpressure safety

                Device dev = GetD3DDevice();
                if (dev == null) return;

                Surface backbuffer = dev.GetRenderTarget(0);
                if (backbuffer == null || backbuffer.Disposed) return;
                Format fmt = backbuffer.Description.Format;

                if (_rtSmall == null || _rtSmall.Disposed)
                {
                    DisposeRecSurfaces();
                    _rtSmall = Surface.CreateRenderTarget(dev, _recordW, _recordH, fmt, MultisampleType.None, 0, false);
                    _offscreenSmall = Surface.CreateOffscreenPlain(dev, _recordW, _recordH, fmt, Pool.SystemMemory);
                }

                dev.StretchRectangle(backbuffer, _rtSmall, TextureFilter.Linear);
                dev.GetRenderTargetData(_rtSmall, _offscreenSmall);

                int neededBytes = _recordW * _recordH * 4;
                if (!_bufferPool.TryDequeue(out byte[] buf) || buf.Length != neededBytes)
                    buf = new byte[neededBytes];

                DataRectangle dr = _offscreenSmall.LockRectangle(LockFlags.ReadOnly);
                try
                {
                    IntPtr srcPtr = dr.Data.DataPointer;
                    int pitch = dr.Pitch;
                    int rowBytes = _recordW * 4;
                    if (pitch == rowBytes)
                    {
                        Marshal.Copy(srcPtr, buf, 0, neededBytes);
                    }
                    else
                    {
                        for (int y = 0; y < _recordH; y++)
                        {
                            Marshal.Copy(IntPtr.Add(srcPtr, y * pitch), buf, y * rowBytes, rowBytes);
                        }
                    }
                }
                finally
                {
                    _offscreenSmall.UnlockRectangle();
                }

                _lastRecordCaptureMs = nowMs;
                int idx = ++_recordFrameSeq;
                int tick = DXGame.Current?.IAIterationInt ?? 0;

                _recordQueue.Enqueue(new RawVideoFrame
                {
                    Index = idx,
                    ElapsedMs = nowMs,
                    SimTick = tick,
                    Width = _recordW,
                    Height = _recordH,
                    Bgra = buf
                });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[VideoRecorder] frame capture error: {ex.Message}");
            }
        }

        private static void RecordEncoderLoop()
        {
            ImageCodecInfo jpegCodec = null;
            foreach (var c in ImageCodecInfo.GetImageEncoders())
            {
                if (c.FormatID == ImageFormat.Jpeg.Guid) { jpegCodec = c; break; }
            }
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);

            using (Bitmap reusableBmp = new Bitmap(_recordW, _recordH, PixelFormat.Format32bppRgb))
            {
                while (_recording || !_recordQueue.IsEmpty)
                {
                    if (!_recordQueue.TryDequeue(out RawVideoFrame f))
                    {
                        Thread.Sleep(4);
                        continue;
                    }

                    try
                    {
                        BitmapData bd = reusableBmp.LockBits(
                            new Rectangle(0, 0, f.Width, f.Height),
                            ImageLockMode.WriteOnly,
                            PixelFormat.Format32bppRgb);
                        try
                        {
                            int rowBytes = f.Width * 4;
                            if (bd.Stride == rowBytes)
                            {
                                Marshal.Copy(f.Bgra, 0, bd.Scan0, f.Bgra.Length);
                            }
                            else
                            {
                                for (int y = 0; y < f.Height; y++)
                                    Marshal.Copy(f.Bgra, y * rowBytes, IntPtr.Add(bd.Scan0, y * bd.Stride), rowBytes);
                            }
                        }
                        finally
                        {
                            reusableBmp.UnlockBits(bd);
                        }

                        string fname = $"frame_{f.Index:D5}.jpg";
                        string fpath = Path.Combine(_recordDir, fname);
                        if (jpegCodec != null)
                            reusableBmp.Save(fpath, jpegCodec, encParams);
                        else
                            reusableBmp.Save(fpath, ImageFormat.Jpeg);

                        lock (_recordedFrameMeta)
                        {
                            _recordedFrameMeta.Add($"{f.Index}|{f.ElapsedMs}|{f.SimTick}|{_measuredRenderFps:F1}|{fname}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Warn($"[VideoRecorder] encode error: {ex.Message}");
                    }
                    finally
                    {
                        if (_bufferPool.Count < 48)
                            _bufferPool.Enqueue(f.Bgra);
                    }
                }
            }
        }

        /// <summary>Schedules a screenshot a few frames after the current one; returns its path immediately.</summary>
        public static string RequestAfterFrames(string tag, int frames = 2)
        {
            if (!Enabled) return null;

            int seq;
            lock (_lock) { seq = ++_seq; }
            string path = BuildPath(seq, tag);

            lock (_lock)
            {
                _pending.Enqueue(new PendingShot
                {
                    DueFrame = _frame + Math.Max(1, frames),
                    Path = path,
                    Tag = tag
                });
            }
            return path;
        }

        /// <summary>Captures the current render target immediately and returns the saved path.</summary>
        public static string CaptureNow(string tag)
        {
            if (!Enabled) return null;

            int seq;
            lock (_lock) { seq = ++_seq; }
            string path = BuildPath(seq, tag);
            Capture(path, tag);
            return path;
        }

        private static void Capture(string path, string tag)
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;

                using (Bitmap bmp = game.CaptureScreen())
                {
                    if (bmp == null) return;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    bmp.Save(path, ImageFormat.Png);
                }
                ModLogger.Info($"[Screenshot] saved '{tag}' -> {path}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[Screenshot] capture failed for '{tag}': {ex.Message}");
            }
        }

        private static string BuildPath(int seq, string tag)
        {
            int tick = DXGame.Current?.IAIterationInt ?? 0;
            string name = $"{seq:D5}_t{tick}_{DateTime.Now:HHmmss_fff}_{Sanitize(tag)}.png";
            return Path.Combine(Directory_, name);
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "shot";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
