using System;
using System.Drawing;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;

namespace TABCoopMod.Sync
{
    /// <summary>
    /// Coordinates local pause/resume. Pausing applies immediately; resuming runs a short
    /// countdown (rendered with the existing top banner) before the simulation actually resumes,
    /// so every peer has time to react. In deterministic replay it applies immediately (no countdown).
    /// </summary>
    public static class PauseSyncController
    {
        public static int CountdownSeconds { get; set; } = 3;
        public static bool CountdownActive { get; private set; }
        public static int SecondsRemaining { get; private set; }

        /// <summary>True while this controller is applying a pause command, so the DXGame.Paused
        /// setter patch does not re-intercept our own application.</summary>
        public static bool IsApplying { get; private set; }

        private static DateTime _resumeAtUtc;
        private static int _lastShown = -1;

        /// <summary>Local human intent: pause now, or resume after the countdown.</summary>
        public static void RequestPause(bool paused)
        {
            if (LockstepClock.IsDeterministicModeActive)
            {
                Apply(paused);
                return;
            }

            if (paused)
            {
                CancelCountdown();
                Apply(true);
                return;
            }

            if (CountdownActive) return; // already counting down
            CountdownActive = true;
            SecondsRemaining = Math.Max(1, CountdownSeconds);
            _resumeAtUtc = DateTime.UtcNow.AddSeconds(SecondsRemaining);
            _lastShown = -1;
            Show(SecondsRemaining);
            Apply(true); // keep paused during the countdown
        }

        /// <summary>Called every render frame.</summary>
        public static void Tick()
        {
            if (!CountdownActive) return;

            double remain = (_resumeAtUtc - DateTime.UtcNow).TotalSeconds;
            if (remain <= 0.0)
            {
                CountdownActive = false;
                SecondsRemaining = 0;
                Apply(false);
                GameBridge.ShowTopBanner("Resumed", Color.Gold, 900);
                return;
            }

            int n = (int)Math.Ceiling(remain);
            SecondsRemaining = n;
            if (n != _lastShown) Show(n);
        }

        public static void CancelCountdown()
        {
            CountdownActive = false;
            SecondsRemaining = 0;
            _lastShown = -1;
        }

        private static void Show(int n)
        {
            _lastShown = n;
            GameBridge.ShowTopBanner($"Resume in {n}...", Color.Yellow, 1100);
        }

        private static void Apply(bool paused)
        {
            bool prev = IsApplying;
            IsApplying = true;
            try
            {
                CommandExecutor.Execute(new NetCommand
                {
                    Type = NetCommandType.SetPaused,
                    PlayerId = 1,
                    TargetPausedState = paused
                });
            }
            finally
            {
                IsApplying = prev;
            }
        }
    }
}
