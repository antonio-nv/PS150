using System;
using System.Collections.Generic;

namespace PS150.Core.ToneEngine
{
    // ======================================================================
    // krok69: AJI pro ŽIVÉ hraní.
    //
    // Na rozdíl od AjiPlanner/AjiPlan (PS150.Core/10_File_Viewer/AjiPlanner.cs),
    // který analyzuje CELÝ .mid soubor předem (zná i budoucnost skladby), tohle
    // běží v reálném čase a umí dva režimy:
    //
    //   - FixedKey:   tónina (tónika + dur/moll) zadaná NATVRDO rukou - žádná
    //                 detekce, jen přepočet odchylek podle zvoleného ladění.
    //   - AutoDetect: tónina se průběžně odhaduje ze SMĚREM DO MINULOSTI
    //                 klouzavého okna posledních pár sekund hraní - stejný
    //                 Krumhansl-Kesslerův algoritmus (korelace histogramu tónů
    //                 s profilem dur/moll) jako AjiPlanner.ScoreKeys, jen
    //                 počítaný z živého, průběžně rostoucího/zapomínajícího
    //                 seznamu not místo z hotového seznamu celé skladby.
    //
    // Důležitý rozdíl oproti souboru: tam se kvůli MIDI (pitch bend je
    // společný pro celý kanál) muselo pracně dělit 16 kanálů mezi noty se
    // stejnou odchylkou (viz AjiPlan.Build/PoolChannel). Tady nic takového
    // není potřeba - ToneEngine počítá kmitočet KAŽDÉ noty zvlášť (viz
    // ToneEngine.NoteOn/RetuneActiveNotes), takže se rovnou spočítá přesný
    // kmitočet, žádné přibližování.
    // ======================================================================

    public enum LiveAjiMode
    {
        Off,
        FixedKey,
        AutoDetect
    }

    /// <summary>
    /// Všechna nastavení živého AJI na jednom místě - "čím víc nastavování, tím
    /// líp, ať se v tom teoretici můžou šťourat" (přání uživatele, krok69).
    /// Mutovatelná POCO - po změně kteréhokoliv pole zavolej
    /// AudioEngine.ApplyLiveAji()/ToneEngine.RetuneActiveNotes(), ať se to hned
    /// projeví i na právě podržených tónech (stejný "glissando" trik jako
    /// krok68 u T/A4).
    /// </summary>
    public sealed class LiveAjiSettings
    {
        public LiveAjiMode Mode { get; set; } = LiveAjiMode.Off;

        /// <summary>Které ladění odchylek se použije - viz PS150.AjiTemperament.</summary>
        public PS150.Core.AjiTemperament Temperament { get; set; } = PS150.Core.AjiTemperament.JustIntonation5Limit;

        // ---- FixedKey ----
        /// <summary>Tónika 0-11 (0=C, 1=Cis, 2=D... stejně jako AjiKeySection.TonicPc).</summary>
        public int FixedTonicPc { get; set; } = 0;
        public bool FixedIsMinor { get; set; } = false;

        // ---- AutoDetect - "pokusné" parametry pro teoretiky ----
        /// <summary>Délka klouzavého okna (s) - jen dozadu, žádná budoucnost jako u souboru.</summary>
        public double WindowSeconds { get; set; } = PS150.Core.AjiPlanner.WindowSeconds;

        /// <summary>Jak často se okno přehodnocuje (s).</summary>
        public double HopSeconds { get; set; } = PS150.Core.AjiPlanner.HopSeconds;

        /// <summary>O kolik musí nová tónina porazit stávající (korelace), aby se vůbec zvažovala.</summary>
        public double SwitchMargin { get; set; } = PS150.Core.AjiPlanner.SwitchMargin;

        /// <summary>Kolik po sobě jdoucích přehodnocení musí nová tónina vyhrát, než se přepne.</summary>
        public int MinSwitchHops { get; set; } = PS150.Core.AjiPlanner.MinSwitchHops;

        /// <summary>
        /// Základní tón, od kterého se počítají odchylky OffsetCents - u dur je to
        /// tónika, u moll tónika JEJÍ relativní dur (a moll -> C, d moll -> F...),
        /// stejně jako AjiKeySection.RootPc u souborového AJI.
        /// </summary>
        public static int RelativeMajorRoot(int tonicPc, bool isMinor) =>
            isMinor ? (((tonicPc + 3) % 12) + 12) % 12 : ((tonicPc % 12) + 12) % 12;
    }

    /// <summary>
    /// Klouzavý detektor tóniny pro LiveAjiMode.AutoDetect - viz komentář u
    /// třídy výš. NEBICÍ noty se posílají přes NoteOn(); vrací aktuální odhad
    /// (HasKey/RootPc/IsMinor) po celou dobu mezi přehodnoceními, HasKey je
    /// false, dokud nehrálo dost not na první odhad.
    /// </summary>
    public sealed class LiveKeyDetector
    {
        private readonly object _lock = new object();
        private readonly List<(double Time, int PitchClass)> _recent = new List<(double, int)>();

        private int _current = -1;      // 0-11 = dur (tónika), 12-23 = moll (tónika-12), -1 = zatím nic
        private int _pending = -1;
        private int _pendingCount = 0;
        private double _lastScoreTime = double.NegativeInfinity;

        /// <summary>Tónika aktuálně odhadnuté tóniny (0-11) - platné jen když HasKey.</summary>
        public int TonicPc { get; private set; } = 0;
        public bool IsMinor { get; private set; } = false;
        public bool HasKey { get; private set; } = false;

        /// <summary>
        /// Nahlásí odehranou melodickou notu (bicí se sem posílat nemají - viz
        /// AjiPlanner.PercussionChannel). nowSeconds = libovolný rostoucí čas
        /// (ToneEngine má svoje vlastní hodiny, viz LiveClockSeconds).
        /// </summary>
        public void NoteOn(int midiNoteNumber, double nowSeconds, LiveAjiSettings settings)
        {
            lock (_lock)
            {
                int pc = ((midiNoteNumber % 12) + 12) % 12;
                _recent.Add((nowSeconds, pc));

                double cutoff = nowSeconds - Math.Max(1.0, settings.WindowSeconds);
                _recent.RemoveAll(n => n.Time < cutoff);

                if (nowSeconds - _lastScoreTime < Math.Max(0.1, settings.HopSeconds)) return;
                _lastScoreTime = nowSeconds;

                var hist = new double[12];
                foreach (var n in _recent) hist[n.PitchClass] += 1.0;

                double total = 0.0;
                foreach (double h in hist) total += h;
                if (total < 3.0) return;   // moc málo not na spolehlivý odhad - nechme být

                double[] scores = PS150.Core.AjiPlanner.ScoreKeys(hist);
                int best = 0;
                for (int i = 1; i < scores.Length; i++)
                {
                    if (scores[i] > scores[best]) best = i;
                }

                if (_current < 0)
                {
                    _current = best;
                    Apply(best);
                    return;
                }

                if (best != _current && scores[best] - scores[_current] > settings.SwitchMargin)
                {
                    if (best == _pending)
                    {
                        _pendingCount++;
                    }
                    else
                    {
                        _pending = best;
                        _pendingCount = 1;
                    }

                    if (_pendingCount >= Math.Max(1, settings.MinSwitchHops))
                    {
                        _current = best;
                        _pending = -1;
                        _pendingCount = 0;
                        Apply(best);
                    }
                }
                else
                {
                    _pending = -1;
                    _pendingCount = 0;
                }
            }
        }

        private void Apply(int keyIndex)
        {
            TonicPc = keyIndex % 12;
            IsMinor = keyIndex >= 12;
            HasKey = true;
        }

        /// <summary>Zapomene vše (volá se při vypnutí/zapnutí AutoDetect nebo změně parametrů okna).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _recent.Clear();
                _current = -1;
                _pending = -1;
                _pendingCount = 0;
                _lastScoreTime = double.NegativeInfinity;
                HasKey = false;
            }
        }
    }
}
