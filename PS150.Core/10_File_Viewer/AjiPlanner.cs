using System;
using System.Collections.Generic;
using System.Linq;

namespace PS150.Core
{
    // ======================================================================
    // AJI = adaptivní čisté ladění pro přehrávání .mid souborů.
    //
    // Myšlenka: při načtení souboru se CELÁ skladba jednou projde (na
    // pozadí, v řádu milisekund) a vznikne "plán ladění":
    //   1) zjistí se tónina skladby a její změny (modulace),
    //   2) každé notě se podle tóniny přiřadí odchylka v centech od
    //      rovnoměrné temperatury (z tabulky níž),
    //   3) noty se rozdělí na kanály tak, aby na jednom kanálu vždy znělo
    //      jen to, co má stejnou odchylku (pitch bend je u MIDI společný
    //      pro celý kanál, ne pro jednotlivou notu).
    // Přehrávač pak jen podle klávesy a buď zahraje původní (temperované)
    // noty, nebo ty z plánu. Plán se nepočítá za chodu, takže přepínání je
    // okamžité a analýza může vidět skladbu dopředu i dozadu.
    //
    // Tahle třída neví nic o DryWetMidi ani o přehrávači: dostane jen čísla
    // (noty, pedál) a vrátí plán. Dá se tedy zkoušet i vyměnit nezávisle
    // na zbytku programu.
    // ======================================================================

    /// <summary>Které ladění se použije pro odchylky not vůči tónice.</summary>
    public enum AjiTemperament
    {
        /// <summary>Čisté ladění (5. limit): čisté tercie 5:4 a 6:5, čisté kvinty 3:2. Pro hlasy a smyčce nejsladší.</summary>
        JustIntonation5Limit,

        /// <summary>Čtvrtkomatové středotónové ladění (barokní): čisté velké tercie, kvinty o 1/4 komatu užší.</summary>
        QuarterCommaMeantone,

        /// <summary>Pythagorejské ladění (starověké): čisté kvinty 3:2, tercie "tvrdé" (81:64).</summary>
        Pythagorean
    }

    /// <summary>Jedna nota ze souboru: kanál a číslo noty MIDI, začátek a konec v sekundách.</summary>
    public readonly struct AjiNoteInput
    {
        public int Channel { get; }
        public int Note { get; }
        public double Start { get; }
        public double End { get; }

        public AjiNoteInput(int channel, int note, double start, double end)
        {
            Channel = channel;
            Note = note;
            Start = start;
            End = end;
        }
    }

    /// <summary>Jedna událost pedálu (CC64) ze souboru: kanál, čas v sekundách, sešlápnut/uvolněn.</summary>
    public readonly struct AjiPedalInput
    {
        public int Channel { get; }
        public double Time { get; }
        public bool Down { get; }

        public AjiPedalInput(int channel, double time, bool down)
        {
            Channel = channel;
            Time = time;
            Down = down;
        }
    }

    /// <summary>Úsek skladby s jednou tóninou.</summary>
    public sealed class AjiKeySection
    {
        private static readonly string[] PitchClassNames =
            { "C", "Cis", "D", "Es", "E", "F", "Fis", "G", "As", "A", "B", "H" };

        public double Start { get; }
        public int TonicPc { get; }       // 0 = C ... 11 = H
        public bool IsMinor { get; }

        /// <summary>
        /// Základní tón, od kterého se počítá ladění. U dur je to tónika,
        /// u moll tónika relativní durové tóniny (a moll se ladí od C, d moll
        /// od F...), stejně jako "C dur = a moll = nula" v zadání.
        /// </summary>
        public int RootPc => IsMinor ? (TonicPc + 3) % 12 : TonicPc;

        public string Name => IsMinor
            ? PitchClassNames[TonicPc].ToLowerInvariant() + " moll"
            : PitchClassNames[TonicPc] + " dur";

        public AjiKeySection(double start, int tonicPc, bool isMinor)
        {
            Start = start;
            TonicPc = ((tonicPc % 12) + 12) % 12;
            IsMinor = isMinor;
        }
    }

    /// <summary>Plán pro jednu notu: na kterém kanálu a s jakým pitch bendem a panorámou se zahraje.</summary>
    public sealed class AjiNotePlan
    {
        public int Channel { get; }          // kanál ze souboru (pro dohledání)
        public int Note { get; }             // číslo noty ze souboru
        public double Start { get; }         // začátek v sekundách
        public int PoolChannel { get; }      // kanál, na kterém se nota fyzicky zahraje
        public double OffsetCents { get; }   // odchylka od rovnoměrné temperatury (bend kanálu)
        public int Pan { get; }              // panoráma kanálu (CC10)

        public AjiNotePlan(int channel, int note, double start, int poolChannel, double offsetCents, int pan)
        {
            Channel = channel;
            Note = note;
            Start = start;
            PoolChannel = poolChannel;
            OffsetCents = offsetCents;
            Pan = pan;
        }
    }

    /// <summary>Hotový plán pro celý soubor: tóniny a rozdělení not na kanály.</summary>
    public sealed class AjiPlan
    {
        private readonly Dictionary<(int Channel, int Note), List<AjiNotePlan>> _byKey = new();

        public IReadOnlyList<AjiKeySection> Sections { get; }
        public int NoteCount { get; }

        /// <summary>Kolik not se nepodařilo rozdělit přesně (nedostatek volných kanálů) - dostaly nejbližší možnou odchylku.</summary>
        public int ApproximatedNotes { get; }

        internal AjiPlan(List<AjiKeySection> sections, List<AjiNotePlan> notes, int approximatedNotes)
        {
            Sections = sections;
            NoteCount = notes.Count;
            ApproximatedNotes = approximatedNotes;

            foreach (var plan in notes)
            {
                var key = (plan.Channel, plan.Note);
                if (!_byKey.TryGetValue(key, out var list))
                {
                    list = new List<AjiNotePlan>();
                    _byKey[key] = list;
                }
                list.Add(plan);
            }

            foreach (var list in _byKey.Values)
            {
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
        }

        /// <summary>
        /// Najde plán pro notu (kanál, číslo) začínající přibližně v daném
        /// čase. Z několika stejných not v souboru vybere tu s nejbližším
        /// začátkem; musí ležet v toleranci (jinak vrací false a přehrávač
        /// zahraje notu normálně).
        /// </summary>
        public bool TryGetNote(int channel, int note, double timeSeconds, double toleranceSeconds, out AjiNotePlan? plan)
        {
            plan = null;
            if (!_byKey.TryGetValue((channel, note), out var list) || list.Count == 0) return false;

            int lo = 0, hi = list.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Start < timeSeconds) lo = mid + 1; else hi = mid;
            }

            AjiNotePlan? best = null;
            double bestDiff = double.MaxValue;
            for (int i = Math.Max(0, lo - 1); i <= Math.Min(list.Count - 1, lo); i++)
            {
                double diff = Math.Abs(list[i].Start - timeSeconds);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = list[i];
                }
            }

            if (best == null || bestDiff > toleranceSeconds) return false;

            plan = best;
            return true;
        }

        /// <summary>Úsek tóniny platný v daný čas (null, pokud skladba nemá žádné melodické noty).</summary>
        public AjiKeySection? SectionAt(double timeSeconds)
        {
            AjiKeySection? result = null;
            foreach (var section in Sections)
            {
                if (section.Start <= timeSeconds + 1e-9) result = section; else break;
            }
            return result ?? (Sections.Count > 0 ? Sections[0] : null);
        }

        /// <summary>Text pro zobrazení, např. "Fis dur" nebo "d moll".</summary>
        public string DescribeKeyAt(double timeSeconds) => SectionAt(timeSeconds)?.Name ?? "?";
    }

    /// <summary>
    /// Plánovač AJI. Všechna nastavení jsou záměrně natvrdo tady nahoře
    /// (konfigurace v kódu, ne v settings.json) - stačí změnit hodnotu a
    /// znovu sestavit.
    /// </summary>
    public static class AjiPlanner
    {
        // ---------------------- NASTAVENÍ (natvrdo) ----------------------

        /// <summary>Které ladění se použije. Zkuste JustIntonation5Limit, QuarterCommaMeantone (barokní) nebo Pythagorean (starověké).</summary>
        public const AjiTemperament ChosenTemperament = AjiTemperament.Pythagorean;

        /// <summary>Délka okna pro určení tóniny (sekundy). Delší = stabilnější, ale pomalejší na modulace.</summary>
        public const double WindowSeconds = 8.0;

        /// <summary>Krok, s jakým se okno posouvá po skladbě (sekundy).</summary>
        public const double HopSeconds = 2.0;

        /// <summary>O kolik musí nová tónina porazit stávající (korelace), aby se vůbec zvažovala.</summary>
        public const double SwitchMargin = 0.04;

        /// <summary>Kolik po sobě jdoucích kroků musí nová tónina vítězit, než se přepne (zabraňuje poskakování).</summary>
        public const int MinSwitchHops = 2;

        /// <summary>Po skončení noty (včetně pedálu) se kanál tuto dobu (s) nepřeladí, ať nota doznívá čistě.</summary>
        public const double NoteGuardSeconds = 0.35;

        /// <summary>O kolik sekund se smí lišit čas události od plánu při dohledávání (viz AjiPlan.TryGetNote).</summary>
        public const double MatchToleranceSeconds = 0.3;

        /// <summary>Kanál bicích (D10) - ten se neladí a do plánu nevstupuje.</summary>
        public const int PercussionChannel = 9;

        /// <summary>Krátký popis zvoleného ladění pro obrazovku.</summary>
        public static string TemperamentLabel => ChosenTemperament switch
        {
            AjiTemperament.JustIntonation5Limit => "JI-5",
            AjiTemperament.QuarterCommaMeantone => "stredot.",
            AjiTemperament.Pythagorean => "Pythag.",
            _ => "?"
        };

        // ---------------------- TABULKY LADĚNÍ ----------------------
        // Odchylky v centech od rovnoměrné temperatury, index = počet půltónů
        // nad základním tónem (0 = základní tón, 7 = kvinta ...).

        // Čisté ladění 5. limitu: 1/1, 16/15, 9/8, 6/5, 5/4, 4/3, 45/32, 3/2, 8/5, 5/3, 16/9, 15/8.
        private static readonly double[] JustTable =
        {
            0.00, +11.73, +3.91, +15.64, -13.69, -1.96, -9.78, +1.96, +13.69, -15.64, -3.91, -11.73
        };

        // Čtvrtkomatové středotónové - stejná tabulka jako Temperament.QuarterCommaMeantone.
        private static readonly double[] MeantoneTable = Temperament.QuarterCommaMeantone.CentOffsets;

        // Pythagorejské ladění: řetěz čistých kvint od Es po Gis (stejné rozložení jako u středotónového).
        private static readonly double[] PythagoreanTable =
        {
            0.00, +13.69, +3.91, -5.87, +7.82, -1.96, +11.73, +1.96, +15.64, +5.87, -3.91, +9.78
        };

        public static double OffsetCents(AjiTemperament temperament, int semitonesAboveRoot)
        {
            int i = ((semitonesAboveRoot % 12) + 12) % 12;
            return temperament switch
            {
                AjiTemperament.JustIntonation5Limit => JustTable[i],
                AjiTemperament.QuarterCommaMeantone => MeantoneTable[i],
                AjiTemperament.Pythagorean => PythagoreanTable[i],
                _ => 0.0
            };
        }

        // Krumhansl-Kessler profily (jak moc se který stupeň vyskytuje v dur / moll).
        private static readonly double[] MajorProfile =
            { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };

        private static readonly double[] MinorProfile =
            { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        // ---------------------- HLAVNÍ VSTUP ----------------------

        /// <summary>
        /// Sestaví plán. <paramref name="panByChannel"/> má 16 hodnot (0-127):
        /// panorámu, kterou má mít každý kanál ze souboru (ping-pong rozložení
        /// z přehrávače) - nota ji zdědí.
        /// </summary>
        public static AjiPlan Build(
            IReadOnlyList<AjiNoteInput> notes,
            IReadOnlyList<AjiPedalInput> pedals,
            int[] panByChannel)
        {
            var melodic = notes
                .Where(n => n.Channel != PercussionChannel && n.Channel >= 0 && n.Channel <= 15 && n.End > n.Start)
                .OrderBy(n => n.Start)
                .ToList();

            if (melodic.Count == 0)
            {
                return new AjiPlan(new List<AjiKeySection>(), new List<AjiNotePlan>(), 0);
            }

            double pieceEnd = melodic.Max(n => n.End);
            var sections = DetectKeys(melodic, pieceEnd);

            // Pedál podle kanálů, seřazený v čase.
            var pedalsByChannel = new Dictionary<int, List<AjiPedalInput>>();
            foreach (var pedal in pedals.OrderBy(p => p.Time))
            {
                if (!pedalsByChannel.TryGetValue(pedal.Channel, out var list))
                {
                    list = new List<AjiPedalInput>();
                    pedalsByChannel[pedal.Channel] = list;
                }
                list.Add(pedal);
            }

            // Kanály, na které se noty rozdělují (všechny kromě bicích).
            var pool = new List<PoolChannel>();
            for (int channel = 0; channel <= 15; channel++)
            {
                if (channel != PercussionChannel) pool.Add(new PoolChannel(channel));
            }

            var result = new List<AjiNotePlan>(melodic.Count);
            int approximated = 0;

            foreach (var note in melodic)
            {
                // 1) Odchylka noty podle tóniny, která platí na jejím začátku.
                var section = FindSection(sections, note.Start);
                int interval = (note.Note % 12 - section.RootPc + 12) % 12;
                double offset = OffsetCents(ChosenTemperament, interval);

                int pan = (panByChannel != null && note.Channel < panByChannel.Length) ? panByChannel[note.Channel] : 64;

                // 2) Kdy nota skutečně přestane znít (pedál ji může držet déle).
                double effectiveEnd = ExtendEndByPedal(pedalsByChannel, note.Channel, note.End, pieceEnd);
                double busyUntil = effectiveEnd + NoteGuardSeconds;

                // 3) Výběr kanálu.
                PoolChannel? chosen = null;

                // 3a) Obsazený kanál se stejným ohybem a panorámou - sdílíme ho.
                foreach (var ch in pool)
                {
                    if (ch.BusyUntil > note.Start && ch.Pan == pan && SameBend(ch.Bend, offset) && !ch.HasActiveNote(note.Note, note.Start))
                    {
                        chosen = ch;
                        break;
                    }
                }

                // 3b) Volný kanál (nejlépe takový, který už má správný ohyb a panorámu).
                if (chosen == null)
                {
                    PoolChannel? bestIdle = null;
                    foreach (var ch in pool)
                    {
                        if (ch.BusyUntil > note.Start) continue;

                        if (ch.Pan == pan && SameBend(ch.Bend, offset))
                        {
                            bestIdle = ch;
                            break;
                        }

                        if (bestIdle == null || ch.BusyUntil < bestIdle.BusyUntil) bestIdle = ch;
                    }

                    if (bestIdle != null)
                    {
                        chosen = bestIdle;
                        chosen.Bend = offset;
                        chosen.Pan = pan;
                    }
                }

                // 3c) Nouze: všechny kanály jsou obsazené. Vezmeme ten s nejbližším ohybem
                // (a ideálně stejnou panorámou) a notu o to nepřesněji naladíme.
                if (chosen == null)
                {
                    double bestCost = double.MaxValue;
                    foreach (var ch in pool)
                    {
                        if (ch.HasActiveNote(note.Note, note.Start)) continue;

                        double cost = Math.Abs(ch.Bend - offset) + (ch.Pan != pan ? 200.0 : 0.0);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            chosen = ch;
                        }
                    }

                    // Naprosto extrémní případ: všude už stejná nota zní - vezmeme kanál, který skončí nejdřív.
                    chosen ??= pool.OrderBy(c => c.BusyUntil).First();

                    approximated++;
                }

                chosen.BusyUntil = Math.Max(chosen.BusyUntil, busyUntil);
                chosen.Active.Add((note.Note, effectiveEnd));

                result.Add(new AjiNotePlan(
                    note.Channel, note.Note, note.Start,
                    chosen.Index, chosen.Bend, chosen.Pan));
            }

            return new AjiPlan(sections, result, approximated);
        }

        // ---------------------- POMOCNÉ ----------------------

        private sealed class PoolChannel
        {
            public int Index { get; }
            public double Bend = double.NaN;   // NaN = zatím nenastaveno
            public int Pan = -1;
            public double BusyUntil = double.NegativeInfinity;
            public readonly List<(int Note, double End)> Active = new();

            public PoolChannel(int index) { Index = index; }

            public bool HasActiveNote(int note, double atTime)
            {
                Active.RemoveAll(a => a.End <= atTime);
                foreach (var a in Active)
                {
                    if (a.Note == note) return true;
                }
                return false;
            }
        }

        private static bool SameBend(double a, double b)
        {
            return !double.IsNaN(a) && Math.Abs(a - b) < 0.05;
        }

        private static AjiKeySection FindSection(List<AjiKeySection> sections, double time)
        {
            AjiKeySection result = sections[0];
            foreach (var s in sections)
            {
                if (s.Start <= time + 1e-9) result = s; else break;
            }
            return result;
        }

        /// <summary>Je-li v okamžiku konce noty sešlápnutý pedál daného kanálu, nota zní až do jeho uvolnění.</summary>
        private static double ExtendEndByPedal(Dictionary<int, List<AjiPedalInput>> pedalsByChannel, int channel, double end, double pieceEnd)
        {
            if (!pedalsByChannel.TryGetValue(channel, out var list) || list.Count == 0) return end;

            int idx = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Time <= end) idx = i; else break;
            }

            if (idx < 0 || !list[idx].Down) return end;

            for (int i = idx + 1; i < list.Count; i++)
            {
                if (!list[i].Down) return list[i].Time;
            }
            return Math.Max(end, pieceEnd);
        }

        // ---------------------- DETEKCE TÓNINY ----------------------

        private static List<AjiKeySection> DetectKeys(List<AjiNoteInput> melodic, double pieceEnd)
        {
            var sections = new List<AjiKeySection>();

            int current = -1;       // index 0-11 = dur (tónika), 12-23 = moll (tónika - 12)
            int pending = -1;
            int pendingCount = 0;
            double pendingStart = 0.0;

            for (double t = 0.0; t <= pieceEnd + 1e-9; t += HopSeconds)
            {
                double w0 = t - WindowSeconds / 2.0;
                double w1 = t + WindowSeconds / 2.0;

                var hist = new double[12];
                double sum = 0.0;

                foreach (var n in melodic)
                {
                    if (n.Start >= w1) break;   // noty jsou seřazené podle začátku

                    double overlap = Math.Min(n.End, w1) - Math.Max(n.Start, w0);
                    if (overlap > 0.0)
                    {
                        hist[n.Note % 12] += overlap;
                        sum += overlap;
                    }
                }

                if (sum < 0.2) continue;   // skoro ticho - tóninu neměníme

                double[] scores = ScoreKeys(hist);
                int best = 0;
                for (int i = 1; i < scores.Length; i++)
                {
                    if (scores[i] > scores[best]) best = i;
                }

                if (current < 0)
                {
                    current = best;
                    sections.Add(new AjiKeySection(0.0, best % 12, best >= 12));
                    continue;
                }

                if (best != current && scores[best] - scores[current] > SwitchMargin)
                {
                    if (best == pending)
                    {
                        pendingCount++;
                    }
                    else
                    {
                        pending = best;
                        pendingCount = 1;
                        pendingStart = t;
                    }

                    if (pendingCount >= MinSwitchHops)
                    {
                        sections.Add(new AjiKeySection(pendingStart, best % 12, best >= 12));
                        current = best;
                        pending = -1;
                        pendingCount = 0;
                    }
                }
                else
                {
                    pending = -1;
                    pendingCount = 0;
                }
            }

            if (sections.Count == 0)
            {
                sections.Add(new AjiKeySection(0.0, 0, false));
            }

            return sections;
        }

        /// <summary>
        /// Korelace histogramu tónů s profilem každé z 24 tónin (Pearsonův
        /// korelační koeficient). Index 0-11 = dur s danou tonikou, 12-23 = moll.
        /// </summary>
        private static double[] ScoreKeys(double[] hist)
        {
            var scores = new double[24];

            double meanH = hist.Average();
            double sdH = Math.Sqrt(hist.Sum(h => (h - meanH) * (h - meanH)));
            if (sdH < 1e-9) return scores;

            for (int mode = 0; mode < 2; mode++)
            {
                double[] profile = mode == 0 ? MajorProfile : MinorProfile;
                double meanP = profile.Average();
                double sdP = Math.Sqrt(profile.Sum(p => (p - meanP) * (p - meanP)));

                for (int tonic = 0; tonic < 12; tonic++)
                {
                    double num = 0.0;
                    for (int pc = 0; pc < 12; pc++)
                    {
                        double p = profile[(pc - tonic + 12) % 12];
                        num += (hist[pc] - meanH) * (p - meanP);
                    }
                    scores[mode * 12 + tonic] = num / (sdH * sdP);
                }
            }

            return scores;
        }
    }
}
