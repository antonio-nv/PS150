using PS150.Core.Filters;
using NAudio.SoundFont;
using System;

namespace PS150.Core.Generators
{


    public class OrganVoice : SynthVoice
    {
        private readonly VoicePreset _preset;
        private readonly double[] _partialRatios;
        private readonly double[] _partialAmplitudes;
        private double[] _phasesA;
        private double[] _phasesB; // druhý, staticky rozladěný hlas (chór) - viz CalculateWaveform
        private double _chiffEnvelope = 1.0;
        private readonly NoiseGenerator _noiseGen = new NoiseGenerator();
        private readonly BandPassFilter _chiffFilter = new BandPassFilter();

        // AM/FM modulace (tremolo/vibrato) - viz komentář u CalculateWaveform.
        private readonly ModulationType _modType;
        private readonly double _modSpeedHz;
        private readonly double _modDepth;
        private double _modPhase = 0.0;

        // Statický chór (viz komentář u CalculateWaveform).
        private readonly bool _chorusEnabled;
        private readonly double _chorusDetuneRatio;
        private readonly double[] _perPartialDetuneRatio; // null = použij _chorusDetuneRatio pro všechny
        private readonly double _gainA;
        private readonly double _gainB;

        public OrganVoice(VoicePreset preset, double sampleRate) : base(sampleRate)
        {
            _preset = preset;

            // Bezpečnostní záchytka - kdyby na rejstřík při tom velkém ručním
            // přepisu ze svatovítské analýzy někdo zapomněl (nebo ho teprve
            // rozepisuje), radši tichý čistý sinus na základním tónu, než pád.
            bool hasPartials =
                preset.PartialRatios != null && preset.PartialAmplitudes != null &&
                preset.PartialRatios.Length > 0 &&
                preset.PartialRatios.Length == preset.PartialAmplitudes.Length;

            _partialRatios = hasPartials ? preset.PartialRatios : new double[] { 1.0 };
            _partialAmplitudes = hasPartials ? preset.PartialAmplitudes : new double[] { 1.0 };
            _phasesA = new double[_partialRatios.Length];
            _phasesB = new double[_partialRatios.Length];

            _modType = preset.ModType;
            _modSpeedHz = preset.ModSpeedHz;
            _modDepth = preset.ModDepth;

            _chorusDetuneRatio = Math.Pow(2.0, preset.ChorusDetuneCents / 1200.0);
            _chorusEnabled = preset.ChorusDetuneCents != 0.0 || preset.PartialDetuneCentsB != null;

            bool hasPerPartialDetune =
                preset.PartialDetuneCentsB != null &&
                preset.PartialDetuneCentsB.Length == _partialRatios.Length;

            if (hasPerPartialDetune)
            {
                _perPartialDetuneRatio = new double[_partialRatios.Length];
                for (int i = 0; i < _partialRatios.Length; i++)
                {
                    _perPartialDetuneRatio[i] = Math.Pow(2.0, preset.PartialDetuneCentsB[i] / 1200.0);
                }
            }
            else
            {
                _perPartialDetuneRatio = null; // padne se na jednotné _chorusDetuneRatio
            }
            // Jednoduchý lineární crossfade: ChorusMix=0 -> jen hlas A (chór
            // vypnutý), ChorusMix=0.5 -> oba stejně hlasitě, ChorusMix=1 ->
            // jen rozladěný hlas B.
            double mix = Math.Clamp(preset.ChorusMix, 0.0, 1.0);
            _gainA = 1.0 - mix;
            _gainB = mix;

            // VARHANNÍ OBÁLKA:
            NoteEnvelope.AttackTime = 0.015f;  // Rychlý náběh
            NoteEnvelope.DecayTime = 0.05f;
            NoteEnvelope.SustainLevel = 1.0f;  // <--- DRŽÍ 100% HLASITOST POKUD DRŽÍŠ KLÁVESU!
            NoteEnvelope.ReleaseTime = 0.03f;  // Rychlé vypnutí po uvolnění

            _chiffFilter.SetParams(_preset.ChiffFilterFreqHz, _preset.ChiffFilterQ, sampleRate);
        }

        public override void NoteOn()
        {
            base.NoteOn();
            _chiffEnvelope = 1.0; // Reset obálky pro startovní zapraskání píšťaly
        }

        protected override double CalculateWaveform(double frequency)
        {
            double sum = 0.0;   // ZMĚNA: jeden mono signál místo tří pásem

            // AM/FM modulace (tremolo/vibrato) - společný pomalý LFO (ModSpeedHz),
            // podle ModType se uplatní buď na KMITOČET (FM = vibrato), nebo na
            // VÝSLEDNOU HLASITOST (AM = tremolo). POZOR: ModDepth má u těchhle
            // dvou úplně jiný rozsah, než jaký se používá jinde v projektu pro
            // "chorus v centech" (tam bývá ModDepth v jednotkách centů, klidně
            // desítky) - tady je to ZLOMEK (podíl), takže:
            //   FM: ModDepth ~ 0.002-0.01  (0,2-1 % kolísání kmitočtu - jemné vibrato)
            //   AM: ModDepth ~ 0.1-0.4     (10-40 % kolísání hlasitosti - slyšitelné tremolo)
            // Hodnoty jako 4.0 (centy odjinud v projektu) by u FM úplně rozladily
            // tón, u AM by zase přetáčely hlasitost do záporných hodnot (proto je
            // dole ošetřený Clamp).
            double effectiveFrequency = frequency;
            double amplitudeMultiplier = 1.0;

            if (_modType != ModulationType.None && _modSpeedHz > 0.0)
            {
                double modPhase = AdvancePhase(ref _modPhase, 1.0, _modSpeedHz);
                double modulator = Math.Sin(modPhase * 2.0 * Math.PI);

                if (_modType == ModulationType.FM)
                {
                    effectiveFrequency = frequency * (1.0 + modulator * _modDepth);
                }
                else if (_modType == ModulationType.AM)
                {
                    amplitudeMultiplier = Math.Clamp(1.0 + modulator * _modDepth, 0.0, 2.0);
                }
            }

            // 1. Zvuk alikvótních píšťal - stejné pole jako u Bell/AdditiveString
            // (PartialRatios/PartialAmplitudes), jen se tady VŮBEC nepoužívá
            // PartialDecayRates - foukaná píšťala hraje na konstantní hlasitosti,
            // dokud se drží klávesa, celý tón "zhasne" najednou přes ADSR
            // Release (viz konstruktor výš), ne alikvot po alikvotu. Čistá
            // sinusovka má přesně daný kmitočet, takže ji rovnou zařadíme do
            // správného pásma podle toho, kolik Hz doopravdy má - žádné
            // filtrování netřeba.
            //
            // STATICKÝ CHÓR: pokud je ChorusDetuneCents nastavené, CELÁ tahle
            // alikvotní řada se navíc přehraje podruhé, s pevně (ne kmitajícím
            // způsobem jako u AM/FM) rozladěnou frekvencí - přesně to, co bylo
            // naměřeno u Casio STRINGS (např. 2.9662x A 2.9865x zvlášť, ne
            // jeden kmitající pás). Výsledkem jsou zdvojené, těsně u sebe
            // ležící spektrální čáry - to "ztluštění", které AM/FM nikdy
            // nedokáže vytvořit, protože ty vytváří postranní pásma okolo
            // JEDNÉ čáry, ne dvě samostatné pevné čáry.
            for (int i = 0; i < _partialRatios.Length; i++)
            {
                double ratio = _partialRatios[i];
                double amp = _partialAmplitudes[i];

                double freqA = effectiveFrequency;
                double phaseA = AdvancePhase(ref _phasesA[i], ratio, freqA);
                double valueA = Math.Sin(phaseA * 2.0 * Math.PI) * amp * amplitudeMultiplier * _gainA;
                sum += valueA;

                if (_chorusEnabled)
                {
                    double detuneRatio = _perPartialDetuneRatio != null
                        ? _perPartialDetuneRatio[i]
                        : _chorusDetuneRatio;

                    double freqB = effectiveFrequency * detuneRatio;
                    double phaseB = AdvancePhase(ref _phasesB[i], ratio, freqB);
                    double valueB = Math.Sin(phaseB * 2.0 * Math.PI) * amp * amplitudeMultiplier * _gainB;
                    sum += valueB;
                }
            }

            // 2. Chiff (zapraskání vzduchu při otevření ventilu) - barva chiffu
            // zůstává stejná jako dosud (tvaruje ji _chiffFilter rejstříku),
            // ale protože je to šum, jeho energii je nutné doopravdy rozdělit
            // mezi pásma skutečnou výhybkou (_chiffCrossover), ne jen podle
            // jednoho kmitočtu.
            if (_chiffEnvelope > 0.001)
            {
                double noise = _noiseGen.NextSample((int)SampleRate);
                double shapedChiff = _chiffFilter.Process(noise) * _chiffEnvelope * _preset.ChiffNoiseGain;

                sum += shapedChiff;

                _chiffEnvelope *= Math.Exp(-1.0 / (SampleRate * _preset.ChiffDurationSec));
            }

            return sum;
        }
    }
}
