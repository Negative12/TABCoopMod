using System;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Decides whether the next simulation tick may advance. In single-process deterministic
    /// replay this is always ready; network lockstep can block a tick until its inputs arrive.
    /// </summary>
    public interface ITickGate
    {
        bool IsTickReady(int nextSimTick);
    }

    public sealed class AlwaysReadyTickGate : ITickGate
    {
        public bool IsTickReady(int nextSimTick) => true;
    }

    /// <summary>
    /// Manual gate for debugging: ticks stay blocked until explicitly released.
    /// </summary>
    public sealed class ManualTickGate : ITickGate
    {
        private int _releasedThrough = int.MaxValue;

        public void Release(int tick) { _releasedThrough = tick; }
        public void ReleaseAll() { _releasedThrough = int.MaxValue; }
        public bool IsTickReady(int nextSimTick) => nextSimTick <= _releasedThrough;
    }

    /// <summary>
    /// Skeleton for true network lockstep: a tick is ready once the host's turn input for it
    /// has been received. Full wiring (host heartbeat, TargetSimTick buffering) is tracked in
    /// docs/07_TECHNICAL_HARD_PROBLEMS.md and intentionally not wired this round.
    /// </summary>
    public sealed class TurnInputTickGate : ITickGate
    {
        private int _readyThrough = -1;

        public void MarkReady(int tick) { if (tick > _readyThrough) _readyThrough = tick; }
        public bool IsTickReady(int nextSimTick) => nextSimTick <= _readyThrough;
    }
}
