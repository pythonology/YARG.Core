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
        private readonly BaseEngine                     _engine;
        private readonly IReadOnlyList<TNoteType>       _notes;
        private readonly NotePredictionScheduler<TNoteType> _scheduler;
        private readonly EngineRollbackBuffer           _rollback;

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

        // Note hit/miss wire protocol cursor. The sender only emits a packet when the
        // outcome FLIPS (hit→miss or miss→hit), so each arriving event implies that
        // every note since the previous event was the *opposite* kind: a Hit(N)
        // packet means notes [cursor, N-1] were misses and N itself was a hit; a
        // Miss(N) packet means [cursor, N-1] were hits and N itself was a miss.
        // This is Markov-1-consistent — the scheduler's prediction over those
        // implicit ranges already matches the opposite kind, so the gap fills
        // typically don't trigger rollback; only the transition note (N) does.
        //
        // Reset to 0 on Reset() and rewound to the engine's NoteIndex on snapshot
        // restore so the next sender transition references the right window.
        private int _nextExpectedNoteIndex;

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

            // Mirror engines have no input pipeline; CanSustainHold / CheckForNoteHit must
            // bypass input-state checks. State is driven by Force* calls + wire snapshots.
            _engine.IsRemoteMirror = true;

            YargLogger.LogFormatInfo(
                "Prediction[sim-ctor]: engine={0} notes={1} commitWindow={2:0.000}s modeChangeExtra={3:0.000}s",
                engine.GetType().Name, notes.Count, commitWindowSeconds, modeChangeExtensionSeconds);
        }

        public BaseEngine Engine => _engine;

        // Polled each frame by the per-instrument player (remote mirrors have no OnInputQueued).
        // Samples with axis <= 0.01 reset to 0 so releasing the bar zeros the visual.
        public float LatestWhammyValue { get; private set; }

        public double AuthoritativeSnapshotSongTime => _authoritativeSnapshotSongTime;

        // Diagnostic state for the "engine stuck behind localSongTime" probe below.
        private double _lastUpdateLogSongTime = double.NegativeInfinity;
        private int    _consecutiveSkippedUpdates;

        /// <summary>Mirror engine runs at local song time directly — no transport-delay budget,
        /// no visual delay. Pending queues drain in time order so the engine sees every event
        /// at its correct CurrentTime.</summary>
        public void Update(double localSongTime)
        {
            // Track the local clock for the snapshot-apply skew probe below; capture
            // even when we early-return so the probe has fresh data if the engine is
            // already stalled by the time a snapshot arrives.
            _latestKnownLocalSongTime = localSongTime;

            double remoteEngineTime = localSongTime;
            if (remoteEngineTime <= _engine.CurrentTime)
            {
                // Diagnostic probe: catch the "sync broke mid-song" case. The intended
                // steady state is localSongTime ≈ _engine.CurrentTime (small frame-time
                // gap). If a snapshot restored engine.CurrentTime ahead of localSongTime
                // (clock skew, future-stamped wire snapshot), this early-out fires and
                // the engine never advances until localSongTime catches up. A run of
                // skips wider than ~1 s strongly suggests the engine is stuck ahead.
                _consecutiveSkippedUpdates++;
                if (_consecutiveSkippedUpdates % 60 == 0
                    && _engine.CurrentTime - localSongTime > 0.5
                    && localSongTime - _lastUpdateLogSongTime > 1.0)
                {
                    _lastUpdateLogSongTime = localSongTime;
                    YargLogger.LogFormatWarning(
                        "Prediction[sim-update] engine ahead of localSongTime by {0:0.000}s for {1} consecutive ticks " +
                        "(engineCurrent={2:0.000} localSongTime={3:0.000}). Engine is stalled — wire events landing here " +
                        "will fall into RegisterHit/Miss without advancing the engine clock, so PredictedHit/Miss never " +
                        "commit and the highway looks frozen. Likely a snapshot restored CurrentTime ahead of local.",
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
            // Miss(N): fill [cursor, N-1] as implicit hits, then commit N as miss.
            return OnNoteOutcome(noteIndex, currentSongTime, explicitWasHit: false);
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
            // Hit(N): fill [cursor, N-1] as implicit misses, then commit N as hit.
            return OnNoteOutcome(noteIndex, currentSongTime, explicitWasHit: true);
        }

        // Shared body for OnNoteHit / OnNoteMissed under the run-length-encoded wire
        // protocol. The sender only emits a packet when the outcome flips, so the
        // implicit range [_nextExpectedNoteIndex, noteIndex - 1] is the OPPOSITE kind
        // of the explicit one. We register each implicit note + the explicit one and
        // do at most one rollback to the earliest mis-decided note's time — that
        // single rollback's forward replay reconciles every later note via the
        // scheduler's now-up-to-date confirmed sets.
        private bool OnNoteOutcome(int noteIndex, double currentSongTime, bool explicitWasHit)
        {
            double remoteEngineTime = currentSongTime;
            double explicitNoteTime = (noteIndex >= 0 && noteIndex < _notes.Count)
                ? _notes[noteIndex].Time : double.NaN;

            int earliestRollbackIndex = -1;

            // Implicit fill — opposite kind. Clamped to non-negative in case a snapshot
            // bumped the cursor past noteIndex (duplicate / out-of-order; ReliableOrdered
            // shouldn't drop or reorder, but defensive against snapshot rewinds).
            int fillStart = Math.Max(_nextExpectedNoteIndex, 0);
            for (int i = fillStart; i < noteIndex; i++)
            {
                bool needsRollback = explicitWasHit
                    ? _scheduler.RegisterMiss(i, remoteEngineTime)
                    : _scheduler.RegisterHit(i, remoteEngineTime);
                if (needsRollback && earliestRollbackIndex < 0)
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
                YargLogger.LogFormatDebug(
                    "Prediction[sim-recv] Note{0} (matches prediction): noteIndex={1} fillRange=[{2},{3}] engineTime={4:0.000}",
                    kind, noteIndex, fillStart, noteIndex - 1, remoteEngineTime);
                return true;
            }

            double rollbackTime = (earliestRollbackIndex >= 0 && earliestRollbackIndex < _notes.Count)
                ? _notes[earliestRollbackIndex].Time : explicitNoteTime;
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

            // Late sample. Under any non-zero RTT, songTime always trails the mirror's
            // CurrentTime (the engine has been ticking forward in real time while the
            // packet was in flight), so this branch is taken on essentially every
            // whammy packet.
            LatestWhammyValue = value > 0.01f ? value : 0f;

            YargLogger.LogFormatTrace(
                "Prediction[sim-recv] Whammy (LATE): songTime={0:0.000} value={1:0.00} engineCurrent={2:0.000}",
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

        // Sender emits ONLY while singing. Once the user goes silent the sender does
        // a short grace-period hold, then emits a single singing→silent transition
        // packet and stops. This staleness gate is the failsafe for when that single
        // transition packet drops over UDP. 200 ms rides out ordinary network jitter
        // and the ~50 ms inter-sample interval without false-tripping, while still
        // hiding the needle reasonably quickly if the transition packet really is
        // lost (the sender's own grace period guarantees we never see real flicker
        // in the singing direction).
        private const double VocalPitchStaleSeconds = 0.2;

        // Forward-extrapolation cap. We project past the latest sample along the
        // prev→latest slope while waiting for the next packet, but pitch is one of
        // the higher-frequency signals (vibrato, scoops, sharp drop-offs at phrase
        // ends), so an aggressive forward project peaks/troughs in the wrong
        // direction within a single sample interval. Cap at half a typical send
        // interval, then dampen the projected slope below 1.0 so the prediction
        // never overshoots a sample-interval's worth of motion in the wrong
        // direction. The consumer's per-frame transform Mathf.Lerp does the rest.
        private const double VocalPitchMaxExtrapolateSeconds = 0.025;

        // Slope damping past _vocalPitchLatestTime. 1.0 = raw linear extrapolation
        // (what we had — overshoots vibrato), 0.0 = hold latest (no prediction).
        // 0.4 keeps a gentle predictive lean without flying off the chart range
        // when the singer hits a vibrato peak right before a packet is delayed.
        private const double VocalPitchExtrapolateSlopeDamping = 0.4;

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
            // No samples yet — render at zero, hidden.
            if (double.IsNegativeInfinity(_vocalPitchLatestTime))
            {
                return (0f, false);
            }

            // Staleness gate. Sender stopped (or the transition-to-silent packet was
            // dropped) more than VocalPitchStaleSeconds ago. Force isSinging=false; the
            // consumer hides the needle but keeps the last position so the next show
            // snaps back at the prior pitch instead of teleporting to zero.
            //
            // When the most-recent sample is the singing→silent transition packet, its
            // pitch value isn't a reliable continuation of the singer's contour — the
            // analyzer was already chasing near-zero / noise-floor input by the time
            // the sender flipped the flag. Pin the held position to the *previous*
            // (still-singing) anchor's pitch so the needle parks at where the singer
            // actually was when they stopped, not at the analyzer's tail noise.
            if (currentSongTime - _vocalPitchLatestTime > VocalPitchStaleSeconds)
            {
                if (!_vocalPitchLatestSinging && _vocalPitchPrevSinging
                    && !double.IsNegativeInfinity(_vocalPitchPrevTime))
                {
                    return (_vocalPitchPrevMidi, false);
                }
                return (_vocalPitchLatestMidi, false);
            }

            // Only one sample so far — nothing to derive a velocity from. Hold latest.
            // (Pre-staleness, so isSinging follows the sample.)
            if (double.IsNegativeInfinity(_vocalPitchPrevTime))
            {
                return (_vocalPitchLatestMidi, _vocalPitchLatestSinging);
            }

            // Degenerate window (duplicate timestamps). Avoid the divide-by-zero by
            // falling back to the latest value; next non-duplicate sample re-arms the
            // pair.
            double dt = _vocalPitchLatestTime - _vocalPitchPrevTime;
            if (dt <= 0)
            {
                return (_vocalPitchLatestMidi, _vocalPitchLatestSinging);
            }

            // Read time before prev — should be rare (out-of-order delivery is dropped
            // earlier in OnVocalPitch) but defensible. Clamp to prev so the value stays
            // bounded by actually-observed samples.
            if (currentSongTime <= _vocalPitchPrevTime)
            {
                return (_vocalPitchPrevMidi, _vocalPitchPrevSinging);
            }

            // Singing-state transition between the two anchors. Lerping a pitch
            // across the boundary visibly shoots the needle to the top or bottom
            // of the chart at phrase ends: the silent-side sample's pitch value
            // isn't a real continuation of the singer's contour (it's whatever
            // the analyzer last reported when input went quiet — often noise
            // floor or a near-zero MIDI value), and dragging the needle there
            // looks like a sharp jump. Snap to the new flag and pin the needle
            // at the actively-singing side's pitch so the next show resumes
            // from a sensible position.
            if (_vocalPitchPrevSinging != _vocalPitchLatestSinging)
            {
                return _vocalPitchLatestSinging
                    ? (_vocalPitchLatestMidi, true)
                    : (_vocalPitchPrevMidi, false);
            }

            // Combined interpolation + damped prediction.
            //   read ∈ [prevTime, latestTime] → linear interpolation between the two
            //                                   buffered samples (full strength: the
            //                                   read sits between known anchors).
            //   read > latestTime             → forward extrapolation along the
            //                                   prev→latest velocity, with the slope
            //                                   damped (VocalPitchExtrapolateSlopeDamping)
            //                                   and the time cap (VocalPitchMaxExtrapolateSeconds)
            //                                   both applied. Damping keeps the projection
            //                                   from overshooting vibrato peaks; the time
            //                                   cap keeps it from running away if a packet
            //                                   is delayed.
            // isSinging snaps to the latest sample (booleans don't lerp).
            if (currentSongTime <= _vocalPitchLatestTime)
            {
                // Interpolation window — full-strength linear lerp.
                double tLerp = (currentSongTime - _vocalPitchPrevTime) / dt;
                float lerpPitch = (float)(_vocalPitchPrevMidi
                    + (_vocalPitchLatestMidi - _vocalPitchPrevMidi) * tLerp);
                return (lerpPitch, _vocalPitchLatestSinging);
            }

            // Extrapolation: cap the elapsed time past latest, then project with the
            // damped slope. slopePerSec is computed against the actual prev→latest
            // interval (full strength); the damping is applied only to the projected
            // motion past latestTime, so smoothness inside the window is unaffected.
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

            // Local events applied after the sender's snapshot was captured. Re-applied on top
            // of the restored state below to bring the mirror back to its current time.
            var postEvents = _rollback.CollectEventsAfter(snapshotSongTime);

            _engine.RestoreSnapshot(wireSnapshot);

            DropPendingOlderThan(snapshotSongTime);

            _rollback.Clear();
            _rollback.TakeSnapshot(snapshotSongTime, wireSnapshot);

            _scheduler.Rewind(_engine.NoteIndex);

            // The snapshot's authoritative state covers every note up to engine.NoteIndex,
            // so the next sender Hit/Miss transition will reference notes at or beyond
            // that index. Anchor the run-length cursor there so we don't gap-fill into
            // pre-snapshot territory the snapshot already settled.
            _nextExpectedNoteIndex = _engine.NoteIndex;

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
                    "Prediction[sim-snapshot] WIRE SNAPSHOT IS FUTURE-STAMPED relative to local clock: " +
                    "snapTime={0:0.000} engineNow={1:0.000} latestLocalSongTime={2:0.000} skew={3:0.000}s. " +
                    "Subsequent sim.Update calls will short-circuit until localSongTime catches up — if this " +
                    "doesn't recover within a few seconds, the receiver's clock drifted behind the sender.",
                    snapshotSongTime, _engine.CurrentTime, _latestKnownLocalSongTime,
                    _engine.CurrentTime - _latestKnownLocalSongTime);
            }

            // Reset the stall-detection probe — a snapshot just resynced the engine, so
            // any prior "engine ahead of localSongTime" streak is no longer meaningful.
            _consecutiveSkippedUpdates = 0;
            _lastUpdateLogSongTime = double.NegativeInfinity;
        }

        // Captured from the most recent sim.Update / wire event so OnEngineStateSnapshot
        // can compare the snapshot's time against the local clock.
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
            _authoritativeSnapshotSongTime = double.NegativeInfinity;
            _nextExpectedNoteIndex = 0;
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
