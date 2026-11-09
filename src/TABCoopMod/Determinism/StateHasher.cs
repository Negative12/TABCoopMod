using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DXVision;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// O(N) FNV-1a 64-bit state checksum, order-independent over DXGame.Current.Entities.
    /// v2 adds projectile, production-queue and reservation sub-checksums so combat and economy
    /// divergence is detected, not just unit movement and HP.
    ///
    /// IMPORTANT: the game's ZXEntity / CCommandable / ZXLevelState classes are internal, so this
    /// hasher only touches the public DXEntity surface directly and reads ZX-specific data through
    /// reflection helpers on GameBridge. Never cast an entity to ZXEntity here (TypeAccessException).
    /// </summary>
    public static class StateHasher
    {
        public const int HashVersion = 2;

        private static PropertyInfo[] _reservationProps;
        private static Type _reservationStateType;
        private static bool _reflectionWarned;

        public struct LevelStateSnapshotDigest
        {
            public int SimTick;
            public int GameTimeMs;
            public int Gold, Wood, Stone, Iron, Oil;
            public int EntityCount;
            public long PosChecksum, LifeChecksum, ProjectileChecksum, ProductionChecksum, ReservationChecksum;
            public int ProjectileCount, QueuedOrderCount;
            public ulong FullHash;

            public override string ToString()
            {
                return $"Tick={SimTick}, Time={GameTimeMs}ms, Res=({Gold}G/{Wood}W/{Stone}S/{Iron}I/{Oil}O), Entities={EntityCount}, " +
                       $"Proj={ProjectileCount}, Queued={QueuedOrderCount}, Hash=0x{FullHash:X16}";
            }
        }

        public static LevelStateSnapshotDigest ComputeDigest()
        {
            var digest = new LevelStateSnapshotDigest();
            var game = DXGame.Current;
            object ls = GameBridge.CurrentLevelState;
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
            digest.Gold = GameBridge.GetLevelInt("Gold");
            digest.Wood = GameBridge.GetLevelInt("Wood");
            digest.Stone = GameBridge.GetLevelInt("Stone");
            digest.Iron = GameBridge.GetLevelInt("Iron");
            digest.Oil = GameBridge.GetLevelInt("Oil");

            Mix(HashVersion);
            Mix(digest.SimTick);
            Mix(digest.GameTimeMs);
            Mix(digest.Gold);
            Mix(digest.Wood);
            Mix(digest.Stone);
            Mix(digest.Iron);
            Mix(digest.Oil);

            long reservationChecksum = MixReservations(ls);
            digest.ReservationChecksum = reservationChecksum;

            int entityCount = 0;
            int projectileCount = 0;
            int queuedCount = 0;
            long posChecksum = 0;
            long lifeChecksum = 0;
            long projectileChecksum = 0;
            long productionChecksum = 0;

            if (game.Entities != null)
            {
                foreach (var entity in game.Entities)
                {
                    if (entity == null || entity.Disposed)
                        continue;
                    if ((entity.ID & 0x8000000000000000UL) != 0)
                        continue;

                    entityCount++;
                    long qx = (long)Math.Round(entity.Position.X * 100f);
                    long qy = (long)Math.Round(entity.Position.Y * 100f);

                    string typeName = entity.GetType().Name;
                    bool isProjectile = IsProjectileName(typeName);
                    int lifeValue = GameBridge.GetLifeValue(entity);

                    if (isProjectile)
                    {
                        projectileCount++;
                        projectileChecksum += (long)entity.ID ^ (qx * 92837111L) ^ (qy * 689287499L) ^ (lifeValue * 283923481L);
                        continue;
                    }

                    posChecksum += (long)entity.ID ^ (qx * 73856093L) ^ (qy * 19349663L);
                    lifeChecksum += (long)entity.ID ^ (lifeValue * 83492791L);

                    queuedCount += MixQueue(entity, ref productionChecksum);
                }
            }

            digest.EntityCount = entityCount;
            digest.ProjectileCount = projectileCount;
            digest.QueuedOrderCount = queuedCount;
            digest.PosChecksum = posChecksum;
            digest.LifeChecksum = lifeChecksum;
            digest.ProjectileChecksum = projectileChecksum;
            digest.ProductionChecksum = productionChecksum;

            Mix(entityCount);
            Mix(projectileCount);
            Mix(queuedCount);
            Mix(posChecksum);
            Mix(lifeChecksum);
            Mix(projectileChecksum);
            Mix(productionChecksum);
            Mix(reservationChecksum);

            digest.FullHash = hash;
            return digest;
        }

        private static bool IsProjectileName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return typeName.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Proyectile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int MixQueue(DXEntity entity, ref long productionChecksum)
        {
            IList orders;
            try { orders = GameBridge.GetOrdersQueued(GameBridge.GetCommandableObject(entity)); }
            catch (Exception ex)
            {
                if (!_reflectionWarned) { _reflectionWarned = true; ModLogger.Warn($"[StateHasher] production queue unavailable: {ex.Message}"); }
                return 0;
            }
            if (orders == null || orders.Count == 0) return 0;

            int count = 0;
            for (int slot = 0; slot < orders.Count; slot++)
            {
                object item = orders[slot];
                if (item == null) continue;
                int commandId = GameBridge.GetOrderCommandId(item);
                count++;
                // Keyed by owner entity ID + slot + command id; deterministic and order-independent.
                productionChecksum += (long)entity.ID
                    ^ ((long)(slot + 1) * 2654435761L)
                    ^ ((long)commandId * 2246822519L);
            }
            return count;
        }

        private static long MixReservations(object ls)
        {
            Type stateType = GameBridge.LevelStateType;
            if (stateType == null) return 0;

            if (_reservationProps == null || _reservationStateType != stateType)
            {
                var list = new List<PropertyInfo>();
                foreach (var p in stateType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.PropertyType != typeof(int)) continue;
                    string n = p.Name;
                    if (n.IndexOf("Reserv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Worker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("House", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Food", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Energy", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        list.Add(p);
                    }
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                _reservationProps = list.ToArray();
                _reservationStateType = stateType;
            }

            long sum = 0;
            for (int i = 0; i < _reservationProps.Length; i++)
            {
                try
                {
                    int v = Convert.ToInt32(_reservationProps[i].GetValue(ls));
                    sum += ((long)(i + 1) * 1000003L) ^ v;
                }
                catch { }
            }
            return sum;
        }
    }
}
