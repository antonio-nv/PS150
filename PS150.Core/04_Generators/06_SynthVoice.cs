using System;

namespace PS150.Core.Generators
{
    public abstract class SynthVoice
    {
        protected readonly double SampleRate;
        protected readonly AdsrEnvelope NoteEnvelope = new AdsrEnvelope();
        protected bool HasStarted = false;

        /// <summary>NOVÉ: dynamika úhozu 0..1 (z MIDI velocity). Násobí výsledný vzorek hlasu.
        /// Nastavuje ToneEngine; u varhan zůstává 1 (píšťala nezná dynamiku úhozu).</summary>
        public double Velocity { get; set; } = 1.0;

        // Konec tónu: Až po stisku (HasStarted) a doznění obálky (Idle).
        // VIRTUAL - nástroje s vlastním fyzikálním modelem dozvuku (Bell, Cembalo)
        // si tohle mohou rozšířit o vlastní podmínku (pokles pod práh v dB),
        // místo aby konec tónu určovala jen sdílená ADSR obálka.
        public virtual bool IsFinished => HasStarted && !NoteEnvelope.IsActive;

        protected SynthVoice(double sampleRate)
        {
            SampleRate = sampleRate;
        }

        public virtual void NoteOn()
        {
            HasStarted = true;
            NoteEnvelope.TriggerGate(true);
        }

        public virtual void NoteOff()
        {
            NoteEnvelope.TriggerGate(false);
        }

        /// <summary>
        /// Pomocná jednotná metoda pro posun fáze oscilátoru (0.0 až 1.0)
        /// </summary>
        protected double AdvancePhase(ref double phase, double frequencyMult, double baseFreq)
        {
            phase += (baseFreq * frequencyMult) / SampleRate;
            if (phase >= 1.0) phase -= Math.Floor(phase);
            return phase;
        }

        /// <summary>
        /// Jednotné generování vzorku pro libovolný nástroj - jeden mono vzorek.
        /// ZMĚNA: dřív tři pásma (BandSample), teď jediný signál; dělení na
        /// hloubky/středy/výšky patří až na výstup (výhybka reproduktorů).
        /// </summary>
        public double GenerateSample(double frequency)
        {
            // Obálka se spočítá automaticky pro VŠECHNY nástroje stejně!
            double envelopeValue = NoteEnvelope.Process((int)SampleRate);

            // Pokud tón nehraje nebo dozněl, šetříme procesor
            if (envelopeValue <= 0.00001 && HasStarted) return default;

            // Zavolá specifický tvar vlny pro daný nástroj (už rozdělený do pásem)
            double rawWave = CalculateWaveform(frequency);

            return rawWave * envelopeValue * Velocity;
        }

        /// <summary>
        /// Každý nástroj zde definuje POUZE svůj jedinečný tvar vlny/zvuku (mono).
        /// </summary>
        protected abstract double CalculateWaveform(double frequency);
    }
}
