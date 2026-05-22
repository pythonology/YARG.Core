using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core.Chart;
using YARG.Core.Logging;

namespace YARG.Core.Engine.Vocals
{
    public abstract class VocalsEngine :
        BaseEngine<VocalNote, VocalsEngineParameters, VocalsStats>
    {
        protected const int POINTS_PER_PERCUSSION = 100;

        protected VocalNote? CarriedVocalNote;

        public delegate void TargetNoteChangeEvent(VocalNote targetNote);

        public delegate void PhraseHitEvent(double hitPercentAfterParams, bool fullPoints, bool isLastPhrase);

        public TargetNoteChangeEvent? OnTargetNoteChanged;

        public Action<bool>? OnSing;
        public Action<bool>? OnHit;

        public PhraseHitEvent? OnPhraseHit;

        /// <summary>
        /// Whether or not the player/bot has hit their mic in the current update.
        /// </summary>
        protected bool HasHit;

        /// <summary>
        /// Whether or not the player/bot sang in the current update.
        /// </summary>
        protected bool HasSang;

        /// <summary>
        /// The float value for the last pitch sang (as a MIDI note).
        /// </summary>
        public float PitchSang { get; protected set; }

        /// <summary>
        /// The amount of ticks in the current phrase.
        /// </summary>
        public uint? PhraseTicksTotal { get; protected set; }

        /// <summary>
        /// The amount of ticks hit in the current phrase.
        /// This is a decimal since you can get fractions of a point for singing slightly off.
        /// </summary>
        public double PhraseTicksHit { get; protected set; }

        /// <summary>
        /// The last tick where there was a successful sing input.
        /// </summary>
        public uint LastSingTick { get; protected set; }

        protected VocalsEngine(InstrumentDifficulty<VocalNote> chart, SyncTrack syncTrack,
            VocalsEngineParameters engineParameters, bool isBot)
            : base(chart, syncTrack, engineParameters, false, isBot)
        {
        }

        public override void Reset(bool keepCurrentButtons = false)
        {
            HasSang = false;
            PitchSang = 0f;

            PhraseTicksTotal = null;
            PhraseTicksHit = 0;

            LastSingTick = 0;

            base.Reset(keepCurrentButtons);
        }

        public void BuildCountdownsFromSelectedPart()
        {
            // Vocals selected, build countdowns from solo vocals line only
            GetWaitCountdowns(Notes);
        }

        public void BuildCountdownsFromAllParts(List<VocalsPart> allParts)
        {
            // Get notes from all available vocals parts
            var allNotes = new List<VocalNote>();

            for (int p = 0; p < allParts.Count; p++)
            {
                allNotes.AddRange(allParts[p].CloneAsInstrumentDifficulty().Notes);
            }

            if (allParts.Count > 1)
            {
                // Sort combined list by Note time
                allNotes.Sort((a, b) => (int) (a.Tick - b.Tick));
            }

            GetWaitCountdowns(allNotes);
        }

        protected override void GenerateQueuedUpdates(double nextTime)
        {
            base.GenerateQueuedUpdates(nextTime);
            var previousTime = CurrentTime;

            // For bots, queue up updates every approximate vocal input frame to simulate
            // a stream of inputs. Make sure that the previous time has been properly set.
            if (IsBot && previousTime > 0.0)
            {
                double timeForFrame = 1.0 / EngineParameters.ApproximateVocalFps;
                int nextUpdateIndex = (int) Math.Floor(previousTime / timeForFrame) + 1;
                double nextUpdateTime = nextUpdateIndex * EngineParameters.ApproximateVocalFps;

                for (double time = nextUpdateTime; time < nextTime; time += timeForFrame)
                {
                    QueueUpdateTime(time, "Bot Input");
                }
            }
        }

        protected override void HitNote(VocalNote note)
        {
            note.SetHitState(true, false);

            if (note.IsPercussion)
            {
                AddScore(note);
                OnNoteHit?.Invoke(NoteIndex, note);
                // Percussion resolves per-note.
                OnSyncNoteHit?.Invoke(NoteIndex);
            }
            else
            {
                if (note.IsStarPower)
                {
                    AwardStarPower(note);
                    EngineStats.StarPowerPhrasesHit++;
                }

                if (note.IsSoloStart)
                {
                    StartSolo();
                }

                if (IsSoloActive)
                {
                    Solos[CurrentSoloIndex].NotesHit++;
                }

                if (note.IsSoloEnd)
                {
                    EndSolo();
                }

                // If there aren't any ticks in the phrase, then don't add
                // any score or update the multiplier.
                var ticks = GetTicksInPhrase(note);
                if (ticks != 0)
                {
                    IncrementCombo();

                    AddScore(note);

                    UpdateMultiplier();
                }

                // No matter what, we still wanna count this as a phrase hit though
                if (IsRemoteMirror) EngineStats.IncrementNotesHit(note);
                else                EngineStats.IncrementNotesHit(note, CurrentTime);

                OnNoteHit?.Invoke(NoteIndex, note);

                // Phrases resolve at most once per NoteIndex — fire unconditionally.
                OnSyncNoteHit?.Invoke(NoteIndex);

                // I want to call base.HitNote here, but I have no idea how vocals handles hit state so I'm scared to
                NoteIndex++;
            }
        }

        protected override void MissNote(VocalNote note)
        {
            if (note.IsPercussion)
            {
                note.SetMissState(true, false);
                OnNoteMissed?.Invoke(NoteIndex, note);
                OnSyncNoteMissed?.Invoke(NoteIndex);
            }
            else
            {
                MissNote(note, 0);
            }
        }

        protected void MissNote(VocalNote note, double hitPercent)
        {
            note.SetMissState(true, false);

            if (note.IsStarPower)
            {
                StripStarPower(note);
            }

            if (note.IsSoloEnd)
            {
                EndSolo();
            }
            if (note.IsSoloStart)
            {
                StartSolo();
            }

            ResetCombo();

            AddPartialScore(hitPercent);

            UpdateMultiplier();

            OnNoteMissed?.Invoke(NoteIndex, note);

            // Receiver's ForceMiss(noteIndex) routes to MissNote(note, 0).
            OnSyncNoteMissed?.Invoke(NoteIndex);

            // I want to call base.MissNote here, but I have no idea how vocals handles miss state so I'm scared to
            NoteIndex++;
        }

        /// <summary>
        /// Checks if the given vocal note can be hit with the current input
        /// </summary>
        /// <param name="note">The note to attempt to hit.</param>
        /// <param name="hitPercent">The hit percent of the note (0 to 1).</param>
        protected abstract bool CanVocalNoteBeHit(VocalNote note, out float hitPercent);

        /// <returns>
        /// Gets the amount of ticks in the phrase.
        /// </returns>
        protected uint GetTicksInPhrase(VocalNote phrase)
        {
            uint totalTime = 0;
            foreach (var noteInPhrase in phrase.ChildNotes)
            {
                if (noteInPhrase.IsPercussion)
                {
                    continue;
                }

                // If the note continues past the end of the current phrase, clamp it to the end of the phrase instead.
                totalTime += phrase.GetTicksForNote(noteInPhrase);
            }

            if (CarriedVocalNote != null)
            {
                totalTime += phrase.GetTicksForNote(CarriedVocalNote);
            }
            return totalTime;
        }

        /// <returns>
        /// The note in the specified <paramref name="phrase"/> at the specified song <paramref name="tick"/>.
        /// </returns>
        protected VocalNote? GetNoteInPhraseAtSongTick(VocalNote phrase, uint tick)
        {
            if (CarriedVocalNote != null && tick >= CarriedVocalNote.Tick && tick <= CarriedVocalNote.TotalTickEnd)
            {
                return CarriedVocalNote;
            }

            var childNotes = phrase.ChildNotes;
            for (int i = 0; i < childNotes.Count; i++)
            {
                var phraseNote = childNotes[i];
                if (!phraseNote.IsPercussion &&
                    tick >= phraseNote.Tick &&
                    tick <= phraseNote.TotalTickEnd)
                {
                    return phraseNote;
                }
            }

            return null;
        }

        protected static VocalNote? GetNextPercussionNote(VocalNote phrase, uint tick)
        {
            foreach (var note in phrase.ChildNotes)
            {
                // Skip sang vocal notes
                if (!note.IsPercussion && note.Tick < tick)
                {
                    continue;
                }

                // Skip hit/missed percussion notes
                if (note.IsPercussion && (note.WasHit || note.WasMissed))
                {
                    continue;
                }

                // If the next note in the phrase is not a percussion note, then
                // we can't hit the note until the note before it is done.
                if (!note.IsPercussion)
                {
                    return null;
                }

                // Otherwise, we found it!
                return note;
            }

            return null;
        }

        protected override void AddScore(VocalNote note)
        {
            if (note.IsPercussion)
            {
                AddScore(POINTS_PER_PERCUSSION);
                EngineStats.NoteScore += POINTS_PER_PERCUSSION;
            }
            else
            {
                AddScore(EngineParameters.PointsPerPhrase);
                EngineStats.NoteScore += EngineParameters.PointsPerPhrase;
            }
        }

        protected void AddPartialScore(double hitPercent)
        {
            int score = (int) Math.Round(EngineParameters.PointsPerPhrase * hitPercent);
            AddScore(score);
        }

        protected override void UpdateMultiplier()
        {
            EngineStats.ScoreMultiplier = Math.Min(EngineStats.Combo + 1, 4);

            if (EngineStats.IsStarPowerActive)
            {
                EngineStats.ScoreMultiplier *= 2;
            }
        }

        protected sealed override (int baseScore, int noteScore) CalculateChartScores()
        {
            double baseScore = 0;
            double noteScore = 0;
            int combo = 0;
            int multiplier;
            foreach (var note in Notes)
            {
                if (note.ChildNotes.Count == 0)
                {
                    continue;
                };
                multiplier = Math.Min(combo + 1, BaseParameters.MaxMultiplier);
                if (note.IsPercussionPhrase)
                {
                    // Intentionally not counting percussion notes for base score so they don't affect star calculations
                    // baseScore += POINTS_PER_PERCUSSION * note.ChildNotes.Count * multiplier;
                    // noteScore += POINTS_PER_PERCUSSION * note.ChildNotes.Count;
                    continue;
                }
                baseScore += multiplier * EngineParameters.PointsPerPhrase;
                noteScore += EngineParameters.PointsPerPhrase;
                combo++;
            }

            YargLogger.LogDebug($"[Vocals] Base score: {baseScore}, Max Combo: {combo}");
            return ((int) Math.Round(baseScore), (int) Math.Round(noteScore));
        }

        protected override bool CanSustainHold(VocalNote note) => throw new InvalidOperationException();

        protected virtual void UpdateCarriedNote(VocalNote phrase)
        {
            if (CarriedVocalNote != null && CarriedVocalNote.TotalTickEnd > phrase.TickEnd)
            {
                // Keep the current Carried Vocal Note if it continues past the end of the current phrase.
                // This can happen for notes that span across 3 or more phrases.
                return;
            }

            CarriedVocalNote = null;
            foreach (var note in phrase.ChildNotes)
            {
                if (!note.IsPercussion && note.TotalTickEnd > phrase.TickEnd)
                {
                    CarriedVocalNote = note;
                    break;
                }
            }

            EngineStats.HasCarryNote = CarriedVocalNote != null;
        }

        protected override VocalsStats CloneStats() => new(EngineStats);

        /// <summary>
        /// Vocals fire OnSyncNoteHit with the phrase's NoteIndex for both
        /// percussion-child hits (tambourine) AND the phrase-end resolution
        /// itself — they share a NoteIndex because percussion notes don't
        /// advance NoteIndex on the sender. The events arrive in chart order
        /// (every percussion in a phrase resolves before phrase.TickEnd is
        /// crossed), so mirror that here: if the phrase still has an
        /// unresolved percussion child, the wire event must be for that
        /// child; once they're all resolved, the next event under this
        /// NoteIndex is the phrase resolution. Without this two-stage walk,
        /// tambourine hits never reach the receiver — ForceHit(phraseIndex)
        /// would short-circuit on the first call after the phrase is marked
        /// hit, leaving every subsequent percussion silently dropped.
        /// </summary>
        public override void ForceHit(int noteIndex)
        {
            if (noteIndex < 0 || noteIndex >= Notes.Count) return;
            var phrase = Notes[noteIndex];

            foreach (var child in phrase.ChildNotes)
            {
                if (child.IsPercussion && !child.WasHit && !child.WasMissed)
                {
                    HitNote(child);
                    return;
                }
            }

            if (phrase.WasHit || phrase.WasMissed) return;
            HitNote(phrase);
        }

        /// <summary>Mirror of ForceHit for the miss path. TicksHit/TicksMissed
        /// reconcile via the next EngineStateSnapshot regardless of the 0
        /// hitPercent used for phrase misses.</summary>
        public override void ForceMiss(int noteIndex)
        {
            if (noteIndex < 0 || noteIndex >= Notes.Count) return;
            var phrase = Notes[noteIndex];

            foreach (var child in phrase.ChildNotes)
            {
                if (child.IsPercussion && !child.WasHit && !child.WasMissed)
                {
                    MissNote(child);
                    return;
                }
            }

            if (phrase.WasHit || phrase.WasMissed) return;
            MissNote(phrase);
        }

        // Vocal phrases end when their tick range expires; no input-release sustain.
        public override void ForceReleaseSustain(int noteIndex)
        {
        }

        public override EngineSnapshot CreateSnapshot()
        {
            var snap = new VocalsEngineSnapshot();
            CaptureGenericSnapshot(snap);

            snap.HasPhraseTicksTotal = PhraseTicksTotal.HasValue;
            snap.PhraseTicksTotal    = PhraseTicksTotal ?? 0u;
            snap.PhraseTicksHit      = PhraseTicksHit;
            snap.LastSingTick        = LastSingTick;
            snap.PitchSang           = PitchSang;
            snap.HasSang             = HasSang;

            // Carried notes are always children of a phrase near NoteIndex; localized search suffices.
            snap.CarriedVocalNotePhraseIndex = -1;
            snap.CarriedVocalNoteChildIndex  = -1;
            if (CarriedVocalNote != null)
            {
                for (int p = 0; p < Notes.Count; p++)
                {
                    var phrase = Notes[p];
                    var children = phrase.ChildNotes;
                    for (int c = 0; c < children.Count; c++)
                    {
                        if (ReferenceEquals(children[c], CarriedVocalNote))
                        {
                            snap.CarriedVocalNotePhraseIndex = p;
                            snap.CarriedVocalNoteChildIndex  = c;
                            p = Notes.Count; // break outer
                            break;
                        }
                    }
                }
            }

            return snap;
        }

        public override void RestoreSnapshot(EngineSnapshot snapshot)
        {
            if (snapshot is not VocalsEngineSnapshot snap)
            {
                throw new InvalidOperationException(
                    "VocalsEngine.RestoreSnapshot: snapshot must be a VocalsEngineSnapshot.");
            }
            RestoreGenericSnapshot(snap);

            PhraseTicksTotal = snap.HasPhraseTicksTotal ? snap.PhraseTicksTotal : (uint?) null;
            PhraseTicksHit   = snap.PhraseTicksHit;
            LastSingTick     = snap.LastSingTick;
            PitchSang        = snap.PitchSang;
            HasSang          = snap.HasSang;

            CarriedVocalNote = null;
            if (snap.CarriedVocalNotePhraseIndex >= 0 &&
                snap.CarriedVocalNotePhraseIndex < Notes.Count)
            {
                var phrase = Notes[snap.CarriedVocalNotePhraseIndex];
                var children = phrase.ChildNotes;
                if (snap.CarriedVocalNoteChildIndex >= 0 &&
                    snap.CarriedVocalNoteChildIndex < children.Count)
                {
                    CarriedVocalNote = children[snap.CarriedVocalNoteChildIndex];
                }
            }
        }
    }
}
