using System;
using System.Collections.Generic;

namespace YARG.Core.Song
{
    /// <summary>
    /// Classification of a fret-hand motion chunk. Mirrors Ghpp's
    /// <c>FretChunkShape</c> for visualization-side consumers that don't reference Ghpp directly.
    /// </summary>
    public enum DifficultyChunkShape : byte
    {
        /// <summary>Single isolated note that does not belong to any recognized motion chunk.</summary>
        Free,
        /// <summary>Anchor alternates with one or more upper targets.</summary>
        Trill,
        /// <summary>Strictly ascending top-frets, optional skips.</summary>
        RollOn,
        /// <summary>Strictly descending top-frets, optional skips.</summary>
        RollOff,
        /// <summary>Exactly one direction reversal.</summary>
        Zig,
        /// <summary>
        /// Run of notes playable by holding a single union chord — every note
        /// fires off the held set with no fret motion required.
        /// </summary>
        Held,
    }

    /// <summary>
    /// A single fret-hand motion chunk produced by the Ghpp FretComplexity Bar.
    /// Indices reference the YARG <c>InstrumentDifficulty.Notes</c> list 1-1
    /// (the converter is order-preserving). <see cref="InRepeat"/> reflects
    /// the K-period repeat detector at the time the chunk was emitted.
    /// </summary>
    public readonly struct DifficultyFretChunk
    {
        public DifficultyFretChunk(int startNoteIndex, int endNoteIndex, DifficultyChunkShape shape, bool inRepeat)
        {
            StartNoteIndex = startNoteIndex;
            EndNoteIndex = endNoteIndex;
            Shape = shape;
            InRepeat = inRepeat;
        }

        public int StartNoteIndex { get; }
        public int EndNoteIndex { get; }
        public DifficultyChunkShape Shape { get; }
        public bool InRepeat { get; }
    }

    /// <summary>
    /// YARG-side wrapper around a Ghpp difficulty calculation result. Exposes the three
    /// per-Bar curves (fret/strum/sustain) and the composite curve as float arrays so
    /// callers (e.g. the gameplay visualizer) don't need to reference Ghpp directly.
    /// </summary>
    public sealed class DifficultyAnalysis
    {
        public DifficultyAnalysis(
            DifficultyCurveSamples composite,
            DifficultyCurveSamples fret,
            DifficultyCurveSamples strum,
            DifficultyCurveSamples sustain,
            float starRating,
            IReadOnlyList<DifficultyFretChunk> fretChunks,
            IReadOnlyList<bool> noteIsAnchor)
        {
            Composite = composite;
            Fret = fret;
            Strum = strum;
            Sustain = sustain;
            StarRating = starRating;
            FretChunks = fretChunks ?? Array.Empty<DifficultyFretChunk>();
            NoteIsAnchor = noteIsAnchor ?? Array.Empty<bool>();
        }

        /// <summary>Combined Lp-norm curve: the headline difficulty over time.</summary>
        public DifficultyCurveSamples Composite { get; }

        /// <summary>Fingering-complexity curve (left-hand load).</summary>
        public DifficultyCurveSamples Fret { get; }

        /// <summary>Strumming-complexity curve (right-hand load).</summary>
        public DifficultyCurveSamples Strum { get; }

        /// <summary>Sustain-complexity curve (release/overlap load).</summary>
        public DifficultyCurveSamples Sustain { get; }

        /// <summary>The single aggregate star rating, same value as in <see cref="StarRatings"/>.</summary>
        public float StarRating { get; }

        /// <summary>
        /// Fret-hand motion chunks classifying every note in the chart. Empty
        /// if the underlying calculator did not produce chunk data. Indices into
        /// <c>StartNoteIndex</c>/<c>EndNoteIndex</c> address the same notes list
        /// the visualizer iterates.
        /// </summary>
        public IReadOnlyList<DifficultyFretChunk> FretChunks { get; }

        /// <summary>
        /// Per-note flag, indexed against the same notes list as <c>FretChunks</c>:
        /// <c>true</c> when the note sits at its enclosing chunk's lowest fret.
        /// Visualizers paint anchors gray to distinguish them from the
        /// pattern's "target" notes.
        /// </summary>
        public IReadOnlyList<bool> NoteIsAnchor { get; }
    }

    /// <summary>
    /// A time-series of difficulty samples sampled at a fixed rate.
    /// <c>Values[i]</c> corresponds to time <c>StartTimeSeconds + i / SampleRateHz</c>.
    /// </summary>
    public readonly struct DifficultyCurveSamples
    {
        public DifficultyCurveSamples(float[] values, double sampleRateHz, double startTimeSeconds)
        {
            Values = values ?? throw new ArgumentNullException(nameof(values));
            SampleRateHz = sampleRateHz;
            StartTimeSeconds = startTimeSeconds;
        }

        public float[] Values { get; }
        public double SampleRateHz { get; }
        public double StartTimeSeconds { get; }

        public bool IsEmpty => Values == null || Values.Length == 0;

        public double TimeAt(int sampleIndex)
        {
            return StartTimeSeconds + sampleIndex / SampleRateHz;
        }

        /// <summary>End time of the last sample, or <see cref="StartTimeSeconds"/> if empty.</summary>
        public double EndTimeSeconds =>
            Values == null || Values.Length == 0
                ? StartTimeSeconds
                : StartTimeSeconds + (Values.Length - 1) / SampleRateHz;
    }
}
