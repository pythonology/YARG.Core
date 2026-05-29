using System;
using System.Collections.Generic;
using System.Linq;

namespace YARG.Core.Engine.Prediction
{
    public sealed class EngineRollbackBuffer
    {
        public enum DecisionKind : byte
        {
            PredictedHit  = 1,
            ConfirmedMiss = 2,
            SustainReleased = 3,
            StarPowerActivated = 4,
            // Whammy is tracked through rollback because the whammy timer affects SP
            // accumulation on active sustains -- dropping late samples produces wrong SP
            // scores on the receiver.
            Whammy = 5,
            Overstrum = 6,
            PredictedMiss = 7,
            ConfirmedHit = 8,
        }

        public readonly struct DecisionEvent
        {
            public DecisionEvent(double time, DecisionKind kind, int noteIndex)
            {
                Time = time;
                Kind = kind;
                NoteIndex = noteIndex;
                Value = 0f;
            }

            public DecisionEvent(double time, DecisionKind kind, int noteIndex, float value)
            {
                Time = time;
                Kind = kind;
                NoteIndex = noteIndex;
                Value = value;
            }

            public readonly double       Time;
            public readonly DecisionKind Kind;
            public readonly int          NoteIndex;
            public readonly float        Value;
        }

        private sealed class Entry
        {
            public double         SongTime;
            public EngineSnapshot Snapshot = null!;
            public List<DecisionEvent> Events = new();
        }

        private readonly List<Entry> _entries = new();

        public int Count => _entries.Count;

        /// <summary>Time of the newest snapshot, or NegativeInfinity when empty.</summary>
        public double LatestSongTime =>
            _entries.Count == 0 ? double.NegativeInfinity : _entries[^1].SongTime;

        /// <summary>Snapshots must be added in monotonic time order; out-of-order throws.</summary>
        public void TakeSnapshot(double songTime, EngineSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (_entries.Count > 0 && songTime < _entries[^1].SongTime)
            {
                throw new InvalidOperationException(
                    $"TakeSnapshot must be called in monotonic time order " +
                    $"(latest={_entries[^1].SongTime}, new={songTime}).");
            }

            _entries.Add(new Entry { SongTime = songTime, Snapshot = snapshot });
        }

        public void RecordDecision(double time, DecisionKind kind, int noteIndex)
        {
            RecordDecision(time, kind, noteIndex, 0f);
        }

        /// <summary>Whammy variant: carries the axis value. NoteIndex is unused.</summary>
        public void RecordDecision(double time, DecisionKind kind, int noteIndex, float value)
        {
            if (_entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "RecordDecision requires a prior TakeSnapshot call.");
            }
            _entries[^1].Events.Add(new DecisionEvent(time, kind, noteIndex, value));
        }

        /// <summary>Events with <c>Time > cutoff</c>.</summary>
        public List<DecisionEvent> CollectEventsAfter(double cutoff)
        {
            return (from entry in _entries from ev in entry.Events where ev.Time > cutoff select ev).ToList();
        }

        /// <summary>Find the newest snapshot with <c>SongTime &lt;= targetTime</c>, trim newer
        /// entries, and return the chosen snapshot + decisions injected after it. Returns false
        /// if targetTime is older than the rollback window.</summary>
        public bool TryRollback(
            double targetTime,
            out EngineSnapshot? snapshot,
            out IReadOnlyList<DecisionEvent> replayEvents)
        {
            snapshot = null;
            replayEvents = Array.Empty<DecisionEvent>();

            // Find newest entry with SongTime <= targetTime
            int rollbackIndex = -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (!(_entries[i].SongTime <= targetTime))
                {
                    continue;
                }

                rollbackIndex = i;
                break;
            }
            if (rollbackIndex < 0)
            {
                return false;
            }

            var entry = _entries[rollbackIndex];
            snapshot = entry.Snapshot;

            // Already in time order across entries because snapshots are monotonic
            var replay = new List<DecisionEvent>(entry.Events);
            for (int i = rollbackIndex + 1; i < _entries.Count; i++)
            {
                replay.AddRange(_entries[i].Events);
            }
            replayEvents = replay;

            // Chosen snapshot's entry stays but its event log is cleared. The caller will
            // re-record events during replay (some may now be ConfirmedMiss instead of PredictedHit).
            entry.Events.Clear();
            int dropCount = _entries.Count - rollbackIndex - 1;
            if (dropCount > 0)
            {
                _entries.RemoveRange(rollbackIndex + 1, dropCount);
            }
            return true;
        }

        public void Clear() => _entries.Clear();
    }
}
