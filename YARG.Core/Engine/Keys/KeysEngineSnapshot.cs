namespace YARG.Core.Engine.Keys
{
    // Covers both 5-Lane and Pro Keys. Per-instrument stats ride along in
    // <see cref="EngineSnapshot.Stats"/> as <see cref="KeysStats"/>.
    public sealed class KeysEngineSnapshot : EngineSnapshot
    {
        public int KeyMask;
        public int PreviousKeyMask;

        public bool   ChordStaggerTimerActive;
        public double ChordStaggerTimerStartTime;

        // Pro-keys only; FiveLaneKeys leaves zero/null.
        public bool   FatFingerTimerActive;
        public double FatFingerTimerStartTime;
        public int    FatFingerKey;   // -1 when null
        public int    FatFingerNoteIndex; // -1 when null
    }
}
