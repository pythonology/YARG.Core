using System.Collections.Generic;
using YARG.Core.Input;

namespace YARG.Core.Engine
{
    // Snapshot of mutable engine state, sufficient to restore + replay forward. Each engine
    // subclass extends this with its instrument-specific state; capture/restore lives on the
    // engine (CreateSnapshot / RestoreSnapshot).
    //
    // Notes are shared mutable objects (engines flip WasHit/WasMissed on the note). Rather than
    // deep-cloning every note, we capture a flag-state window around the play position. Notes
    // outside the window aren't touched during the rollback budget (250 ms default).
    public abstract class EngineSnapshot
    {
        public int    NoteIndex;
        public double CurrentTime;
        public double LastUpdateTime;
        public double LastQueuedInputTime;

        public uint CurrentTick;
        public uint LastTick;
        public uint FirstWhammyTick;

        public int CurrentSoloIndex;
        public int CurrentStarIndex;
        public int CurrentWaitCountdownIndex;
        public int CurrentCodaIndex;

        public bool IsSoloActive;
        public bool IsCodaActive;
        public bool CodaHasStarted;
        public bool IsWaitCountdownActive;
        public bool IsStarPowerInputActive;

        public int BaseScore;
        public int BaseNoteScore;

        public bool   WasSpSustainActive;
        public uint   StarPowerTickPosition;
        public uint   PreviousStarPowerTickPosition;
        public uint   StarPowerTickActivationPosition;
        public uint   StarPowerTickEndPosition;
        public double StarPowerActivationTime;
        public double StarPowerEndTime;
        public double BaseTimeInStarPower;

        public bool   StarPowerWhammyTimerActive;
        public double StarPowerWhammyTimerStartTime;

        public uint TotalLanes;
        public uint CurrentLaneIndex;
        public int  RequiredLaneNote;
        public int  NextTrillNote;
        public double LaneExpireTime;

        // Usually empty (we snapshot at quiet moments) but captured defensively.
        public GameInput[]       PendingInputs   = System.Array.Empty<GameInput>();
        public ScheduledUpdate[] PendingUpdates  = System.Array.Empty<ScheduledUpdate>();

        public readonly struct ScheduledUpdate
        {
            public ScheduledUpdate(double time, string reason)
            {
                Time = time;
                Reason = reason;
            }

            public readonly double Time;
            public readonly string Reason;
        }

        // Per-note flag slice, indexed parallel to [NoteFlagStartIndex, +NoteFlags.Length).
        public int    NoteFlagStartIndex;
        public byte[] NoteFlags = System.Array.Empty<byte>();

        public int[] SoloNotesHit = System.Array.Empty<int>();
        public int[] SoloBonus    = System.Array.Empty<int>();

        // Captured by chart-index to avoid pinning typed note references.
        public SustainSnapshot[] ActiveSustains = System.Array.Empty<SustainSnapshot>();

        public double WhammyTicksRemainder;

        // Subclasses populate with a deep clone (via matching TEngineStats copy ctor); restored
        // via CopyFrom against the live engine because engines hold their stats by readonly ref.
        public BaseStats? Stats;

        [System.Flags]
        public enum NoteFlag : byte
        {
            None       = 0,
            WasHit     = 1 << 0,
            WasMissed  = 1 << 1,
            WasFullyHit = 1 << 2,
        }
    }

    /// <summary>One entry in the engine's active-sustain list. Identifies the note by
    /// chart-list index so the snapshot stays decoupled from the engine's type parameter.</summary>
    public readonly struct SustainSnapshot
    {
        public SustainSnapshot(int noteIndex, uint baseTick, double baseScore,
            bool hasFinishedScoring, bool isLeniencyHeld, double leniencyDropTime)
        {
            NoteIndex = noteIndex;
            BaseTick = baseTick;
            BaseScore = baseScore;
            HasFinishedScoring = hasFinishedScoring;
            IsLeniencyHeld = isLeniencyHeld;
            LeniencyDropTime = leniencyDropTime;
        }

        public readonly int    NoteIndex;
        public readonly uint   BaseTick;
        public readonly double BaseScore;
        public readonly bool   HasFinishedScoring;
        public readonly bool   IsLeniencyHeld;
        public readonly double LeniencyDropTime;
    }
}
