using System.Collections.Generic;
using System.IO;
using YARG.Core.Extensions;
using YARG.Core.IO;
using YARG.Core.Replays;

namespace YARG.Core.Engine.Drums
{
    public class DrumsStats : BaseStats
    {
        /// <summary>
        /// Number of overhits which have occurred.
        /// </summary>
        public int Overhits;

        /// <summary>
        /// Number of overhits which have occurred for each drum action.
        /// </summary>
        public readonly Dictionary<int, int> OverhitsByAction = new();

        /// <summary>
        /// Number of ghosts the player hit with correct dynamics.
        /// </summary>
        public int GhostsHit;

        /// <summary>
        /// Total number of ghost notes in the chart.
        /// </summary>
        public int TotalGhosts;

        /// <summary>
        /// Number of accents the player hit with correct dynamics.
        /// </summary>
        public int AccentsHit;

        /// <summary>
        /// Total number of accent notes in the chart.
        /// </summary>
        public int TotalAccents;

        /// <summary>
        /// Amount of points earned from hitting notes with correct dynamics.
        /// </summary>
        public int DynamicsBonus;

        public DrumsStats()
        {
        }

        public DrumsStats(DrumsStats stats) : base(stats)
        {
            Overhits = stats.Overhits;
            foreach (var (action, count) in stats.OverhitsByAction)
            {
                OverhitsByAction[action] = count;
            }

            GhostsHit = stats.GhostsHit;
            TotalGhosts = stats.TotalGhosts;
            AccentsHit = stats.AccentsHit;
            TotalAccents = stats.TotalAccents;
            DynamicsBonus = stats.DynamicsBonus;
        }

        public DrumsStats(ref FixedArrayStream stream, int version)
            : base(ref stream, version)
        {
            Overhits = stream.Read<int>(Endianness.Little);
            GhostsHit = stream.Read<int>(Endianness.Little);
            TotalGhosts = stream.Read<int>(Endianness.Little);
            AccentsHit = stream.Read<int>(Endianness.Little);
            TotalAccents = stream.Read<int>(Endianness.Little);
            DynamicsBonus = stream.Read<int>(Endianness.Little);
        }

        // Required because base CopyFrom only handles its own fields. Without this the
        // snapshot's OverhitsByAction is dropped and remote drum per-pad overhits show zeros
        // (RecordOverhit on the mirror is called with no Action, so the live dict stays empty).
        public override void CopyFrom(BaseStats source)
        {
            base.CopyFrom(source);
            if (source is not DrumsStats other) return;

            Overhits      = other.Overhits;
            GhostsHit     = other.GhostsHit;
            TotalGhosts   = other.TotalGhosts;
            AccentsHit    = other.AccentsHit;
            TotalAccents  = other.TotalAccents;
            DynamicsBonus = other.DynamicsBonus;

            OverhitsByAction.Clear();
            foreach (var kv in other.OverhitsByAction)
            {
                OverhitsByAction[kv.Key] = kv.Value;
            }
        }

        public override void Reset()
        {
            base.Reset();
            Overhits = 0;
            OverhitsByAction.Clear();
            GhostsHit = 0;
            // Don't reset TotalGhosts
            // TotalGhosts = 0;

            AccentsHit = 0;
            // Don't reset TotalAccents
            // TotalAccents = 0;

            DynamicsBonus = 0;
        }

        public override void Serialize(BinaryWriter writer)
        {
            base.Serialize(writer);

            writer.Write(Overhits);
            writer.Write(GhostsHit);
            writer.Write(TotalGhosts);
            writer.Write(AccentsHit);
            writer.Write(TotalAccents);
            writer.Write(DynamicsBonus);
        }

        public void RecordOverhit(int? action)
        {
            Overhits++;

            if (!action.HasValue)
            {
                return;
            }

            OverhitsByAction[action.Value] = OverhitsByAction.GetValueOrDefault(action.Value) + 1;
        }

        public override ReplayStats ConstructReplayStats(string name, bool isReplayPlayer)
        {
            return new DrumsReplayStats(name, isReplayPlayer, this);
        }
    }
}