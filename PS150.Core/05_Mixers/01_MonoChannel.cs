using System;
using PS150.Core.Filters;
using PS150.Core.Dynamics;

namespace PS150.Core.Mixers
{
    /// <summary>
    /// krok85/86/87: ZÁKLADNÍ a VÝCHOZÍ typ mixpultového kanálu - jeden mono vstup,
    /// jeden mono výstup. Žádné Pan - u mono-mono kanálu nedává panoráma smysl
    /// (není kam panorámovat, výstup je jediný).
    ///
    /// Použití: mikrofonní vstupy, syntezátory, varhanní/samplerové hlasy - cokoliv,
    /// co samo o sobě mono je a zůstává (typicky domácí přehrávání na reprobednách
    /// přes jednu mono sběrnici). Viz také StereoChannel (stereo vstup, typicky
    /// zvuková karta PC) a MonoToStereoChannel (mono vstup, stereo výstup s Pan -
    /// specializovaný případ, např. sluchátkový výstup).
    ///
    /// krok86/87: pořadí zpracování je standardní praxe mixpultů - Gain -> HPF ->
    /// Gate -> Comp -> EQ (viz Process). HPF jde PŘED Gate/Comp proto, že by
    /// jinak gate/kompresor reagoval i na basový brum/rachot, který HPF stejně
    /// useká - nemá smysl na něj plýtvat dynamickým zpracováním. EQ jde AŽ PO
    /// Gate/Comp, aby případné zvýraznění/zeslabení pásma EQ neovlivnilo, kdy
    /// gate/kompresor sepne (ty reagují na hladinu PŘED EQ úpravou tónu).
    ///
    /// Gate a Comp mají vlastní plynulou obálku (attack/release, viz
    /// PS150.Core.Dynamics.EnvelopeFollower), takže nelupou při zapnutí/vypnutí -
    /// na rozdíl od Gain/HPF/EQ, kde je skoková změna hodnoty za chodu v pořádku.
    /// </summary>
    public sealed class MonoChannel
    {
        private readonly double _sampleRate;

        public const double MinGainDb = -24.0;
        public const double MaxGainDb = 24.0;

        private double _gainDb;
        private double _gainLinear = 1.0;

        /// <summary>Gain: standardní převod dB -> lineární násobič (10^(dB/20)).</summary>
        public double GainDb
        {
            get => _gainDb;
            set
            {
                _gainDb = Math.Clamp(value, MinGainDb, MaxGainDb);
                _gainLinear = Math.Pow(10.0, _gainDb / 20.0);
            }
        }

        // ---- krok86: HPF (horní propust) - odřízne basový brum/rachot pod mezním
        // kmitočtem. 0 = VYPNUTO (výchozí, signál beze změny), kladná hodnota =
        // zapnuto na dané mezi. Typické rozmezí 20-500Hz (řeč/varhany/mikrofony). ----
        private readonly Biquad _hpf = new();
        private double _hpfCutoffHz;

        public const double MinHpfCutoffHz = 20.0;
        public const double MaxHpfCutoffHz = 500.0;

        /// <summary>0 = HPF vypnutý (výchozí). Kladná hodnota (20-500Hz) HPF zapne na dané mezi.</summary>
        public double HpfCutoffHz
        {
            get => _hpfCutoffHz;
            set
            {
                _hpfCutoffHz = value <= 0.0 ? 0.0 : Math.Clamp(value, MinHpfCutoffHz, MaxHpfCutoffHz);
                if (_hpfCutoffHz > 0.0) _hpf.SetHighPass(_sampleRate, _hpfCutoffHz);
            }
        }

        // ---- krok87: Gate - viz formulář mixpultu, pole "Gate". Jediný vystavený
        // parametr je práh (ThresholdDb) - Attack/Release jsou pevně dané rozumné
        // výchozí hodnoty uvnitř NoiseGate (krátký nástup, delší povolení), ať se
        // to nezačne lupat, pokud nebude mít smysl je časem taky zpřístupnit. ----
        private readonly NoiseGate _gate;

        /// <summary>Práh brány v dB. -120 (výchozí) = prakticky vypnuto.</summary>
        public double GateThresholdDb
        {
            get => _gate.ThresholdDb;
            set => _gate.ThresholdDb = value;
        }

        // ---- krok87: Comp - viz formulář mixpultu, pole "Comp". Stejně jako u
        // Gate je jediný vystavený parametr práh (ThresholdDb), Ratio/Attack/
        // Release mají pevné rozumné výchozí hodnoty (4:1, rychlý nástup,
        // pomalejší povolení). ----
        private readonly Compressor _comp;

        /// <summary>Práh komprese v dB. 0 (výchozí) = prakticky vypnuto.</summary>
        public double CompThresholdDb
        {
            get => _comp.ThresholdDb;
            set => _comp.ThresholdDb = value;
        }

        // ---- krok86: jednopásmové parametrické EQ ("zvon") na kanálu - E.Ga/E.Fr/
        // E.Q z formuláře mixpultu. Jiná věc než 32pásmový grafický ekvalizér (ten
        // je na master výstupu, ne na kanálu) - viz PS150.Core.Equalizers. ----
        private readonly Biquad _eq = new();
        private double _eqGainDb;
        private double _eqFreqHz = 1000.0;
        private double _eqQ = 1.0;

        public const double MinEqGainDb = -12.0;
        public const double MaxEqGainDb = 12.0;
        public const double MinEqFreqHz = 20.0;
        public const double MaxEqFreqHz = 20000.0;

        /// <summary>E.Ga - zisk zvonu v dB (±12dB, stejný rozsah jako grafický ekvalizér).</summary>
        public double EqGainDb
        {
            get => _eqGainDb;
            set { _eqGainDb = Math.Clamp(value, MinEqGainDb, MaxEqGainDb); UpdateEq(); }
        }

        /// <summary>E.Fr - středový kmitočet zvonu v Hz.</summary>
        public double EqFreqHz
        {
            get => _eqFreqHz;
            set { _eqFreqHz = Math.Clamp(value, MinEqFreqHz, MaxEqFreqHz); UpdateEq(); }
        }

        /// <summary>E.Q - šířka zvonu (vyšší = užší/ostřejší). Výchozí 1,0 je rozumný střed.</summary>
        public double EqQ
        {
            get => _eqQ;
            set { _eqQ = Math.Max(0.1, value); UpdateEq(); }
        }

        private void UpdateEq() => _eq.SetPeakingEq(_sampleRate, _eqFreqHz, _eqGainDb, _eqQ);

        public MonoChannel(double sampleRate)
        {
            if (sampleRate <= 0.0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _sampleRate = sampleRate;
            _gate = new NoiseGate(sampleRate);
            _comp = new Compressor(sampleRate);
            UpdateEq();   // krok86: i při EqGainDb=0 musí mít _eq platné koeficienty (neutrální průchod)
        }

        /// <summary>Zpracuje jeden mono vzorek: Gain -> HPF (je-li zapnutý) -> Gate -> Comp -> EQ.</summary>
        public float Process(float monoSample)
        {
            double x = monoSample * _gainLinear;
            if (_hpfCutoffHz > 0.0) x = _hpf.Process(x);
            x = _gate.Process(x);
            x = _comp.Process(x);
            x = _eq.Process(x);
            return (float)x;
        }
    }
}
