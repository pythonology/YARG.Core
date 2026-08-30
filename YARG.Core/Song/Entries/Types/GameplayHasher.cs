using System;
using System.IO;

namespace YARG.Core.Song
{
    /// <summary>
    /// Computes a hash over the *parsed* chart data (<see cref="Chart.SongChart"/>) rather
    /// than raw source-file bytes.
    ///
    /// This is intentionally kept separate from <see cref="SongEntry.Hash"/>:
    ///   - SongEntry.Hash stays a byte-exact hash of the source file(s), used for the
    ///     song cache, leaderboards, and anywhere exactness matters.
    ///   - GameplayHash is computed lazily (only when actually needed, e.g. right after
    ///     a multiplayer session calls LoadChart()) and is only ever used to widen
    ///     multiplayer song matching to charts that are gameplay-identical but not
    ///     byte-identical.
    ///
    /// Neither SongEntry's fields, its cache serialization, nor the existing scan-time
    /// hash call sites are touched by this.
    ///
    /// A chart matches under this hash if it's the same song approximately: same notes,
    /// same timing, same difficulty content. It does NOT require byte-for-byte gameplay
    /// equivalence - the goal is "close enough that two players don't look out of sync
    /// to each other," not exactness. Known tolerances:
    ///   - MIDI velocity magnitude (engines only check on/off, see MidiFiveFretPreparser)
    ///   - notes outside an instrument's valid pitch range (never parsed at all)
    ///   - legacy RB1/RB2 versus-phrase markers (dropped since RB3, unused by YARG)
    ///   - sustain tails shorter than a fraction of a beat (quantized away below)
    ///   - non-gameplay tracks (EVENTS/VENUE/track order) - simply never read here
    ///   - Strum vs. HOPO on guitar/pro guitar (see NormalizeType) - this DOES change
    ///     hit mechanics, unlike everything else on this list, but not what's rendered
    ///     on the highway, so it's tolerated deliberately for lenient matching rather
    ///     than proven invisible to the engine like the rest of this list.
    /// </summary>
    public static class GameplayHasher
    {
        public const int HASH_VERSION = 26_08_29_00; // bump: strum/HOPO tolerated for lenient matching (see NormalizeType)
        // Every chart is rescaled to this PPQ before hashing, so two files authored
        // at different MIDI resolutions (e.g. 480 vs 960) still match as long as the
        // underlying timing is the same. SongChart.Resolution is read straight from
        // the source file and is NOT normalized upstream, so this has to happen here.
        private const uint CANONICAL_RESOLUTION = 480;

        // Sustain lengths are bucketed to this many canonical ticks (a 16th note at
        // CANONICAL_RESOLUTION) rather than hashed exactly. This isn't an arbitrary
        // safety margin - it's sized from real data: comparing a from-scratch CON
        // extraction against Onyx's for the same song showed ~90% of held notes had
        // their tail trimmed by exactly 60 ticks (a 32nd note at 480 PPQ) by one tool
        // and not the other, with zero effect on tap/sustain classification at
        // YARG's default SustainCutoffThreshold (resolution / 3).
        //
        // A grain equal to the trim itself does NOT absorb it: with floor bucketing,
        // sustain / GRAIN, a value of exactly one grain (e.g. 120) and one grain minus
        // the trim (e.g. 60) fall in adjacent buckets by construction - they can never
        // collide. The grain has to be strictly bigger than the trim, and bucketing has
        // to round to the nearest multiple (not floor) so each bucket gets roughly
        // symmetric tolerance around its center instead of zero tolerance on one edge.
        // A 16th note (twice the observed 32nd-note trim) verified clean against real
        // CON/Onyx pairs for this song - every previously-mismatched sustain now lands
        // in the same bucket - while still telling apart sustains that differ by a
        // musically meaningful amount. If a future extractor trims by something bigger
        // than this, re-derive the grain the same way: diff real CON/Onyx sustains for
        // a song, and pick a grain comfortably larger than the largest systematic skew.
        private const uint SUSTAIN_QUANTIZE = CANONICAL_RESOLUTION / 4;

        private static readonly Difficulty[] ORDERED_DIFFICULTIES =
        {
            Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.Expert,
        };

        // Strum and HOPO render identically on the highway - only the hit mechanic
        // differs (strum required vs. hammer-on/pull-off allowed). For lenient
        // multiplayer matching that's tolerable: two players on differently-encoded
        // rips of the same song still see the same notes go by. Tap is kept distinct -
        // it renders differently (diamond notes) and would look out of place if
        // collapsed.
        //
        // This matters more than a rounding error: on a real CON/Onyx pair for this
        // song, 98% of expert guitar chords differed in resolved Strum/HOPO status,
        // because one rip encoded its force-flags on non-standard MIDI pitches that
        // get silently dropped by the parser (see class doc). Collapsing here is what
        // makes that pair - and others like it - match at all.
        private static byte NormalizeType(Chart.GuitarNoteType type)
        {
            if (type == Chart.GuitarNoteType.Strum || type == Chart.GuitarNoteType.Hopo)
            {
                return (byte) Chart.GuitarNoteType.Hopo;
            }

            return (byte) type;
        }

        public static HashWrapper Hash(Chart.SongChart chart)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            double scale = CANONICAL_RESOLUTION / (double) chart.Resolution;

            // Fixed, deterministic order - matches SongChart's own enumeration order,
            // not file/track order, so track shuffling between extractors is a no-op.
            foreach (var track in chart.FiveFretTracks)
            {
                WriteInstrumentTrack(writer, track, scale, WriteGuitarNote);
            }

            foreach (var track in chart.SixFretTracks)
            {
                WriteInstrumentTrack(writer, track, scale, WriteGuitarNote);
            }

            foreach (var track in chart.VocalsTracks)
            {
                WriteVocalsTrack(writer, track, scale);
            }

            foreach (var track in chart.DrumsTracks)
            {
                WriteInstrumentTrack(writer, track, scale, WriteDrumsNote);
            }

            WriteInstrumentTrack(writer, chart.EliteDrums, scale, WriteEliteDrumsNote);

            foreach (var track in chart.ProGuitarTracks)
            {
                WriteInstrumentTrack(writer, track, scale, WriteProGuitarNote);
            }

            WriteInstrumentTrack(writer, chart.ProKeys, scale, WriteProKeysNote);

            return HashWrapper.Hash(stream.ToArray());
        }

        // Every non-vocals instrument shares the same track shape: an optional
        // per-difficulty note list. This is the one place that shape gets walked -
        // each instrument only supplies how to write a single note via writeNote.
        private static void WriteInstrumentTrack<TNote>(BinaryWriter writer, Chart.InstrumentTrack<TNote> track,
            double scale, Action<BinaryWriter, TNote, double> writeNote)
            where TNote : Chart.Note<TNote>
        {
            if (track.IsEmpty)
            {
                return;
            }

            writer.Write((byte) track.Instrument);

            foreach (var difficulty in ORDERED_DIFFICULTIES)
            {
                if (!track.TryGetDifficulty(difficulty, out var diff) || diff.Notes.Count == 0)
                {
                    continue;
                }

                writer.Write((byte) difficulty);
                writer.Write(diff.Notes.Count);

                foreach (var note in diff.Notes)
                {
                    writeNote(writer, note, scale);
                }
            }
        }

        // Rounds to the nearest bucket rather than flooring, so tolerance is spread
        // symmetrically (+/- half a grain) instead of only below each multiple.
        // Integer-only, no floating point: adding half a grain before dividing is the
        // standard round-half-up trick and matches Math.Round's default
        // MidpointRounding. See SUSTAIN_QUANTIZE for how the grain size was picked.
        // Shared by every note type with a meaningful sustain (guitar, vocals, pro
        // guitar, pro keys) - drum-family notes don't call this, since their
        // TickLength is always 0 by construction.
        private static void WriteQuantizedSustain(BinaryWriter writer, uint tickLength, double scale)
        {
            uint sustain = (uint) Math.Round(tickLength * scale);
            writer.Write((sustain + SUSTAIN_QUANTIZE / 2) / SUSTAIN_QUANTIZE);
        }

        private static void WriteGuitarNote(BinaryWriter writer, Chart.GuitarNote note, double scale)
        {
            writer.Write((uint) Math.Round(note.Tick * scale));
            writer.Write((byte) note.NoteMask);
            writer.Write(NormalizeType(note.Type)); // strum / hopo / tap - see DIAGNOSTIC_IGNORE_STRUM_HOPO above
            WriteQuantizedSustain(writer, note.TickLength, scale);
        }

        private static void WriteVocalsTrack(BinaryWriter writer, Chart.VocalsTrack track, double scale)
        {
            if (track.IsEmpty)
            {
                return;
            }

            writer.Write((byte) track.Instrument);

            // track.Parts is already voice-ordered (solo vocals: 1 part; harmony:
            // HARM1/HARM2/HARM3 at indices 0/1/2) - iterating it directly gives a
            // fixed, deterministic order without sorting by VocalNote.HarmonyPart.
            foreach (var part in track.Parts)
            {
                // NOTE: deliberately NOT using VocalsPart.IsEmpty here - that check
                // ignores StaticLyricPhrases entirely, and this method used to key its
                // own skip logic off it too. That meant any part carrying only
                // unpitched/static lyric data (no NotePhrases, no OtherPhrases, no
                // TextEvents) got silently skipped, contributing zero bytes to the
                // hash. For a song whose only content is a lyrics-only vocals chart -
                // no five/six-fret tracks either - that leaves the entire stream
                // empty, so every such song collapsed to the same hash. Skip only when
                // there is truly nothing (of the two phrase lists this method writes)
                // to write.
                if (part.NotePhrases.Count == 0 && part.StaticLyricPhrases.Count == 0)
                {
                    continue;
                }

                writer.Write(part.NotePhrases.Count);
                foreach (var phrase in part.NotePhrases)
                {
                    WriteVocalsPhrase(writer, phrase, scale);
                }

                // Static lyric phrases (lyrics displayed without pitch tracking) carry
                // no gameplay-relevant pitch data, but they're still real chart content
                // that differs from song to song - has to be included or two different
                // lyrics-only songs hash identically.
                writer.Write(part.StaticLyricPhrases.Count);
                foreach (var phrase in part.StaticLyricPhrases)
                {
                    WriteVocalsPhrase(writer, phrase, scale);
                }
            }
        }

        private static void WriteVocalsPhrase(BinaryWriter writer, Chart.VocalsPhrase phrase, double scale)
        {
            var parent = phrase.PhraseParentNote;

            writer.Write((uint) Math.Round(parent.Tick * scale));
            writer.Write(parent.IsPercussionPhrase);
            writer.Write(parent.ChildNotes.Count);

            foreach (var note in parent.ChildNotes)
            {
                writer.Write((uint) Math.Round(note.Tick * scale));
                writer.Write(note.Type == Chart.VocalNoteType.Percussion);
                writer.Write(NormalizePitch(note.Pitch));
                WriteQuantizedSustain(writer, note.TickLength, scale);
            }
        }

        private static void WriteDrumsNote(BinaryWriter writer, Chart.DrumNote note, double scale)
        {
            writer.Write((uint) Math.Round(note.Tick * scale));
            // Drum "chords" (e.g. snare+hihat hit together) are stored as
            // ChildNotes rather than a NoteMask like guitar, since each
            // component can carry its own dynamics/kick-lane flags - AllNotes
            // walks the parent plus every child in a fixed order.
            writer.Write(note.ChildNotes.Count + 1);

            foreach (var n in note.AllNotes)
            {
                writer.Write((byte) n.Pad);
                writer.Write((byte) n.Type); // Neutral / Ghost / Accent
                writer.Write(n.IsDoubleKick);
                // Only the kick-lane bits are gameplay-relevant here; StarPowerActivator
                // is already implied by the phrase-level StarPower flags on other tracks.
                writer.Write((byte) (n.DrumFlags &
                    (Chart.DrumNoteFlags.KickLane | Chart.DrumNoteFlags.KickLaneStart |
                        Chart.DrumNoteFlags.KickLaneEnd)));
            }
        }

        private static void WriteEliteDrumsNote(BinaryWriter writer, Chart.EliteDrumNote note, double scale)
        {
            writer.Write((uint) Math.Round(note.Tick * scale));
            writer.Write(note.ChildNotes.Count + 1);

            foreach (var n in note.AllNotes)
            {
                writer.Write((byte) n.Pad);
                writer.Write((byte) n.Dynamics);
                writer.Write((byte) n.HatState);
                writer.Write((byte) n.HatPedalType);
                writer.Write(n.IsFlam);
                writer.Write(n.IsDoubleKick);
            }
        }

        private static void WriteProGuitarNote(BinaryWriter writer, Chart.ProGuitarNote note, double scale)
        {
            writer.Write((uint) Math.Round(note.Tick * scale));
            // String+fret pairs are ChildNotes (unlike five-fret's single NoteMask),
            // since each string in a chord needs its own fret number.
            writer.Write(note.ChildNotes.Count + 1);

            foreach (var n in note.AllNotes)
            {
                writer.Write((byte) n.String);
                writer.Write((byte) n.Fret);
                writer.Write(NormalizeProGuitarType(n.Type));
                writer.Write(n.IsMuted);
                WriteQuantizedSustain(writer, n.TickLength, scale);
            }
        }

        private static void WriteProKeysNote(BinaryWriter writer, Chart.ProKeysNote note, double scale)
        {
            writer.Write((uint) Math.Round(note.Tick * scale));
            writer.Write(note.NoteMask);
            writer.Write(note.IsGlissando);
            WriteQuantizedSustain(writer, note.TickLength, scale);
        }

        // Same tolerance and rationale as NormalizeType, applied to pro guitar's
        // parallel enum.
        private static byte NormalizeProGuitarType(Chart.ProGuitarNoteType type)
        {
            if (type == Chart.ProGuitarNoteType.Strum || type == Chart.ProGuitarNoteType.Hopo)
            {
                return (byte) Chart.ProGuitarNoteType.Hopo;
            }

            return (byte) type;
        }

        // Pitch is a float, but every real chart stores whole MIDI note numbers (or -1
        // for unpitched/percussion) - round defensively instead of trusting that, then
        // narrow to sbyte since MIDI pitches fit in 0-127 and -1 is the only negative.
        private static sbyte NormalizePitch(float pitch)
        {
            if (pitch < 0) return -1;
            return (sbyte) Math.Round(pitch);
        }
    }
}
