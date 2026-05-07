using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Extensions;
using YARG.Core.IO;

namespace YARG.Core.Song
{
    /// <summary>
    /// Per-(<see cref="Instrument"/>, <see cref="Difficulty"/>) star ratings produced by the Ghpp
    /// difficulty calculator, populated during scan and persisted in the song cache.
    /// Missing keys mean no rating was computed (e.g. instrument absent or unsupported).
    /// </summary>
    [Serializable]
    public sealed class StarRatings
    {
        // Layout: high byte = Instrument, low byte = Difficulty.
        private readonly Dictionary<int, float> _ratings = new();

        public int Count => _ratings.Count;

        public bool TryGet(Instrument instrument, Difficulty difficulty, out float rating)
        {
            return _ratings.TryGetValue(MakeKey(instrument, difficulty), out rating);
        }

        public float? Get(Instrument instrument, Difficulty difficulty)
        {
            return _ratings.TryGetValue(MakeKey(instrument, difficulty), out var r) ? r : (float?) null;
        }

        public void Set(Instrument instrument, Difficulty difficulty, float rating)
        {
            _ratings[MakeKey(instrument, difficulty)] = rating;
        }

        /// <summary>
        /// Highest star rating across all stored difficulties for the instrument, or null if none.
        /// </summary>
        public float? GetMax(Instrument instrument)
        {
            float? best = null;
            foreach (var kv in _ratings)
            {
                UnpackKey(kv.Key, out var i, out var _);
                if (i != instrument) continue;
                if (!best.HasValue || kv.Value > best.Value) best = kv.Value;
            }
            return best;
        }

        public IEnumerable<(Instrument Instrument, Difficulty Difficulty, float Rating)> Entries
        {
            get
            {
                foreach (var kv in _ratings)
                {
                    UnpackKey(kv.Key, out var instr, out var diff);
                    yield return (instr, diff, kv.Value);
                }
            }
        }

        internal void Serialize(MemoryStream stream)
        {
            stream.Write(_ratings.Count, Endianness.Little);
            foreach (var kv in _ratings)
            {
                stream.Write(kv.Key, Endianness.Little);
                stream.Write(kv.Value, Endianness.Little);
            }
        }

        internal static StarRatings Deserialize(ref FixedArrayStream stream)
        {
            var ratings = new StarRatings();
            int count = stream.Read<int>(Endianness.Little);
            for (int i = 0; i < count; i++)
            {
                int key = stream.Read<int>(Endianness.Little);
                float val = stream.Read<float>(Endianness.Little);
                ratings._ratings[key] = val;
            }
            return ratings;
        }

        private static int MakeKey(Instrument instrument, Difficulty difficulty)
        {
            return ((int) instrument << 8) | (int) difficulty;
        }

        private static void UnpackKey(int key, out Instrument instr, out Difficulty diff)
        {
            instr = (Instrument)(key >> 8);
            diff = (Difficulty)(byte)(key & 0xFF);
        }
    }
}
