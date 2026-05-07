using System.Collections.Generic;
using YARG.Core.Chart;
using GhppAbs = Ghpp.Core.Abstractions;

namespace YARG.Core.Song
{
    /// <summary>
    /// Translates a YARG <see cref="InstrumentDifficulty{TNote}"/> of <see cref="GuitarNote"/>s into
    /// the format expected by Ghpp's <see cref="Ghpp.Core.DifficultyCalculator"/>.
    /// </summary>
    internal static class GhppChartConverter
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyMetadata
            = new Dictionary<string, string>();

        public static GhppAbs.Chart Convert(InstrumentDifficulty<GuitarNote> difficulty, uint resolution)
        {
            var notes = new List<GhppAbs.Note>(difficulty.Notes.Count);

            foreach (var parent in difficulty.Notes)
            {
                var frets = GhppAbs.Frets.None;
                int chordTickLen = 0;
                double chordTimeLen = 0;
                var perFret = new double[5];

                foreach (var sub in parent.AllNotes)
                {
                    if (TryMapFretBit(sub.Fret, out var bit, out var lane))
                    {
                        frets |= bit;
                        perFret[lane] = sub.TimeLength;
                    }
                    // Open notes contribute no fret bits — Frets.None is the open marker.

                    if (sub.TickLength > chordTickLen)
                    {
                        chordTickLen = (int) sub.TickLength;
                    }
                    if (sub.TimeLength > chordTimeLen)
                    {
                        chordTimeLen = sub.TimeLength;
                    }
                }

                var type = parent.Type switch
                {
                    GuitarNoteType.Strum => GhppAbs.NoteType.Strum,
                    GuitarNoteType.Hopo  => GhppAbs.NoteType.Hopo,
                    GuitarNoteType.Tap   => GhppAbs.NoteType.Tap,
                    _                    => GhppAbs.NoteType.Strum,
                };

                notes.Add(new GhppAbs.Note(
                    timeTicks: (int) parent.Tick,
                    timeSeconds: parent.Time,
                    frets: frets,
                    sustainTicks: chordTickLen,
                    sustainSeconds: chordTimeLen,
                    type: type,
                    perFretSustainSeconds: perFret));
            }

            return new GhppAbs.Chart(EmptyMetadata, (int) resolution, notes);
        }

        private static bool TryMapFretBit(int fret, out GhppAbs.Frets bit, out int lane)
        {
            switch ((FiveFretGuitarFret) fret)
            {
                case FiveFretGuitarFret.Green:  bit = GhppAbs.Frets.Green;  lane = 0; return true;
                case FiveFretGuitarFret.Red:    bit = GhppAbs.Frets.Red;    lane = 1; return true;
                case FiveFretGuitarFret.Yellow: bit = GhppAbs.Frets.Yellow; lane = 2; return true;
                case FiveFretGuitarFret.Blue:   bit = GhppAbs.Frets.Blue;   lane = 3; return true;
                case FiveFretGuitarFret.Orange: bit = GhppAbs.Frets.Orange; lane = 4; return true;
                default:
                    bit = GhppAbs.Frets.None;
                    lane = -1;
                    return false;
            }
        }
    }
}
