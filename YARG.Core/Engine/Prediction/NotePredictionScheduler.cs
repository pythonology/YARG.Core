using System;
using System.Collections.Generic;
using YARG.Core.Chart;

namespace YARG.Core.Engine.Prediction
{
    // Markov-1 prediction over the chart's flat note list: next note is predicted hit if the
    // previous note's confirmed status was hit, predicted miss if it was missed. No prior
    // confirmed context defaults to PredictedHit (optimistic start).
    //
    // Each upcoming note has a "slot" open from note.Time to note.Time + commit window. Inside
    // the slot a hit/miss event flips the pending decision with no rollback. Outside it the
    // decision is committed and any contradicting late event triggers rollback.
    //
    // The scheduler doesn't talk to the engine -- DrainDueDecisions surfaces committed decisions
    // and the simulator translates them into ForceHit / ForceMiss calls.
    public sealed class NotePredictionScheduler<TNoteType>
        where TNoteType : Note<TNoteType>
    {
        public const double DefaultCommitWindowSeconds = 0.05;

        // Extra commit-window slack on the FIRST note after the predicted kind changes
        // (hit↔miss). Transitions are the most likely moment for the prediction to be wrong, so
        // we hold the slot open longer for the next confirmation to land in-window. 0.025 s
        // gives 0.075 s total commit slack on transitions -- still inside typical guitar hit
        // windows so the engine's own miss-detection doesn't pre-empt.
        public const double DefaultModeChangeExtensionSeconds = 0.025;

        public enum DecisionKind : byte
        {
            PredictedHit  = 1,
            ConfirmedMiss = 2,
            PredictedMiss = 3,
            ConfirmedHit  = 4,
        }

        public readonly struct Decision
        {
            public Decision(int noteIndex, double noteHitTime, DecisionKind kind)
            {
                NoteIndex = noteIndex;
                NoteHitTime = noteHitTime;
                Kind = kind;
            }

            public readonly int          NoteIndex;
            public readonly double       NoteHitTime;
            public readonly DecisionKind Kind;
        }

        private readonly IReadOnlyList<TNoteType> _notes;

        // Mutually exclusive -- Register* enforces.
        private readonly HashSet<int> _confirmedMisses = new();
        private readonly HashSet<int> _confirmedHits = new();

        // Per-note decision the scheduler emitted. Register* compares the incoming confirmation
        // against this to decide whether rollback is required.
        private readonly Dictionary<int, DecisionKind> _emittedDecisions = new();

        private readonly double _commitWindowSeconds;
        private readonly double _modeChangeExtensionSeconds;
        private int _nextNoteIndex;
        // Used for mode-change detection; null until the first decision is emitted.
        private bool? _lastEmittedWasHit;

        public NotePredictionScheduler(
            IReadOnlyList<TNoteType> notes,
            double commitWindowSeconds         = DefaultCommitWindowSeconds,
            double modeChangeExtensionSeconds  = DefaultModeChangeExtensionSeconds)
        {
            _notes = notes ?? throw new ArgumentNullException(nameof(notes));
            if (commitWindowSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commitWindowSeconds));
            }
            if (modeChangeExtensionSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(modeChangeExtensionSeconds));
            }
            _commitWindowSeconds = commitWindowSeconds;
            _modeChangeExtensionSeconds = modeChangeExtensionSeconds;
        }

        public double CommitWindowSeconds => _commitWindowSeconds;
        public double ModeChangeExtensionSeconds => _modeChangeExtensionSeconds;
        public int NextNoteIndex => _nextNoteIndex;
        public int NoteCount => _notes.Count;

        public bool IsConfirmedMiss(int noteIndex) => _confirmedMisses.Contains(noteIndex);
        public bool IsConfirmedHit(int noteIndex)  => _confirmedHits.Contains(noteIndex);

        /// <summary>Markov-1: walk backward from <paramref name="noteIndex"/> to the nearest
        /// confirmed note and return its status. Optimistic (true) if no confirmed history.</summary>
        public bool PredictHitAt(int noteIndex)
        {
            if (_confirmedHits.Contains(noteIndex))  return true;
            if (_confirmedMisses.Contains(noteIndex)) return false;

            for (int i = noteIndex - 1; i >= 0; i--)
            {
                if (_confirmedHits.Contains(i))  return true;
                if (_confirmedMisses.Contains(i)) return false;
            }
            return true;
        }

        /// <summary>Returns true if rollback is needed (a miss decision was already emitted for
        /// this note); false otherwise (matches, not emitted yet, or duplicate).</summary>
        public bool RegisterHit(int noteIndex, double currentSongTime)
        {
            if (noteIndex < 0 || noteIndex >= _notes.Count) return false;
            _confirmedMisses.Remove(noteIndex);
            bool isNew = _confirmedHits.Add(noteIndex);
            if (!isNew) return false;

            if (!_emittedDecisions.TryGetValue(noteIndex, out var emitted))
                return false;

            bool emittedHit = emitted == DecisionKind.PredictedHit
                           || emitted == DecisionKind.ConfirmedHit;
            return !emittedHit;
        }

        /// <summary>Returns true if rollback is needed (a hit decision was already emitted).</summary>
        public bool RegisterMiss(int noteIndex, double currentSongTime)
        {
            if (noteIndex < 0 || noteIndex >= _notes.Count) return false;
            _confirmedHits.Remove(noteIndex);
            bool isNew = _confirmedMisses.Add(noteIndex);
            if (!isNew) return false;

            if (!_emittedDecisions.TryGetValue(noteIndex, out var emitted))
                return false;

            bool emittedMiss = emitted == DecisionKind.PredictedMiss
                            || emitted == DecisionKind.ConfirmedMiss;
            return !emittedMiss;
        }

        /// <summary>Emit a decision for every note whose commit deadline has passed at
        /// <paramref name="currentSongTime"/>. Confirmed if we have the outcome, otherwise
        /// predicted via Markov-1.</summary>
        public void DrainDueDecisions(double currentSongTime, List<Decision> output)
        {
            if (output is null) throw new ArgumentNullException(nameof(output));

            while (_nextNoteIndex < _notes.Count)
            {
                var note = _notes[_nextNoteIndex];

                // Determined BEFORE the deadline check so we can detect mode-change and apply
                // the extra commit slack.
                DecisionKind kind;
                if (_confirmedHits.Contains(_nextNoteIndex))
                    kind = DecisionKind.ConfirmedHit;
                else if (_confirmedMisses.Contains(_nextNoteIndex))
                    kind = DecisionKind.ConfirmedMiss;
                else
                    kind = PredictHitAt(_nextNoteIndex)
                        ? DecisionKind.PredictedHit
                        : DecisionKind.PredictedMiss;

                bool kindIsHit = kind == DecisionKind.PredictedHit
                              || kind == DecisionKind.ConfirmedHit;

                // Only applies on hit↔miss transitions; first note and same-kind runs use the regular window.
                bool isModeChange = _lastEmittedWasHit.HasValue
                    && _lastEmittedWasHit.Value != kindIsHit;
                double effectiveWindow = isModeChange
                    ? _commitWindowSeconds + _modeChangeExtensionSeconds
                    : _commitWindowSeconds;

                double commitDeadline = note.Time + effectiveWindow;
                if (commitDeadline > currentSongTime)
                {
                    break;
                }

                output.Add(new Decision(_nextNoteIndex, note.Time, kind));
                _emittedDecisions[_nextNoteIndex] = kind;
                _lastEmittedWasHit = kindIsHit;
                _nextNoteIndex++;
            }
        }

        /// <summary>Drain decisions for uncommitted notes with <c>note.Time &lt;= upToTime</c>,
        /// ignoring the commit-deadline check. Used when a non-note event arrives earlier than
        /// the next note's commit deadline -- its arrival proves the sender moved past those
        /// notes, so the "wait for late confirmation" is already done. Required to keep the
        /// simulator's snapshot-monotonicity invariant.</summary>
        public void ForceDrainUpTo(double upToTime, List<Decision> output)
        {
            if (output is null) throw new ArgumentNullException(nameof(output));

            while (_nextNoteIndex < _notes.Count)
            {
                var note = _notes[_nextNoteIndex];
                if (note.Time > upToTime) break;

                DecisionKind kind;
                if (_confirmedHits.Contains(_nextNoteIndex))
                    kind = DecisionKind.ConfirmedHit;
                else if (_confirmedMisses.Contains(_nextNoteIndex))
                    kind = DecisionKind.ConfirmedMiss;
                else
                    kind = PredictHitAt(_nextNoteIndex)
                        ? DecisionKind.PredictedHit
                        : DecisionKind.PredictedMiss;

                output.Add(new Decision(_nextNoteIndex, note.Time, kind));
                _emittedDecisions[_nextNoteIndex] = kind;
                _lastEmittedWasHit = kind == DecisionKind.PredictedHit
                                  || kind == DecisionKind.ConfirmedHit;
                _nextNoteIndex++;
            }
        }

        /// <summary>Record what the simulator applied during rollback replay so a later
        /// confirmation can detect whether the replay decision still matches.</summary>
        public void RecordEmittedDecision(int noteIndex, DecisionKind kind)
        {
            if (noteIndex < 0 || noteIndex >= _notes.Count) return;
            _emittedDecisions[noteIndex] = kind;
        }

        /// <summary>Rewind the commit cursor. Confirmed-status sets and the emitted-decisions
        /// log are preserved -- replay uses them to re-evaluate predictions during rollback.</summary>
        public void Rewind(int toNoteIndex)
        {
            if (toNoteIndex < 0) toNoteIndex = 0;
            if (toNoteIndex > _notes.Count) toNoteIndex = _notes.Count;
            _nextNoteIndex = toNoteIndex;

            // Re-derive _lastEmittedWasHit so mode-change detection stays consistent across rewind.
            _lastEmittedWasHit = null;
            for (int i = toNoteIndex - 1; i >= 0; i--)
            {
                if (_emittedDecisions.TryGetValue(i, out var emitted))
                {
                    _lastEmittedWasHit = emitted == DecisionKind.PredictedHit
                                      || emitted == DecisionKind.ConfirmedHit;
                    break;
                }
            }
        }

        public void Reset()
        {
            _confirmedMisses.Clear();
            _confirmedHits.Clear();
            _emittedDecisions.Clear();
            _nextNoteIndex = 0;
            _lastEmittedWasHit = null;
        }
    }
}
