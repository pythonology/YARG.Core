namespace YARG.Core.Engine.Guitar
{
    // Adds GuitarEngine's button-mask, leniency-timer and ghost/fret state. 5-fret and
    // pro-guitar currently share enough that one snapshot type covers both.
    public sealed class GuitarEngineSnapshot : EngineSnapshot
    {
        public byte   EffectiveButtonMask;
        public ushort InputButtonMask;
        public byte   LastButtonMask;
        public bool   StandardButtonHeld;

        public bool HasFretted;
        public bool HasStrummed;
        public bool HasTapped;
        public bool HasWhammied;
        public bool IsFretPress;

        public bool WasNoteGhosted;

        public bool   HopoLeniencyTimerActive;
        public double HopoLeniencyTimerStartTime;

        public bool   StrumLeniencyTimerActive;
        public double StrumLeniencyTimerStartTime;

        public double FrontEndExpireTime;
    }
}
