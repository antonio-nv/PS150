using System;

namespace PS150.Core.Tones
{
    /// <summary>
    /// Zobrazovací název MIDI noty - čistá logika bez závislosti na
    /// konkrétním UI frameworku (používá jak PS150.UI.Windows, tak by měl
    /// i PS150.UI.Linux, ať zobrazení nikde nerozjede).
    /// </summary>
    public static class NoteDisplay
    {
        // Přirozené tóny (bez křížku/béčka) a jejich výška v centech od C
        // v rámci jedné oktávy - jediná tabulka pro všechno (běžné
        // půltóny i čtvrttóny/tříčtvrttóny), viz NoteName níž.
        private static readonly (char Letter, int Cents)[] Naturals =
        {
            ('C', 0), ('D', 200), ('E', 400), ('F', 500), ('G', 700), ('A', 900), ('B', 1100),
        };

        /// <summary>
        /// Zkratka GM bicího (kanál D10) podle čísla noty - stejná tabulka
        /// jako u syntézy (_700_Drums.cs), takže se zobrazení nemůže
        /// rozejít s tím, co skutečně hraje.
        /// </summary>
        public static string DrumAbbreviation(int noteNumber)
        {
            foreach (var p in _700_Drums.Preset.DrumParameters)
            {
                if (p.GmNoteNumber == noteNumber) return p.Abbreviation;
            }
            return noteNumber.ToString(); // Neznámé číslo v tabulce - aspoň syrové číslo noty, ať je vidět, že něco hraje
        }

        /// <summary>
        /// Název noty vč. čtvrttónové/tříčtvrttónové odchylky. `bendCents`
        /// je aktuální pitch bend kanálu v centech v okamžiku NoteOn (0 pro
        /// běžné, nečtvrttónové soubory). Značka (¼/¾, #/b) se píše PŘED
        /// písmenem, jak požadováno - např. "¼#C5", "¾bC5", "bG5".
        ///
        /// Písmeno se NEODVOZUJE ze syrového čísla MIDI noty (to by
        /// čtvrttóny/tříčtvrttóny vždycky ukázalo jako křížek, nikdy jako
        /// béčko) - místo toho se hledá nejbližší přirozený tón
        /// (C/D/E/F/G/A/B) k výsledné výšce (nota+ohyb dohromady). Když je
        /// přesně uprostřed mezi dvěma sousedními přirozenými tóny
        /// (klasický případ černé klávesy bez ohybu, ale stejně tak
        /// čtvrttón přesně "mezi" notami), rozhoduje `preferFlats` (z
        /// tóniny souboru).
        /// </summary>
        public static string NoteName(int noteNumber, double bendCents, bool preferFlats)
        {
            double totalCents = noteNumber * 100.0 + bendCents;
            double localBase = Math.Floor(totalCents / 1200.0) * 1200.0;

            char bestLetter = 'C';
            double bestCents = 0;
            double bestDist = double.MaxValue;

            for (int octaveShift = -1; octaveShift <= 1; octaveShift++)
            {
                double candidateBase = localBase + octaveShift * 1200.0;
                foreach (var (letter, cents) in Naturals)
                {
                    double candidateCents = candidateBase + cents;
                    double dist = Math.Abs(totalCents - candidateCents);

                    bool strictlyBetter = dist < bestDist - 0.01;
                    bool tied = Math.Abs(dist - bestDist) <= 0.01;
                    bool preferThisOnTie = tied && PreferCandidate(totalCents, candidateCents, preferFlats);

                    if (strictlyBetter || preferThisOnTie)
                    {
                        bestLetter = letter;
                        bestCents = candidateCents;
                        bestDist = dist;
                    }
                }
            }

            int quarterSteps = (int)Math.Round((totalCents - bestCents) / 50.0, MidpointRounding.AwayFromZero);
            int octave = (int)Math.Floor(bestCents / 1200.0) - 1;

            string accidental = quarterSteps switch
            {
                0 => "",
                1 => "¼#",
                2 => "#",
                3 => "¾#",
                -1 => "¼b",
                -2 => "b",
                -3 => "¾b",
                // Extrémnější odchylky (nad tříčtvrttón) - v běžné hudbě se
                // nestávají, a i kdyby, čtvrttónová hudba je stejně naprosto
                // šílená, takže tady stačí cokoliv rozumně čitelného.
                > 3 => $"{quarterSteps}q#",
                _ => $"{-quarterSteps}qb",
            };

            return $"{accidental}{bestLetter}{octave}";
        }

        /// <summary>Při přesné shodě dvou stejně vzdálených přirozených tónů: preferFlats=true chce tón NAD (vyjde to jako béčko), jinak tón POD (vyjde to jako křížek).</summary>
        private static bool PreferCandidate(double totalCents, double candidateCents, bool preferFlats)
        {
            bool candidateIsAbove = candidateCents > totalCents;
            return preferFlats ? candidateIsAbove : !candidateIsAbove;
        }
    }
}
