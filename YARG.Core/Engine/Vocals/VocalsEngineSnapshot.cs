namespace YARG.Core.Engine.Vocals
{
    public sealed class VocalsEngineSnapshot : EngineSnapshot
    {
        public uint   PhraseTicksTotal;
        public bool   HasPhraseTicksTotal;
        public double PhraseTicksHit;
        public uint   LastSingTick;
        public float  PitchSang;
        public bool   HasSang;

        public int    CarriedVocalNotePhraseIndex;
        public int    CarriedVocalNoteChildIndex;
    }
}
