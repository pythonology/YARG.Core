using System;
using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Logging;

namespace YARG.Core.Engine.Prediction
{
    // Per-remote-peer mirror engine. Snapshots are the source of truth; per-event
    // decisions drive low-latency visuals between them.
    public sealed class RemotePlayerSimulator<TNoteType> : IRemotePlayerSimulator
        where TNoteType : Note<TNoteType>
    {
        private readonly BaseEngine                     _engine;
        private readonly IReadOnlyList<TNoteType>       _notes;
        private readonly NotePredictionScheduler<TNoteType> _scheduler;
        private readonly EngineRollbackBuffer           _rollback;

        // Reused to keep Update allocation-free.
        private readonly List<NotePredictionScheduler<TNoteType>.Decision> _pendingDecisions = new();

        // Future-dated events; drained in time order alongside scheduler decisions.
        private readonly SortedList<double, int>   _pendingReleases = new();
        private readonly SortedList<double, byte>  _pendingSpActivations = new();
        private readonly SortedList<double, float> _pendingWhammy = new();
        private readonly SortedList<double, byte>  _pendingOverstrums = new();

        // Idempotency sets for duplicate packets.
        private readonly HashSet<int>    _confirmedReleased = new();
        private readonly HashSet<double> _confirmedSpActivations = new();

        // RLE receive cursor: the next note index we haven't been told the outcome of.
        // The sender only emits a packet on a hit<->miss flip, so the gap
        // [_nextExpectedNoteIndex, transitionIndex - 1] is the opposite kind of the
        // transition note and is filled in implicitly. Reset to the engine's NoteIndex
        // on each authoritative snapshot.
        private int _nextExpectedNoteIndex;

        private double _authoritativeSnapshotSongTime = double.NegativeInfinity;

        public RemotePlayerSimulator(
            BaseEngine engine,
            IReadOnlyList<TNoteType> notes,
            double commitWindowSeconds     = NotePredictionScheduler<TNoteType>.DefaultCommitWindowSeconds,
            double modeChangeExtensionSeconds = NotePredictionScheduler<TNoteType>.DefaultModeChangeExtensionSeconds)
        {
            _engine    = engine ?? throw new ArgumentNullException(nameof(engine));
            _notes     = notes  ?? throw new ArgumentNullException(nameof(notes));
            _scheduler = new NotePredictionScheduler<TNoteType>(notes, commitWindowSeconds, modeChangeExtensionSeconds);
            _rollback  = new EngineRollbackBuffer();

            // No input pipeline on mirrors; state comes from Force* calls + snapshots.
            _engine.IsRemoteMirror = true;

            YargLogger.LogFormatInfo(
                "Prediction[sim-ctor]: engine={0} notes={1} commitWindow={2:0.000}s modeChangeExtra={3:0.000}s",
                engine.GetType().Name, notes.Count, commitWindowSeconds, modeChangeExtensionSeconds);
        }

        public BaseEngine Engine => _engine;

        // Polled each frame by the per-instrument player. Whammy values under 0.01 zero out.
        public float LatestWhammyValue { get; private set; }

        public double AuthoritativeSnapshotSongTime => _authoritativeSnapshotSongTime;

        private double _lastUpdateLogSongTime = double.NegativeInfinity;
        private int    _consecutiveSkippedUpdates;

        /// <summary>Tick the mirror to the given local song time.</summary>
        public void Update(double localSongTime)
        {
            _latestKnownLocalSongTime = localSongTime;

            double remoteEngineTime = localSongTime;
            if (remoteEngineTime <= _engine.CurrentTime)
            {
                // Engine ahead of local clock. Usually means a wire snapshot restored
                // CurrentTime past us; we sit idle until localSongTime catches up.
                _consecutiveSkippedUpdates++;
                if (_consecutiveSkippedUpdates % 60 == 0
                    && _engine.CurrentTime - localSongTime > 0.5
                    && localSongTime - _lastUpdateLogSongTime > 1.0)
                {
                    _lastUpdateLogSongTime = localSongTime;
                    YargLogger.LogFormatWarning(
                        "Prediction[sim-update] engine ahead of localSongTime by {0:0.000}s for {1} ticks " +
                        "(engineCurrent={2:0.000} localSongTime={3:0.000}). Highway is frozen until the clocks resync.",
                        _engine.CurrentTime - localSongTime, _consecutiveSkippedUpdates,
                        _engine.CurrentTime, localSongTime);
                }
                return;
            }
            _consecutiveSkippedUpdates = 0;

            _pendingDecisions.Clear();
            _scheduler.DrainDueDecisions(remoteEngineTime, _pendingDecisions);

            int decisionIdx = 0;
            while (true)
            {
                double tDecision = decisionIdx < _pendingDecisions.Count
                    ? _pendingDecisions[decisionIdx].NoteHitTime : double.MaxValue;
                double tRelease   = _pendingReleases.Count > 0 ? _pendingReleases.Keys[0] : double.MaxValue;
                double tSp        = _pendingSpActivations.Count > 0 ? _pendingSpActivations.Keys[0] : double.MaxValue;
                double tWhammy    = _pendingWhammy.Count > 0 ? _pendingWhammy.Keys[0] : double.MaxValue;
                double tOverstrum = _pendingOverstrums.Count > 0 ? _pendingOverstrums.Keys[0] : double.MaxValue;

                double earliestNonNote = Math.Min(
                    Math.Min(tRelease, tSp),
                    Math.Min(tWhammy, tOverstrum));

                // Commit notes ahead of the non-note event so rollback buffer time stays monotonic.
                if (earliestNonNote != double.MaxValue
                    && earliestNonNote <= remoteEngineTime
                    && earliestNonNote < tDecision
                    && _scheduler.NextNoteIndex < _scheduler.NoteCount)
                {
                    int beforeCount = _pendingDecisions.Count;
                    _scheduler.ForceDrainUpTo(earliestNonNote, _pendingDecisions);
                    if (_pendingDecisions.Count > beforeCount)
                    {
                        tDecision = decisionIdx < _pendingDecisions.Count
                            ? _pendingDecisions[decisionIdx].NoteHitTime : double.MaxValue;
                    }
                }

                double earliest = Math.Min(tDecision, earliestNonNote);
                if (earliest > remoteEngineTime || earliest == double.MaxValue)
                {
                    break;
                }

                if (earliest == tDecision)
                {
                    ApplyDecision(_pendingDecisions[decisionIdx]);
                    decisionIdx++;
                }
                else if (earliest == tRelease)
                {
                    int noteIndex = _pendingReleases.Values[0];
                    _pendingReleases.RemoveAt(0);
                    ApplySustainRelease(noteIndex, tRelease);
                }
                else if (earliest == tSp)
                {
                    _pendingSpActivations.RemoveAt(0);
                    ApplyStarPowerActivation(tSp);
                }
                else if (earliest == tOverstrum)
                {
                    _pendingOverstrums.RemoveAt(0);
                    ApplyOverstrum(tOverstrum);
                }
                else
                {
                    float value = _pendingWhammy.Values[0];
                    _pendingWhammy.RemoveAt(0);
                    ApplyWhammy(tWhammy, value);
                }
            }

            _engine.Update(remoteEngineTime);
        }

        // Inbound wire events.

        public bool OnNoteMissed(int noteIndex, double currentSongTime)
        {
            return OnNoteOutcome(noteIndex, currentSongTime, explicitWasHit: false);
        }

        public void RecordWireHitOffset(int noteIndex, double wireHitTime)
        {
            if (noteIndex < 0 || noteIndex >= _notes.Count) return;
            // Ignore 0 to avoid polluting the histogram with a giant negative offset
            // if anything ever leaks a sentinel value through this path.
            if (wireHitTime <= 0.0) return;
            double offset = wireHitTime - _notes[noteIndex].Time;
            _engine.BaseStats.AddOffsetSample(offset);
        }

        public bool OnNoteHit(int noteIndex, double currentSongTime)
        {
            // Hit(N): fill [cursor, N-1] as implicit misses, then commit N as hit.
            return OnNoteOutcome(noteIndex, currentSongTime, explicitWasHit: true);
        }

        // Shared body for OnNoteHit / OnNoteMissed under the run-length-encoded wire
        // protocol. The sender only emits a packet when the outcome flips, so the
        // implicit range [_nextExpectedNoteIndex, noteIndex - 1] is the OPPOSITE kind
        // of the explicit one. We register each implicit note + the explicit one and
        // do at most one rollback to the earliest mis-decided note's time -- that
        // single rollback's forward replay reconciles every later note via the
        // scheduler's now-up-to-date confirmed sets.
        private bool OnNoteOutcome(int noteIndex, double currentSongTime, bool explicitWasHit)
        {
            double remoteEngineTime = currentSongTime;
            double explicitNoteTime = (noteIndex >= 0 && noteIndex < _notes.Count)
                ? _notes[noteIndex].Time : double.NaN;

            int earliestRollbackIndex = -1;

            // Implicit fill -- opposite kind. Clamped to non-negative in case a snapshot
            // bumped the cursor past noteIndex (duplicate / out-of-order; ReliableOrdered
            // shouldn't drop or reorder, but defensive against snapshot rewinds).
            int fillStart = Math.Max(_nextExpectedNoteIndex, 0);
            for (int i = fillStart; i < noteIndex; i++)
            {
                bool fillNeedsRollback = explicitWasHit
                    ? _scheduler.RegisterMiss(i, remoteEngineTime)
                    : _scheduler.RegisterHit(i, remoteEngineTime);
                if (fillNeedsRollback && earliestRollbackIndex < 0)
                {
                    earliestRollbackIndex = i;
                }
            }

            // Explicit transition note.
            bool explicitNeedsRollback = explicitWasHit
                ? _scheduler.RegisterHit(noteIndex, remoteEngineTime)
                : _scheduler.RegisterMiss(noteIndex, remoteEngineTime);
            if (explicitNeedsRollback && earliestRollbackIndex < 0)
            {
                earliestRollbackIndex = noteIndex;
            }

            // Advance the cursor past this transition. Don't rewind on duplicates.
            if (noteIndex + 1 > _nextExpectedNoteIndex)
            {
                _nextExpectedNoteIndex = noteIndex + 1;
            }

            string kind = explicitWasHit ? "Hit" : "Miss";
            if (earliestRollbackIndex < 0)
            {
                YargLogger.LogFormatTrace(
                    "Prediction[sim-recv] Note{0} (matches prediction): noteIndex={1} fillRange=[{2},{3}] engineTime={4:0.000}",
                    kind, noteIndex, fillStart, noteIndex - 1, remoteEngineTime);
                return true;
            }

            double rollbackTime = (earliestRollbackIndex >= 0 && earliestRollbackIndex < _notes.Count)
                ? _notes[earliestRollbackIndex].Time
                : (!double.IsNaN(explicitNoteTime) ? explicitNoteTime : remoteEngineTime);
            YargLogger.LogFormatInfo(
                "Prediction[sim-recv] Note{0} (rollback at noteIndex={1}, earliest mis-decided={2}): rollbackTime={3:0.000} engineCurrent={4:0.000}",
                kind, noteIndex, earliestRollbackIndex, rollbackTime, _engine.CurrentTime);

            return RollbackAndReplay(rollbackTime, explicitWasHit ? "hit" : "miss", injectedEvent: null);
        }

        public bool OnSustainReleased(int sustainNoteIndex, double releaseSongTime, double currentSongTime)
        {
            if (!_confirmedReleased.Add(sustainNoteIndex))
            {
                YargLogger.LogFormatTrace(
                    "Prediction[sim-recv] SustainReleased (duplicate): noteIndex={0}",
                    sustainNoteIndex);
                return true;
            }

            if (releaseSongTime > _engine.CurrentTime)
            {
                _pendingReleases[releaseSongTime] = sustainNoteIndex;
                YargLogger.LogFormatTrace(
                    "Prediction[sim-recv] SustainReleased (queued): noteIndex={0} releaseTime={1:0.000} engineCurrent={2:0.000}",
                    sustainNoteIndex, releaseSongTime, _engine.CurrentTime);
                return true;
            }

            YargLogger.LogFormatInfo(
                "Prediction[sim-recv] SustainReleased (LATE, rollback): noteIndex={0} releaseTime={1:0.000} engineCurrent={2:0.000}",
                sustainNoteIndex, releaseSongTime, _engine.CurrentTime);

            return RollbackAndReplay(
                releaseSongTime,
                "sustain release",
                new EngineRollbackBuffer.DecisionEvent(
                    releaseSongTime,
                    EngineRollbackBuffer.DecisionKind.SustainReleased,
                    sustainNoteIndex));
        }

        public bool OnStarPowerActivated(double songTime, double currentSongTime)
        {
            double key = Math.Round(songTime * 1000.0) / 1000.0;
            if (!_confirmedSpActivations.Add(key))
            {
                YargLogger.LogFormatTrace(
                    "Prediction[sim-recv] StarPowerActivated (duplicate): songTime={0:0.000}",
                    songTime);
                return true;
            }

            if (songTime > _engine.CurrentTime)
            {
                _pendingSpActivations[songTime] = 0;
                YargLogger.LogFormatTrace(
                    "Prediction[sim-recv] StarPowerActivated (queued): songTime={0:0.000} engineCurrent={1:0.000}",
                    songTime, _engine.CurrentTime);
                return true;
            }

            YargLogger.LogFormatInfo(
                "Prediction[sim-recv] StarPowerActivated (LATE, rollback): songTime={0:0.000} engineCurrent={1:0.000}",
                songTime, _engine.CurrentTime);

            return RollbackAndReplay(
                songTime,
                "SP activation",
                new EngineRollbackBuffer.DecisionEvent(
                    songTime,
                    EngineRollbackBuffer.DecisionKind.StarPowerActivated,
                    0));
        }

        public bool OnOverstrum(double songTime, double currentSongTime)
        {
            if (songTime > _engine.CurrentTime)
            {
                _pendingOverstrums[songTime] = 0;
                YargLogger.LogFormatTrace(
                    "Prediction[sim-recv] Overstrum (queued): songTime={0:0.000} engineCurrent={1:0.000}",
                    songTime, _engine.CurrentTime);
                return true;
            }

            YargLogger.LogFormatInfo(
                "Prediction[sim-recv] Overstrum (LATE, rollback): songTime={0:0.000} engineCurrent={1:0.000}",
                songTime, _engine.CurrentTime);

            return RollbackAndReplay(
                songTime,
                "overstrum",
                new EngineRollbackBuffer.DecisionEvent(
                    songTime,
                    EngineRollbackBuffer.DecisionKind.Overstrum,
                    noteIndex: 0));
        }

        public void OnWhammy(double songTime, float value, double currentSongTime)
        {
            if (songTime > _engine.CurrentTime)
            {
                _pendingWhammy[songTime] = value;
                YargLogger.LogFormatTrace(
                    "Prediction[sim-recv] Whammy (queued): songTime={0:0.000} value={1:0.00}",
                    songTime, value);
                return;
            }

            // Late sample. Under any non-zero RTT, songTime always trails the mirror's
            // CurrentTime (the engine has been ticking forward in real time while the
            // packet was in flight), so this branch is taken on essentially every
            // whammy packet.
            LatestWhammyValue = value > 0.01f ? value : 0f;

            YargLogger.LogFormatTrace(
                "Prediction[sim-recv] Whammy (LATE): songTime={0:0.000} value={1:0.00} engineCurrent={2:0.000}",
                songTime, value, _engine.CurrentTime);
        }

        // Two-sample ring (previous + latest) for vocal pitch lerp.
        private double _vocalPitchPrevTime    = double.NegativeInfinity;
        private float  _vocalPitchPrevMidi    = 0f;
        private bool   _vocalPitchPrevSinging;
        private double _vocalPitchLatestTime  = double.NegativeInfinity;
        private float  _vocalPitchLatestMidi  = 0f;
        private bool   _vocalPitchLatestSinging;

        // Failsafe in case the sender's singing-to-silent transition packet drops.
        // Larger than the ~50 ms inter-sample interval, small enough to hide the
        // needle quickly when packets really do stop.
        private const double VocalPitchStaleSeconds = 0.2;

        // Forward-project the prev-to-latest slope only briefly. Vibrato peaks and
        // phrase drop-offs would overshoot on a longer window.
        private const double VocalPitchMaxExtrapolateSeconds = 0.025;

        // Slope damping during forward projection. 1.0 = raw linear, 0.0 = hold.
        private const double VocalPitchExtrapolateSlopeDamping = 0.4;

        public void OnVocalPitch(double songTime, float pitchMidi, bool isSinging, double currentSongTime)
        {
            // Late samples dropped; anchor times must be non-decreasing for the lerp.
            if (songTime < _vocalPitchLatestTime) return;

            _vocalPitchPrevTime    = _vocalPitchLatestTime;
            _vocalPitchPrevMidi    = _vocalPitchLatestMidi;
            _vocalPitchPrevSinging = _vocalPitchLatestSinging;

            _vocalPitchLatestTime    = songTime;
            _vocalPitchLatestMidi    = pitchMidi;
            _vocalPitchLatestSinging = isSinging;
        }

        public (float pitchMidi, bool isSinging) GetInterpolatedPitch(double currentSongTime)
        {
            if (double.IsNegativeInfinity(_vocalPitchLatestTime))
            {
                return (0f, false);
            }

            // Stale: hide the needle but hold position. If the most recent sample was
            // the singing-to-silent transition, the analyzer was chasing noise, so
            // pin to the previous still-singing anchor instead.
            if (currentSongTime - _vocalPitchLatestTime > VocalPitchStaleSeconds)
            {
                if (!_vocalPitchLatestSinging && _vocalPitchPrevSinging
                    && !double.IsNegativeInfinity(_vocalPitchPrevTime))
                {
                    return (_vocalPitchPrevMidi, false);
                }
                return (_vocalPitchLatestMidi, false);
            }

            // Only one sample so far: hold latest.
            if (double.IsNegativeInfinity(_vocalPitchPrevTime))
            {
                return (_vocalPitchLatestMidi, _vocalPitchLatestSinging);
            }

            // Duplicate timestamps: hold latest until a real interval shows up.
            double dt = _vocalPitchLatestTime - _vocalPitchPrevTime;
            if (dt <= 0)
            {
                return (_vocalPitchLatestMidi, _vocalPitchLatestSinging);
            }

            // Read time before prev: rare but possible; clamp to prev.
            if (currentSongTime <= _vocalPitchPrevTime)
            {
                return (_vocalPitchPrevMidi, _vocalPitchPrevSinging);
            }

            // Singing flag flipped between anchors. Don't lerp across the boundary;
            // the silent side's pitch is whatever noise the analyzer was chasing.
            if (_vocalPitchPrevSinging != _vocalPitchLatestSinging)
            {
                return _vocalPitchLatestSinging
                    ? (_vocalPitchLatestMidi, true)
                    : (_vocalPitchPrevMidi, false);
            }

            if (currentSongTime <= _vocalPitchLatestTime)
            {
                // Interpolation between known anchors.
                double tLerp = (currentSongTime - _vocalPitchPrevTime) / dt;
                float lerpPitch = (float)(_vocalPitchPrevMidi
                    + (_vocalPitchLatestMidi - _vocalPitchPrevMidi) * tLerp);
                return (lerpPitch, _vocalPitchLatestSinging);
            }

            // Forward extrapolation: cap the lookahead and dampen the slope.
            double aheadSeconds = currentSongTime - _vocalPitchLatestTime;
            if (aheadSeconds > VocalPitchMaxExtrapolateSeconds)
            {
                aheadSeconds = VocalPitchMaxExtrapolateSeconds;
            }
            double slopePerSec = (_vocalPitchLatestMidi - _vocalPitchPrevMidi) / dt;
            float predicted = (float)(_vocalPitchLatestMidi
                + slopePerSec * aheadSeconds * VocalPitchExtrapolateSlopeDamping);
            return (predicted, _vocalPitchLatestSinging);
        }

        /// <summary>The single drift-reconciliation mechanism. Restores the engine to the
        /// sender's exact state at <paramref name="snapshotSongTime"/>, anchors the rollback
        /// buffer here, drops pending queue entries baked into the snapshot, then replays local
        /// events that occurred after the snapshot's time.</summary>
        public void OnEngineStateSnapshot(EngineSnapshot wireSnapshot, double snapshotSongTime)
        {
            if (wireSnapshot is null)
            {
                throw new ArgumentNullException(nameof(wireSnapshot));
            }

            int scoreBefore = _engine.BaseStats.TotalScore;
            double engineBefore = _engine.CurrentTime;

            // Events we already applied past the sender's snapshot time; we replay them
            // on top of the restored state below to bring the mirror back to current.
            var postEvents = _rollback.CollectEventsAfter(snapshotSongTime);

            _engine.RestoreSnapshot(wireSnapshot);

            DropPendingOlderThan(snapshotSongTime);

            _rollback.Clear();
            _rollback.TakeSnapshot(snapshotSongTime, wireSnapshot);

            _scheduler.Rewind(_engine.NoteIndex);

            // Snapshot is authoritative: the next sender transition references notes at
            // or beyond the engine's restored NoteIndex, so resync the RLE fill cursor.
            _nextExpectedNoteIndex = _engine.NoteIndex;

            foreach (var ev in postEvents)
            {
                ApplyReplayEvent(ev);
            }

            _authoritativeSnapshotSongTime = snapshotSongTime;

            // Re-emit decisions now due at the engine's current time. Skipping this
            // makes vocals flash a tick of pre-prediction score every snapshot, since
            // their decision time trails the note's start.
            int replayedDecisions = 0;
            _pendingDecisions.Clear();
            _scheduler.DrainDueDecisions(_engine.CurrentTime, _pendingDecisions);
            foreach (var dec in _pendingDecisions)
            {
                ApplyDecision(dec);
                replayedDecisions++;
            }
            _pendingDecisions.Clear();

            YargLogger.LogFormatTrace(
                "Prediction[sim-snapshot] applied: snapTime={0:0.000} engineWas={1:0.000} engineNow={2:0.000} scoreWas={3} scoreNow={4} replayedEvents={5} reEmittedDecisions={6}",
                snapshotSongTime, engineBefore, _engine.CurrentTime,
                scoreBefore, _engine.BaseStats.TotalScore, postEvents.Count, replayedDecisions);

            // Future-stamped snapshot probe: if the snapshot's CurrentTime is ahead of
            // the receiver's most recent localSongTime, sim.Update will short-circuit
            // on subsequent ticks (its `remoteEngineTime <= _engine.CurrentTime` gate)
            // and the engine sits frozen until local time catches up. Under normal
            // network latency this never happens (the snapshot was created at the
            // sender's clock and takes RTT to arrive, so it's always in the past);
            // when it DOES happen the engine appears stuck for the rest of the song.
            // The most likely cause is clock skew or a wall-clock-sync miscalibration
            // that drifted the receiver's localSongTime behind the sender's. Log so we
            // can see the exact moment the stall begins.
            if (_engine.CurrentTime > _latestKnownLocalSongTime
                && !double.IsNegativeInfinity(_latestKnownLocalSongTime))
            {
                YargLogger.LogFormatWarning(
                    "Prediction[sim-snapshot] future-stamped snapshot: snapTime={0:0.000} engineNow={1:0.000} " +
                    "latestLocalSongTime={2:0.000} skew={3:0.000}s",
                    snapshotSongTime, _engine.CurrentTime, _latestKnownLocalSongTime,
                    _engine.CurrentTime - _latestKnownLocalSongTime);
            }

            _consecutiveSkippedUpdates = 0;
            _lastUpdateLogSongTime = double.NegativeInfinity;
        }

        // Captured from the most recent Update / wire event for the snapshot skew check.
        private double _latestKnownLocalSongTime = double.NegativeInfinity;

        public void Reset()
        {
            _rollback.Clear();
            _scheduler.Reset();
            _pendingReleases.Clear();
            _pendingSpActivations.Clear();
            _pendingWhammy.Clear();
            _pendingOverstrums.Clear();
            _confirmedReleased.Clear();
            _confirmedSpActivations.Clear();
            _nextExpectedNoteIndex = 0;
            _authoritativeSnapshotSongTime = double.NegativeInfinity;
            _consecutiveSkippedUpdates = 0;
            _lastUpdateLogSongTime = double.NegativeInfinity;
            _latestKnownLocalSongTime = double.NegativeInfinity;
            _vocalPitchPrevTime    = double.NegativeInfinity;
            _vocalPitchLatestTime  = double.NegativeInfinity;
            _vocalPitchPrevMidi    = 0f;
            _vocalPitchLatestMidi  = 0f;
            _vocalPitchPrevSinging = false;
            _vocalPitchLatestSinging = false;
        }

        // Snapshot at engine current time. Skipped if it would violate the buffer's
        // monotonic-time invariant, which is safe since Force* is idempotent on replay.
        private void TakeRollbackSnapshotIfMonotonic()
        {
            double t = _engine.CurrentTime;
            if (t > _rollback.LatestSongTime)
            {
                _rollback.TakeSnapshot(t, _engine.CreateSnapshot());
            }
        }


        private void ApplyDecision(NotePredictionScheduler<TNoteType>.Decision decision)
        {
            // Advance before committing so CurrentTick at ForceHit/ForceMiss matches
            // what the sender's engine had at the same point.
            if (decision.NoteHitTime > _engine.CurrentTime)
            {
                _engine.Update(decision.NoteHitTime);
            }

            TakeRollbackSnapshotIfMonotonic();

            EngineRollbackBuffer.DecisionKind kind;
            bool applyAsHit;
            switch (decision.Kind)
            {
                case NotePredictionScheduler<TNoteType>.DecisionKind.PredictedHit:
                    kind = EngineRollbackBuffer.DecisionKind.PredictedHit;
                    applyAsHit = true;
                    break;
                case NotePredictionScheduler<TNoteType>.DecisionKind.ConfirmedHit:
                    kind = EngineRollbackBuffer.DecisionKind.ConfirmedHit;
                    applyAsHit = true;
                    break;
                case NotePredictionScheduler<TNoteType>.DecisionKind.PredictedMiss:
                    kind = EngineRollbackBuffer.DecisionKind.PredictedMiss;
                    applyAsHit = false;
                    break;
                default: // ConfirmedMiss
                    kind = EngineRollbackBuffer.DecisionKind.ConfirmedMiss;
                    applyAsHit = false;
                    break;
            }

            if (applyAsHit) _engine.ForceHit(decision.NoteIndex);
            else            _engine.ForceMiss(decision.NoteIndex);

            _rollback.RecordDecision(decision.NoteHitTime, kind, decision.NoteIndex);

            YargLogger.LogFormatTrace(
                "Prediction[sim-commit] {0}: noteIndex={1} noteTime={2:0.000} engineCurrent={3:0.000} combo={4} mult={5}x score={6}",
                kind, decision.NoteIndex, decision.NoteHitTime, _engine.CurrentTime,
                _engine.BaseStats.Combo, _engine.BaseStats.ScoreMultiplier, _engine.BaseStats.TotalScore);
        }

        private void ApplySustainRelease(int sustainNoteIndex, double releaseTime)
        {
            if (releaseTime > _engine.CurrentTime)
            {
                _engine.Update(releaseTime);
            }

            TakeRollbackSnapshotIfMonotonic();
            _engine.ForceReleaseSustain(sustainNoteIndex);
            _rollback.RecordDecision(
                releaseTime,
                EngineRollbackBuffer.DecisionKind.SustainReleased,
                sustainNoteIndex);
            YargLogger.LogFormatTrace(
                "Prediction[sim-commit] SustainReleased: noteIndex={0} releaseTime={1:0.000} score={2}",
                sustainNoteIndex, releaseTime, _engine.BaseStats.TotalScore);
        }

        private void ApplyStarPowerActivation(double songTime)
        {
            if (songTime > _engine.CurrentTime)
            {
                _engine.Update(songTime);
            }

            TakeRollbackSnapshotIfMonotonic();
            _engine.ForceStarPowerActivation(songTime);
            _rollback.RecordDecision(
                songTime,
                EngineRollbackBuffer.DecisionKind.StarPowerActivated,
                0);
            YargLogger.LogFormatTrace(
                "Prediction[sim-commit] StarPowerActivated: songTime={0:0.000} spActive={1}",
                songTime, _engine.BaseStats.IsStarPowerActive);
        }

        private void ApplyOverstrum(double songTime)
        {
            if (songTime > _engine.CurrentTime)
            {
                _engine.Update(songTime);
            }
            TakeRollbackSnapshotIfMonotonic();
            _engine.ForceOverstrum(songTime);
            _rollback.RecordDecision(
                songTime, EngineRollbackBuffer.DecisionKind.Overstrum, 0);
            YargLogger.LogFormatTrace(
                "Prediction[sim-commit] Overstrum: songTime={0:0.000} score={1} combo={2}",
                songTime, _engine.BaseStats.TotalScore, _engine.BaseStats.Combo);
        }

        private void ApplyWhammy(double songTime, float value)
        {
            // Snapshot before applying so rollback can rewind across the sample if a
            // later event invalidates the SP it generated.
            if (songTime > _engine.CurrentTime)
            {
                _engine.Update(songTime);
            }
            TakeRollbackSnapshotIfMonotonic();
            _engine.ForceWhammyAxis(songTime, value);
            _rollback.RecordDecision(
                songTime, EngineRollbackBuffer.DecisionKind.Whammy, 0, value);

            // Polled per-frame by the per-instrument player.
            LatestWhammyValue = value > 0.01f ? value : 0f;

            YargLogger.LogFormatTrace(
                "Prediction[sim-commit] Whammy: songTime={0:0.000} value={1:0.00}",
                songTime, value);
        }

        // Used after a wire snapshot anchors state; earlier events are baked into it.
        private void DropPendingOlderThan(double cutoff)
        {
            DropFromSortedListOlderThan(_pendingReleases, cutoff);
            DropFromSortedListOlderThan(_pendingSpActivations, cutoff);
            DropFromSortedListOlderThan(_pendingWhammy, cutoff);
            DropFromSortedListOlderThan(_pendingOverstrums, cutoff);
        }

        private static void DropFromSortedListOlderThan<TValue>(SortedList<double, TValue> list, double cutoff)
        {
            while (list.Count > 0 && list.Keys[0] <= cutoff)
            {
                list.RemoveAt(0);
            }
        }

        // Rollback.

        private bool RollbackAndReplay(
            double targetTime,
            string latePathLogTag,
            EngineRollbackBuffer.DecisionEvent? injectedEvent)
        {
            double engineBefore = _engine.CurrentTime;
            int scoreBefore = _engine.BaseStats.TotalScore;
            int comboBefore = _engine.BaseStats.Combo;

            if (!_rollback.TryRollback(targetTime, out var snapshot, out var replay))
            {
                // Late event outside the buffer; next snapshot reconciles.
                YargLogger.LogWarning(
                    $"Prediction[sim-rollback] target out of buffer (target={targetTime:0.000}, " +
                    $"buffer={_rollback.Count}); dropping late {latePathLogTag}");
                return false;
            }

            YargLogger.LogFormatInfo(
                "Prediction[sim-rollback] starting: tag={0} targetTime={1:0.000} engineWas={2:0.000} scoreWas={3} comboWas={4} replayEvents={5}",
                latePathLogTag, targetTime, engineBefore, scoreBefore, comboBefore, replay.Count);

            _engine.RestoreSnapshot(snapshot!);
            _scheduler.Rewind(_engine.NoteIndex);

            // Merge replay log + injected event in time order.
            int replayIdx = 0;
            bool injectedPending = injectedEvent.HasValue;

            while (replayIdx < replay.Count || injectedPending)
            {
                double replayTime = replayIdx < replay.Count ? replay[replayIdx].Time : double.MaxValue;
                double injectedTime = injectedPending ? injectedEvent!.Value.Time : double.MaxValue;

                EngineRollbackBuffer.DecisionEvent ev;
                if (replayTime <= injectedTime)
                {
                    ev = replay[replayIdx++];
                }
                else
                {
                    ev = injectedEvent!.Value;
                    injectedPending = false;
                }

                ApplyReplayEvent(ev);
            }

            YargLogger.LogFormatInfo(
                "Prediction[sim-rollback] done: tag={0} engineNow={1:0.000} scoreNow={2} comboNow={3} (was score={4} combo={5})",
                latePathLogTag, _engine.CurrentTime, _engine.BaseStats.TotalScore, _engine.BaseStats.Combo,
                scoreBefore, comboBefore);
            return true;
        }

        private void ApplyReplayEvent(EngineRollbackBuffer.DecisionEvent ev)
        {
            if (ev.Time > _engine.CurrentTime)
            {
                _engine.Update(ev.Time);
            }

            TakeRollbackSnapshotIfMonotonic();

            switch (ev.Kind)
            {
                case EngineRollbackBuffer.DecisionKind.PredictedHit:
                case EngineRollbackBuffer.DecisionKind.PredictedMiss:
                {
                    // Re-evaluate against current confirmed state; late confirmations
                    // upgrade a predicted kind to its Confirmed counterpart.
                    EngineRollbackBuffer.DecisionKind kind;
                    bool applyAsHit;
                    if (_scheduler.IsConfirmedHit(ev.NoteIndex))
                    {
                        kind = EngineRollbackBuffer.DecisionKind.ConfirmedHit;
                        applyAsHit = true;
                    }
                    else if (_scheduler.IsConfirmedMiss(ev.NoteIndex))
                    {
                        kind = EngineRollbackBuffer.DecisionKind.ConfirmedMiss;
                        applyAsHit = false;
                    }
                    else if (_scheduler.PredictHitAt(ev.NoteIndex))
                    {
                        kind = EngineRollbackBuffer.DecisionKind.PredictedHit;
                        applyAsHit = true;
                    }
                    else
                    {
                        kind = EngineRollbackBuffer.DecisionKind.PredictedMiss;
                        applyAsHit = false;
                    }

                    if (applyAsHit) _engine.ForceHit(ev.NoteIndex);
                    else            _engine.ForceMiss(ev.NoteIndex);

                    _rollback.RecordDecision(ev.Time, kind, ev.NoteIndex);
                    _scheduler.RecordEmittedDecision(ev.NoteIndex,
                        kind switch
                        {
                            EngineRollbackBuffer.DecisionKind.ConfirmedHit  => NotePredictionScheduler<TNoteType>.DecisionKind.ConfirmedHit,
                            EngineRollbackBuffer.DecisionKind.ConfirmedMiss => NotePredictionScheduler<TNoteType>.DecisionKind.ConfirmedMiss,
                            EngineRollbackBuffer.DecisionKind.PredictedHit  => NotePredictionScheduler<TNoteType>.DecisionKind.PredictedHit,
                            _                                                => NotePredictionScheduler<TNoteType>.DecisionKind.PredictedMiss,
                        });
                    _scheduler.Rewind(ev.NoteIndex + 1);
                    break;
                }

                case EngineRollbackBuffer.DecisionKind.ConfirmedHit:
                {
                    _engine.ForceHit(ev.NoteIndex);
                    _rollback.RecordDecision(
                        ev.Time, EngineRollbackBuffer.DecisionKind.ConfirmedHit, ev.NoteIndex);
                    _scheduler.RecordEmittedDecision(ev.NoteIndex,
                        NotePredictionScheduler<TNoteType>.DecisionKind.ConfirmedHit);
                    _scheduler.Rewind(ev.NoteIndex + 1);
                    break;
                }

                case EngineRollbackBuffer.DecisionKind.ConfirmedMiss:
                {
                    _engine.ForceMiss(ev.NoteIndex);
                    _rollback.RecordDecision(
                        ev.Time, EngineRollbackBuffer.DecisionKind.ConfirmedMiss, ev.NoteIndex);
                    _scheduler.RecordEmittedDecision(ev.NoteIndex,
                        NotePredictionScheduler<TNoteType>.DecisionKind.ConfirmedMiss);
                    _scheduler.Rewind(ev.NoteIndex + 1);
                    break;
                }

                case EngineRollbackBuffer.DecisionKind.SustainReleased:
                {
                    _engine.ForceReleaseSustain(ev.NoteIndex);
                    _rollback.RecordDecision(
                        ev.Time, EngineRollbackBuffer.DecisionKind.SustainReleased, ev.NoteIndex);
                    break;
                }

                case EngineRollbackBuffer.DecisionKind.StarPowerActivated:
                {
                    _engine.ForceStarPowerActivation(ev.Time);
                    _rollback.RecordDecision(
                        ev.Time, EngineRollbackBuffer.DecisionKind.StarPowerActivated, 0);
                    break;
                }

                case EngineRollbackBuffer.DecisionKind.Whammy:
                {
                    _engine.ForceWhammyAxis(ev.Time, ev.Value);
                    _rollback.RecordDecision(
                        ev.Time, EngineRollbackBuffer.DecisionKind.Whammy, 0, ev.Value);
                    break;
                }

                case EngineRollbackBuffer.DecisionKind.Overstrum:
                {
                    _engine.ForceOverstrum(ev.Time);
                    _rollback.RecordDecision(
                        ev.Time, EngineRollbackBuffer.DecisionKind.Overstrum, 0);
                    break;
                }
            }
        }
    }
}
