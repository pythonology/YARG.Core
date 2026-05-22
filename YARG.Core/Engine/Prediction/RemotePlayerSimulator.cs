using System;
using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Logging;

namespace YARG.Core.Engine.Prediction
{
    // Per-remote-peer simulation owner. Holds a YARG.Core engine configured like the peer's
    // local engine, runs it on a delayed timeline, and feeds it predicted/confirmed events
    // derived from the peer's wire packets.
    //
    // Periodic EngineStateSnapshot packets are the single source of truth for the mirror's
    // BaseStats — they collapse drift and trim the rollback buffer. Per-event decisions drive
    // low-latency visual updates between snapshots; the next snapshot reconciles any drift.
    public sealed class RemotePlayerSimulator<TNoteType> : IRemotePlayerSimulator
        where TNoteType : Note<TNoteType>
    {
        public const double DefaultRemoteTrackDelaySeconds = 0.0;

        private readonly BaseEngine                     _engine;
        private readonly IReadOnlyList<TNoteType>       _notes;
        private readonly NotePredictionScheduler<TNoteType> _scheduler;
        private readonly EngineRollbackBuffer           _rollback;
        private readonly double                         _remoteTrackDelay;
        private readonly double                         _commitWindow;

        // Reused across ticks to keep the hot path allocation-free.
        private readonly List<NotePredictionScheduler<TNoteType>.Decision> _pendingDecisions = new();

        // Future-dated events. Drained in time-order in Update() alongside scheduler decisions.
        private readonly SortedList<double, int>   _pendingReleases = new();    // releaseTime -> root note index
        private readonly SortedList<double, byte>  _pendingSpActivations = new(); // songTime -> sentinel
        private readonly SortedList<double, float> _pendingWhammy = new();      // songTime -> axis value
        private readonly SortedList<double, byte>  _pendingOverstrums = new();  // songTime -> sentinel

        // Idempotency sets: receivers must be safe against duplicate packets.
        private readonly HashSet<int>    _confirmedReleased = new();
        private readonly HashSet<double> _confirmedSpActivations = new();

        private double _authoritativeSnapshotSongTime = double.NegativeInfinity;

        public RemotePlayerSimulator(
            BaseEngine engine,
            IReadOnlyList<TNoteType> notes,
            double remoteTrackDelaySeconds = DefaultRemoteTrackDelaySeconds,
            double commitWindowSeconds     = NotePredictionScheduler<TNoteType>.DefaultCommitWindowSeconds,
            double modeChangeExtensionSeconds = NotePredictionScheduler<TNoteType>.DefaultModeChangeExtensionSeconds)
        {
            _engine    = engine ?? throw new ArgumentNullException(nameof(engine));
            _notes     = notes  ?? throw new ArgumentNullException(nameof(notes));
            _scheduler = new NotePredictionScheduler<TNoteType>(notes, commitWindowSeconds, modeChangeExtensionSeconds);
            _rollback  = new EngineRollbackBuffer();
            _remoteTrackDelay = remoteTrackDelaySeconds;
            _commitWindow     = commitWindowSeconds;

            // Mirror engines have no input pipeline; CanSustainHold / CheckForNoteHit must
            // bypass input-state checks. State is driven by Force* calls + wire snapshots.
            _engine.IsRemoteMirror = true;

            YargLogger.LogFormatInfo(
                "Prediction[sim-ctor]: engine={0} notes={1} trackDelay={2:0.000}s commitWindow={3:0.000}s modeChangeExtra={4:0.000}s",
                engine.GetType().Name, notes.Count,
                remoteTrackDelaySeconds, commitWindowSeconds, modeChangeExtensionSeconds);
        }

        public BaseEngine Engine => _engine;
        public double RemoteTrackDelaySeconds => _remoteTrackDelay;

        // Polled each frame by the per-instrument player (remote mirrors have no OnInputQueued).
        // Samples with axis <= 0.01 reset to 0 so releasing the bar zeros the visual.
        public float LatestWhammyValue { get; private set; }
        public double VisualDelaySeconds => _remoteTrackDelay + _commitWindow;

        public double AuthoritativeSnapshotSongTime => _authoritativeSnapshotSongTime;

        /// <summary>Engine runs at <c>(localSongTime - remoteTrackDelay)</c>. All pending queues
        /// are drained in time order so the engine sees every event at its correct CurrentTime.</summary>
        public void Update(double localSongTime)
        {
            double remoteEngineTime = localSongTime - _remoteTrackDelay;
            if (remoteEngineTime <= _engine.CurrentTime)
            {
                return;
            }

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

                // Force-commit notes that precede the non-note event so the rollback buffer's
                // monotonic-time invariant holds.
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

        // ---- Inbound wire events --------------------------------------------

        public bool OnNoteMissed(int noteIndex, double currentSongTime)
        {
            double remoteEngineTime = currentSongTime - _remoteTrackDelay;
            double noteTime = (noteIndex >= 0 && noteIndex < _notes.Count) ? _notes[noteIndex].Time : double.NaN;

            bool rollbackNeeded = _scheduler.RegisterMiss(noteIndex, remoteEngineTime);
            if (!rollbackNeeded)
            {
                YargLogger.LogFormatDebug(
                    "Prediction[sim-recv] NoteMissed (matches prediction or in-window): noteIndex={0} noteTime={1:0.000} engineTime={2:0.000}",
                    noteIndex, noteTime, remoteEngineTime);
                return true;
            }

            YargLogger.LogFormatInfo(
                "Prediction[sim-recv] NoteMissed (rollback — engine had it as hit): noteIndex={0} noteTime={1:0.000} engineTime={2:0.000} engineCurrent={3:0.000}",
                noteIndex, noteTime, remoteEngineTime, _engine.CurrentTime);

            return RollbackAndReplay(noteTime, "miss", injectedEvent: null);
        }

        public void RecordWireHitOffset(int noteIndex, double wireHitTime)
        {
            if (noteIndex < 0 || noteIndex >= _notes.Count) return;
            // 0 is a sentinel from older senders / missed-only packets — ignore to avoid
            // polluting the histogram with a giant negative offset.
            if (wireHitTime <= 0.0) return;
            double offset = wireHitTime - _notes[noteIndex].Time;
            _engine.BaseStats.AddOffsetSample(offset);
        }

        public bool OnNoteHit(int noteIndex, double currentSongTime)
        {
            double remoteEngineTime = currentSongTime - _remoteTrackDelay;
            double noteTime = (noteIndex >= 0 && noteIndex < _notes.Count) ? _notes[noteIndex].Time : double.NaN;

            bool rollbackNeeded = _scheduler.RegisterHit(noteIndex, remoteEngineTime);
            if (!rollbackNeeded)
            {
                YargLogger.LogFormatDebug(
                    "Prediction[sim-recv] NoteHit (matches prediction or in-window): noteIndex={0} noteTime={1:0.000} engineTime={2:0.000}",
                    noteIndex, noteTime, remoteEngineTime);
                return true;
            }

            YargLogger.LogFormatInfo(
                "Prediction[sim-recv] NoteHit (rollback — engine had it as miss): noteIndex={0} noteTime={1:0.000} engineTime={2:0.000} engineCurrent={3:0.000}",
                noteIndex, noteTime, remoteEngineTime, _engine.CurrentTime);

            return RollbackAndReplay(noteTime, "hit", injectedEvent: null);
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
                YargLogger.LogFormatDebug(
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
                YargLogger.LogFormatDebug(
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
                YargLogger.LogFormatDebug(
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
                YargLogger.LogFormatDebug(
                    "Prediction[sim-recv] Whammy (queued): songTime={0:0.000} value={1:0.00}",
                    songTime, value);
                return;
            }

            // Drop late whammy samples; the next snapshot reconciles SP exactly.
            YargLogger.LogFormatDebug(
                "Prediction[sim-recv] Whammy (LATE, dropped — next snapshot will reconcile): songTime={0:0.000} value={1:0.00} engineCurrent={2:0.000}",
                songTime, value, _engine.CurrentTime);
        }

        // Two-sample ring (previous + latest) for vocal pitch lerp. Sender rate-limits to ~20 Hz
        // so the lerp window is short enough that linear interp looks smooth.
        private double _vocalPitchPrevTime    = double.NegativeInfinity;
        private float  _vocalPitchPrevMidi    = 0f;
        private bool   _vocalPitchPrevSinging;
        private double _vocalPitchLatestTime  = double.NegativeInfinity;
        private float  _vocalPitchLatestMidi  = 0f;
        private bool   _vocalPitchLatestSinging;

        public void OnVocalPitch(double songTime, float pitchMidi, bool isSinging, double currentSongTime)
        {
            // Late samples are dropped — lerp anchor times must be monotonically non-decreasing.
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
            // Forward-prediction isn't useful for pitch (voices change unpredictably) — hold last.
            if (double.IsNegativeInfinity(_vocalPitchPrevTime) || currentSongTime >= _vocalPitchLatestTime)
            {
                return (_vocalPitchLatestMidi, _vocalPitchLatestSinging);
            }
            if (currentSongTime <= _vocalPitchPrevTime)
            {
                return (_vocalPitchPrevMidi, _vocalPitchPrevSinging);
            }

            // IsSinging follows the latest sample (boolean doesn't lerp); transition snaps at latest time.
            double t = (currentSongTime - _vocalPitchPrevTime)
                       / (_vocalPitchLatestTime - _vocalPitchPrevTime);
            float pitch = (float)(_vocalPitchPrevMidi + (_vocalPitchLatestMidi - _vocalPitchPrevMidi) * t);
            return (pitch, _vocalPitchLatestSinging);
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

            // Local events applied after the sender's snapshot was captured. Re-applied on top
            // of the restored state below to bring the mirror back to its current time.
            var postEvents = _rollback.CollectEventsAfter(snapshotSongTime);

            _engine.RestoreSnapshot(wireSnapshot);

            DropPendingOlderThan(snapshotSongTime);

            _rollback.Clear();
            _rollback.TakeSnapshot(snapshotSongTime, wireSnapshot);

            _scheduler.Rewind(_engine.NoteIndex);

            foreach (var ev in postEvents)
            {
                ApplyReplayEvent(ev);
            }

            _authoritativeSnapshotSongTime = snapshotSongTime;

            // Re-emit scheduler decisions now due at the engine's current time. Without this,
            // instruments whose decision time trails note.Time (vocals — phrases score at
            // phrase end) would flash one tick of pre-prediction score between each snapshot
            // and the next Update. Predictions remain speculative; a later wire NoteMissed
            // for the same note still rolls back correctly.
            int replayedDecisions = 0;
            _pendingDecisions.Clear();
            _scheduler.DrainDueDecisions(_engine.CurrentTime, _pendingDecisions);
            foreach (var dec in _pendingDecisions)
            {
                ApplyDecision(dec);
                replayedDecisions++;
            }
            _pendingDecisions.Clear();

            YargLogger.LogFormatDebug(
                "Prediction[sim-snapshot] applied: snapTime={0:0.000} engineWas={1:0.000} engineNow={2:0.000} scoreWas={3} scoreNow={4} replayedEvents={5} reEmittedDecisions={6}",
                snapshotSongTime, engineBefore, _engine.CurrentTime,
                scoreBefore, _engine.BaseStats.TotalScore, postEvents.Count, replayedDecisions);
        }

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
            _authoritativeSnapshotSongTime = double.NegativeInfinity;
        }

        // Snapshot at engine's current time, but skip if it would violate the buffer's
        // monotonic-time invariant. Happens when a wire EngineStateSnapshot lands while a
        // per-event commit is in flight: the wire snapshot advances the anchor past the event's
        // nominal time. Skipping is safe — the wire snapshot already covers the post-event
        // state and Force* on already-resolved notes is idempotent during replay.
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
            // Advance to noteHitTime BEFORE committing so CurrentTick at ForceHit/ForceMiss
            // matches what the local engine had — UpdateMultiplier → RebaseSustains rebases
            // at the correct chart tick.
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

            YargLogger.LogFormatDebug(
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
            YargLogger.LogFormatDebug(
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
            YargLogger.LogFormatDebug(
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
            YargLogger.LogFormatDebug(
                "Prediction[sim-commit] Overstrum: songTime={0:0.000} score={1} combo={2}",
                songTime, _engine.BaseStats.TotalScore, _engine.BaseStats.Combo);
        }

        private void ApplyWhammy(double songTime, float value)
        {
            // Snapshot before applying so rollback can rewind across the sample if a later
            // event invalidates the SP it generated. The whammy timer (started in
            // ForceWhammyAxis) influences SP gain on the active sustain.
            if (songTime > _engine.CurrentTime)
            {
                _engine.Update(songTime);
            }
            TakeRollbackSnapshotIfMonotonic();
            _engine.ForceWhammyAxis(songTime, value);
            _rollback.RecordDecision(
                songTime, EngineRollbackBuffer.DecisionKind.Whammy, 0, value);

            // Mirror has no OnInputQueued path; the per-instrument player polls this each frame.
            LatestWhammyValue = value > 0.01f ? value : 0f;

            YargLogger.LogFormatTrace(
                "Prediction[sim-commit] Whammy: songTime={0:0.000} value={1:0.00}",
                songTime, value);
        }

        // Used after a wire snapshot anchors state — earlier events are baked into it.
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

        // ---- Rollback -------------------------------------------------------

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
                // No buffered snapshot reaches back to targetTime. Drop the late event; the
                // next wire snapshot will reconcile any score discrepancy.
                YargLogger.LogWarning(
                    $"Prediction[sim-rollback] target out of buffer (target={targetTime:0.000}, " +
                    $"buffer={_rollback.Count}) — dropping late {latePathLogTag}; next snapshot will reconcile.");
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

            // Rebuild the rollback buffer during replay; same monotonic guard as the live path.
            TakeRollbackSnapshotIfMonotonic();

            switch (ev.Kind)
            {
                case EngineRollbackBuffer.DecisionKind.PredictedHit:
                case EngineRollbackBuffer.DecisionKind.PredictedMiss:
                {
                    // Re-evaluate against current confirmed state — late confirmations during
                    // replay flip a predicted kind to its Confirmed counterpart.
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
