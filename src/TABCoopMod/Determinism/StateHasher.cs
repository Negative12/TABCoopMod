using System;
using DXVision;
using TABCoopMod.Core;
using ZX;
using ZX.Entities;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// High-speed O(N) FNV-1a 64-bit state checksum calculator.
    /// Order-independent over DXGame.Current.Entities so minor list ordering differences
    /// never cause false-positive desyncs, while 0.01-cell position or 1-HP differences are 100% caught.
    /// </summary>
    public static class StateHasher
    {
        public struct LevelStateSnapshotDigest
        {
            public int SimTick;
            public int GameTimeMs;
            public int Gold;
            public int Wood;
            public int Stone;
            public int Iron;
            public int Oil;
            public int EntityCount;
            public ulong FullHash;

            public override string ToString()
            {
                return $"Tick={SimTick}, Time={GameTimeMs}ms, Res=({Gold}G/{Wood}W/{Stone}S/{Iron}I/{Oil}O), Entities={EntityCount}, Hash=0x{FullHash:X16}";
            }
        }

        public static LevelStateSnapshotDigest ComputeDigest()
        {
            var digest = new LevelStateSnapshotDigest();
            var game = DXGame.Current;
            var ls = GameBridge.CurrentLevelState;
            if (game == null || ls == null)
                return digest;

            ulong hash = 14695981039346656037UL;
            void Mix(long val)
            {
                hash ^= (ulong)val;
                hash *= 1099511628211UL;
            }

            digest.SimTick = game.IAIterationInt;
            digest.GameTimeMs = game.GameTimeInt;
            digest.Gold = ls.Gold;
            digest.Wood = ls.Wood;
            digest.Stone = ls.Stone;
            digest.Iron = ls.Iron;
            digest.Oil = ls.Oil;

            Mix(digest.SimTick);
            Mix(digest.GameTimeMs);
            Mix(digest.Gold);
            Mix(digest.Wood);
            Mix(digest.Stone);
            Mix(digest.Iron);
            Mix(digest.Oil);
            Mix(ls.FoodReserved);
            Mix(ls.EnergyReserved);
            Mix(ls.WorkersReserved);
            Mix(ls.NZombiesDead);

            int entityCount = 0;
            long posChecksum = 0;
            long lifeChecksum = 0;

            if (game.Entities != null)
            {
                foreach (var entity in game.Entities)
                {
                    if (entity == null || entity.Disposed)
                        continue;

                    // Ignore local-only UI objects (high bit set by LockstepClock.NextLocalUiGuid)
                    if ((entity.ID & 0x8000000000000000UL) != 0)
                        continue;

                    entityCount++;
                    long qx = (long)Math.Round(entity.Position.X * 100f);
                    long qy = (long)Math.Round(entity.Position.Y * 100f);
                    posChecksum += (long)entity.ID ^ (qx * 73856093L) ^ (qy * 19349663L);

                    if (entity is ZXEntity zx)
                    {
                        var life = zx.GetLife();
                        if (life != null)
                        {
                            lifeChecksum += (long)entity.ID ^ (life.Life * 83492791L);
                        }
                    }
                }
            }

            digest.EntityCount = entityCount;
            Mix(entityCount);
            Mix(posChecksum);
            Mix(lifeChecksum);

            digest.FullHash = hash;
            return digest;
        }
    }
}
