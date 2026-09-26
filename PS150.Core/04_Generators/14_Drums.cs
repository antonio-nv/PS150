using PS150.Core.Filters;

namespace PS150.Core.Generators
{
    /// <summary>
    /// Syntetizovaná bicí souprava (GM 35-81) - bílý šum protažený buď
    /// širokopásmovým filtrem (dolní+horní propust v sérii), nebo
    /// rezonančním úzkopásmovým filtrem, podle DrumParameter.Method.
    ///
    /// Na rozdíl od ostatních nástrojů NEHRAJE podle frekvence noty -
    /// číslo MIDI noty (viz konstruktor) přímo VYBÍRÁ, který z
    /// DrumParameter záznamů v preset.DrumParameters se použije (36 =
    /// kopák, 38 = virbl...). Parametr `frequency` v CalculateWaveform
    /// se proto ignoruje.
    ///
    /// Obálka (útok/dozvuk) je sdílená přes preset.Envelope jako u
    /// ostatních nástrojů - všech 47 zvuků v jednom rejstříku tak zatím
    /// sdílí stejný tvar ADSR (viz konverzace - "obálku bych nechal od
    /// dřívějška"). Rozdílnost délky dozvuku mezi jednotlivými bicími jde
    /// částečně vyladit přes Q u NarrowBandpass (vyšší Q = déle dozvučí).
    /// </summary>
    public class DrumVoice : SynthVoice
    {
        private readonly NoiseGenerator _noise = new NoiseGenerator();
        private readonly LowPassFilter _lowPass = new LowPassFilter();
        private readonly HighPassFilter _highPass = new HighPassFilter();
        private readonly BandPassFilter _narrowBand = new BandPassFilter();
        private readonly ThreeBandCrossover _crossover;

        private readonly DrumParameter _param;
        private readonly bool _found;

        public DrumVoice(VoicePreset preset, int noteNumber, double sampleRate) : base(sampleRate)
        {
            _crossover = new ThreeBandCrossover(sampleRate);

            if (preset?.Envelope != null)
            {
                NoteEnvelope.AttackTime = preset.Envelope.AttackTime;
                NoteEnvelope.DecayTime = preset.Envelope.DecayTime;
                NoteEnvelope.SustainLevel = preset.Envelope.SustainLevel;
                NoteEnvelope.ReleaseTime = preset.Envelope.ReleaseTime;
            }
            else
            {
                // Rozumný výchozí "úderový" tvar, když preset obálku
                // nepřepisuje - okamžitý úder, krátký dozvuk, žádné
                // sustain (bicí nezní dál, dokud se drží klávesa).
                NoteEnvelope.AttackTime = 0.001f;
                NoteEnvelope.DecayTime = 0.25f;
                NoteEnvelope.SustainLevel = 0.0f;
                NoteEnvelope.ReleaseTime = 0.05f;
            }

            _found = false;
            if (preset?.DrumParameters != null)
            {
                foreach (var p in preset.DrumParameters)
                {
                    if (p.GmNoteNumber == noteNumber)
                    {
                        _param = p;
                        _found = true;
                        break;
                    }
                }
            }

            if (_found)
            {
                _lowPass.SetSampleRate((float)sampleRate);
                _lowPass.SetCutoff((float)_param.HighHz);   // Horní propust pásma = LowPassFilter na HORNÍM kmitočtu
                _highPass.SetSampleRate((float)sampleRate);
                _highPass.SetCutoff((float)_param.LowHz);   // Dolní propust pásma = HighPassFilter na DOLNÍM kmitočtu
                _narrowBand.SetParams(_param.CenterHz, _param.Q, sampleRate);
            }
        }

        // Bicí nemají výšku tónu - GmNoteNumber (v konstruktoru) už vybral
        // zvuk, `frequency` se tu záměrně nepoužívá.
        protected override BandSample CalculateWaveform(double frequency)
        {
            if (!_found) return default; // Neznámé číslo noty v téhle sadě - ticho, ne pád

            float raw = _noise.NextSample((int)SampleRate);
            float filtered;

            if (_param.Method == DrumNoiseMethod.WideBandpass)
            {
                // V sérii: nejdřív odřízne vše pod LowHz (HighPassFilter),
                // pak vše nad HighHz (LowPassFilter) - zbyde pásmo mezi nimi.
                float afterHighPass = _highPass.Process(raw);
                filtered = _lowPass.Process(afterHighPass);
            }
            else
            {
                filtered = (float)_narrowBand.Process(raw);
            }

            var split = _crossover.Process(filtered * (float)_param.Amplitude);

            return new BandSample { Bass = split.Bass, Mid = split.Mid, Treble = split.Treble };
        }
    }
}
