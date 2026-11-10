using System;
using System.Drawing;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;

namespace TABCoopMod.Sync
{
    /// <summary>
    /// Coordinates local pause/resume. Every state change is emitted as a NetCommandType.SetPaused
    /// command through CommandInterceptor.Dispatch, so in a live session it is broadcast and applied
    /// on every peer at the same turn instead of only mutating the local game.
    ///
    /// Resume is delayed by a short countdown (rendered with the top banner) so peers can react. In
    /// deterministic replay and on Clients the resume is applied immediately in turn order; only the
    /// Host owns the countdown, so its expiry moment is the single authoritative resume tick.
    /// </summary>
    public static class PauseSyncController
    {
        public static int CountdownSeconds { get; set; } = 3;
        public static bool CountdownActive { get; private set; }
        public static int SecondsRemaining { get; private set; }

        /// <summary>True while this controller is applying a pause command. Kept for compatibility
        /// with any input-layer producer that must not re-capture its own application.</summary>
        public static bool IsApplying { get; private set; }

        private static DateTime _resumeAtUtc;
        private static int _lastShown = -1;

        private static bool InNetworkSession =>
            CoopSessionManager.IsSessionActive && CoopSessionManager.IsInSyncedLevel;

        /// <summary>Local human intent: pause now, or resume after the countdown.</summary>
        public static void RequestPause(bool paused)
        {
            if (!paused)
            {
                if (CountdownActive) return; // already counting down

                // Deterministic replay has no wall-clock peers to wait for.
                if (LockstepClock.IsDeterministicModeActive && !CoopSessionManager.IsSessionActive)
                {
                    Apply(false);
                    return;
                }

                // Only the Host runs the authoritative countdown; a Client's resume request is
                // emitted immediately and ordered by the Host's turn broadcast.
                if (InNetworkSession && CoopSessionManager.Transport != null && !CoopSessionManager.Transport.IsHost)
                {
                    Apply(false);
                    return;
                }
            }
            else
            {
                CancelCountdown();
                Apply(true);
                return;
            }

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
                CommandInterceptor.Dispatch(new NetCommand
                {
                    Type = NetCommandType.SetPaused,
                    PlayerId = CommandInterceptor.LocalPlayerId,
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
