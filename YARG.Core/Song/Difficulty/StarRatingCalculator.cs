using System;
using System.Collections.Generic;
using Ghpp.Core;
using Ghpp.Core.Aggregation;
using Ghpp.Core.Models;
using YARG.Core.Chart;
using YARG.Core.Logging;

namespace YARG.Core.Song
{
    /// <summary>
    /// Runs the Ghpp difficulty calculator against every supported track/difficulty
    /// of a <see cref="SongChart"/> and packs the results into a <see cref="StarRatings"/>.
    /// Also offers an on-demand entry point for callers that need the full per-curve
    /// time-series (e.g. the practice-mode difficulty visualizer).
    /// </summary>
    public static class StarRatingCalculator
    {
        // Ghpp is calibrated for 5-fret GRYBO charts. Six-fret, drums, vocals, etc. are skipped.
        private static readonly Instrument[] FiveFretInstruments =
        {
            Instrument.FiveFretGuitar,
            Instrument.FiveFretBass,
            Instrument.FiveFretRhythm,
            Instrument.FiveFretCoopGuitar,
            Instrument.Keys,
        };

        private static readonly Difficulty[] DifficultiesToScore =
        {
            Difficulty.Easy,
            Difficulty.Medium,
            Difficulty.Hard,
            Difficulty.Expert,
            Difficulty.ExpertPlus,
        };

        public static StarRatings Compute(SongChart songChart)
        {
            var ratings = new StarRatings();
            if (songChart == null)
            {
                return ratings;
            }

            foreach (var instrument in FiveFretInstruments)
            {
                var track = songChart.GetFiveFretTrack(instrument);
                if (track.IsEmpty)
                {
                    continue;
                }

                foreach (var diff in DifficultiesToScore)
                {
                    if (!track.TryGetDifficulty(diff, out var diffTrack) || diffTrack.Notes.Count == 0)
                    {
                        continue;
                    }

                    try
                    {
                        var ghppChart = GhppChartConverter.Convert(diffTrack, songChart.Resolution);
                        var report = DifficultyCalculator.Calculate(ghppChart);
                        ratings.Set(instrument, diff, (float) report.StarRating);
                    }
                    catch (Exception ex)
                    {
                        YargLogger.LogException(ex,
                            $"Failed to compute Ghpp star rating for {instrument} {diff}");
                    }
                }
            }

            return ratings;
        }

        /// <summary>
        /// Re-runs the Ghpp difficulty calculator against a single (instrument, difficulty)
        /// of the given chart and returns the full time-series report. Returns null if
        /// the instrument is unsupported, the track is empty, or computation fails.
        /// </summary>
        /// <remarks>
        /// Designed for on-demand use at gameplay-load time (practice-mode visualizer).
        /// Not cached — call once and hold the result.
        /// </remarks>
        public static DifficultyAnalysis ComputeAnalysis(
            SongChart songChart, Instrument instrument, Difficulty difficulty)
        {
            if (songChart == null)
            {
                return null;
            }

            if (Array.IndexOf(FiveFretInstruments, instrument) < 0 ||
                Array.IndexOf(DifficultiesToScore, difficulty) < 0)
            {
                return null;
            }

            var track = songChart.GetFiveFretTrack(instrument);
            if (track.IsEmpty)
            {
                return null;
            }

            if (!track.TryGetDifficulty(difficulty, out var diffTrack) || diffTrack.Notes.Count == 0)
            {
                return null;
            }

            try
            {
                var ghppChart = GhppChartConverter.Convert(diffTrack, songChart.Resolution);
                var report = DifficultyCalculator.Calculate(ghppChart);
                return ToAnalysis(report);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex,
                    $"Failed to compute Ghpp difficulty analysis for {instrument} {difficulty}");
                return null;
            }
        }

        private static DifficultyAnalysis ToAnalysis(DifficultyReport report)
        {
            return new DifficultyAnalysis(
                ToSamples(report.Curve),
                ToSamples(report.FretComplexityCurve),
                ToSamples(report.StrumComplexityCurve),
                ToSamples(report.SustainComplexityCurve),
                (float) report.StarRating,
                ToFretChunks(report.FretChunks),
                report.NoteIsAnchor ?? Array.Empty<bool>());
        }

        private static DifficultyFretChunk[] ToFretChunks(IReadOnlyList<FretChunkRecord> chunks)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return Array.Empty<DifficultyFretChunk>();
            }

            var result = new DifficultyFretChunk[chunks.Count];
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                result[i] = new DifficultyFretChunk(
                    c.StartNoteIndex,
                    c.EndNoteIndex,
                    ToYargShape(c.Shape),
                    c.InRepeat);
            }
            return result;
        }

        private static DifficultyChunkShape ToYargShape(FretChunkShape shape)
        {
            switch (shape)
            {
                case FretChunkShape.Trill:   return DifficultyChunkShape.Trill;
                case FretChunkShape.RollOn:  return DifficultyChunkShape.RollOn;
                case FretChunkShape.RollOff: return DifficultyChunkShape.RollOff;
                case FretChunkShape.Zig:     return DifficultyChunkShape.Zig;
                case FretChunkShape.Held:    return DifficultyChunkShape.Held;
                default:                     return DifficultyChunkShape.Free;
            }
        }

        private static DifficultyCurveSamples ToSamples(DifficultyCurve curve)
        {
            var src = curve.Values;
            var dst = src == null ? Array.Empty<float>() : new float[src.Length];
            if (src != null)
            {
                for (int i = 0; i < src.Length; i++)
                {
                    dst[i] = (float) src[i];
                }
            }
            return new DifficultyCurveSamples(dst, curve.SampleRateHz, curve.StartTimeSeconds);
        }
    }
}
