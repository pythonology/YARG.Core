using System;

namespace YARG.Core.Song
{
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
            float starRating)
        {
            Composite = composite;
            Fret = fret;
            Strum = strum;
            Sustain = sustain;
            StarRating = starRating;
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
