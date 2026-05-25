namespace YARG.Core.Engine.Prediction
{
    // Non-generic facade over RemotePlayerSimulator<TNoteType> so callers can
    // hold simulators by peer id in a single dictionary.
    public interface IRemotePlayerSimulator
    {
        BaseEngine Engine { get; }

        /// <summary>Drive the simulator forward to <paramref name="localSongTime"/>. The
        /// mirror engine ticks at local song time directly — no transport-delay budget.</summary>
        void Update(double localSongTime);

        /// <summary>Returns true if no rollback was needed, false if rollback fired or the
        /// event was past the rollback window.</summary>
        bool OnNoteMissed(int noteIndex, double currentSongTime);

        bool OnNoteHit(int noteIndex, double currentSongTime);

        /// <summary>Record the sender's actual hit-time offset for a note. The mirror engine
        /// commits hits at <c>note.Time</c>, so its own CurrentTime - note.Time is always ~0;
        /// the sender ships its engine's CurrentTime at HitNote time and the receiver computes
        /// <c>offset = wireHitTime - note.Time</c> to feed the offset histogram with real
        /// player timing.</summary>
        void RecordWireHitOffset(int noteIndex, double wireHitTime);

        /// <summary>Most recent whammy axis value in [0,1]. Visual layers (sustain bar bend,
        /// stem pitch shift) poll this each frame because remote mirrors have no
        /// <c>OnInputQueued</c> path. Defaults to 0.</summary>
        float LatestWhammyValue { get; }

        bool OnStarPowerActivated(double songTime, double currentSongTime);

        /// <summary>Late samples are silently dropped — the next authoritative snapshot
        /// reconciles any resulting SP discrepancy.</summary>
        void OnWhammy(double songTime, float value, double currentSongTime);

        /// <summary>Senders rate-limit (~20 Hz); the simulator stores the latest sample
        /// (and the one before it) so the visual layer can interpolate via
        /// <see cref="GetInterpolatedPitch"/>. pitchMidi matches VocalsEngine.PitchSang.
        /// isSinging disambiguates a valid 0 MIDI value from "no input".</summary>
        void OnVocalPitch(double songTime, float pitchMidi, bool isSinging, double currentSongTime);

        /// <summary>Linear interpolation when the read time falls between the two buffered
        /// samples, or forward extrapolation along the prev->latest velocity when it doesn't.</summary>
        (float pitchMidi, bool isSinging) GetInterpolatedPitch(double currentSongTime);

        bool OnSustainReleased(int sustainNoteIndex, double releaseSongTime, double currentSongTime);

        /// <summary>Routes back through the engine's normal Overstrum() path, so combo break,
        /// sustain drop, SP strip and rock-meter dock all run.</summary>
        bool OnOverstrum(double songTime, double currentSongTime);

        /// <summary>Restore the mirror engine to the sender's exact state at
        /// <paramref name="snapshotSongTime"/>, trim the rollback buffer, and replay local
        /// events that occurred after the snapshot's time. The single drift-reconciliation
        /// mechanism.</summary>
        void OnEngineStateSnapshot(EngineSnapshot wireSnapshot, double snapshotSongTime);

        /// <summary>Song time of the most recent wire snapshot applied. -infinity until the
        /// first snapshot. Used to gate the results-screen transition on every remote peer
        /// having delivered a "final" snapshot at song end.</summary>
        double AuthoritativeSnapshotSongTime { get; }

        void Reset();
    }
}
