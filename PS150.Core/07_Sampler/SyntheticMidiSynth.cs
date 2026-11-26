using Melanchall.DryWetMidi.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok41: engine pro přehrávání .mid souborů přes VLASTNÍ syntézu -
    /// obaluje přímo PS150.Core.ToneEngine.ToneEngine (banka 1 = Syntetické
    /// zvuky/tóny, banka 9 = Syntetické zvuky/bicí, viz Banky.cs). Na rozdíl
    /// od Sf2MidiSynth/OplMidiSynth se tu NEPOUŽÍVÁ žádná cizí vzorková/čipová
    /// banka - je to přesně ten samý kód, co hraje živě z MIDI-IN klávesnice.
    /// Přehrávání .mid souboru je tak zároveň první poslechový test tvé
    /// vlastní syntézy (Melzerovy rejstříky, klavíry, cembalo, zvon, smyčce,
    /// GM bicí souprava), ne jen další demo.
    ///
    /// Jedna instance = buď TÓNOVÝ engine (ToneEngine konstruktor sám od sebe
    /// zapne výchozí rejstřík banka1/číslo1 = Bombard16 - viz ToneEngine()),
    /// nebo BICÍ engine (žádné zapínání netřeba - ToneEngine na kanálu D10
    /// VŽDY hraje DrumRegister, banka 9/číslo 1 = _001_Drums, bez ohledu na
    /// aktivní rejstříky - viz ToneEngine.NoteOn). O to, která instance
    /// dostane která MIDI data, se stará DualEngineMidiOut (bicí kanál D10
    /// zvlášť), stejně jako u Sf2MidiSynth/OplMidiSynth.
    ///
    /// krok41 - PRVNÍ VERZE, vědomá zjednodušení (ať to jde HNED vyzkoušet,
    /// doladí se podle toho, jak to zní):
    ///  - tónová instance hraje VŽDY jen ten jeden natvrdo aktivovaný
    ///    rejstřík (dnes výchozí Bombard16) na všech netbicích kanálech.
    ///    Program Change ze souboru se ignoruje - GM čísla nástrojů (0-127)
    ///    stejně nemají žádný vztah k číslům tvých Melzerových rejstříků
    ///    (1, 2, 85, 108, 200...), takže mapovat by se musely ručně, případ
    ///    od případu - to je oddělená budoucí práce.
    ///  - ladění/AJI/pitch bend z přehrávače se na tenhle engine zatím
    ///    NEAPLIKUJE - ToneEngine.NoteOn počítá kmitočet jen z čísla noty a
    ///    temperamentu rejstříku, ne z kmitočtu poslaného zvenčí.
    /// Všechno výš je řešitelné příště, až se ukáže, jak to vůbec zní.
    ///
    /// krok42: hlavní hlasitost (Vol, šipky nahoru/dolů) ovládá přehrávač
    /// posláním CC7 na všech 16 kanálů najednou (viz GmPianoMidiPlayer.
    /// SendVolumeToAllChannels) - proto stačí jediná sdílená hodnota, ne
    /// pole 16 hlasitostí po kanálech jako u Sf2MidiSynth. Použita stejná
    /// kvadratická GM křivka (vol/127)^2 jako u Sf2MidiSynth, aplikovaná až
    /// na už smíchaný výstup ToneEngine.GenerateNextSample (ten engine si
    /// hlasitost jednotlivých kanálů/not mezi sebou nerozlišuje).
    ///
    /// krok44: pole UniR teď u téhle banky neznamená "GM program 0-127", ale
    /// PŘÍMO číslo rejstříku z ToneEngine (Melzer 1-200, dál 200/300/400/
    /// 500/501/600...) - viz ISupportsSyntheticRegister/SetSyntheticRegister.
    /// Nastaví se přes ToneEngine.SetSoleActiveRegister (vypne ostatní
    /// rejstříky, zapne jen tenhle) - kombinování víc rejstříků najednou
    /// (tutti/pléno) zůstává jen živému hraní, pro .mid soubor stačí jeden.
    /// </summary>
    public sealed class SyntheticMidiSynth : ISampleProvider, IMidiOut, ISupportsSyntheticRegister
    {
        private const int Rate = 44100;
        private const int VolumeControlNumber = 7;   // GM/MIDI CC7 = Channel Volume

        private readonly PS150.Core.ToneEngine.ToneEngine _engine;
        private readonly int _bankNumber;
        private WaveOutEvent? _waveOut;
        private volatile float _volumeGain = 1.0f;    // (127/127)^2, dokud nepřijde CC7

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public string Description { get; }

        private SyntheticMidiSynth(string description, int bankNumber)
        {
            _engine = new PS150.Core.ToneEngine.ToneEngine(Rate);
            _bankNumber = bankNumber;
            Description = description;
        }

        /// <summary>krok41: isDrumEngine jen kvůli popisku - ToneEngine sám pozná bicí kanál (D10) podle čísla kanálu, viz SendEvent.</summary>
        public static SyntheticMidiSynth Create(bool isDrumEngine)
        {
            var synth = new SyntheticMidiSynth(
                isDrumEngine
                    ? "Syntetické zvuky, bicí (ToneEngine)"
                    : "Syntetické zvuky, tóny (ToneEngine)",
                isDrumEngine
                    ? PS150.Core.Banks._009_SynteticBici.Number
                    : PS150.Core.Banks._001_SyntetickeZvuky.Number);

            synth._waveOut = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 4 };
            synth._waveOut.Init(new SampleToWaveProvider16(synth));
            synth._waveOut.Play();
            return synth;
        }

        /// <summary>
        /// krok44: viz ISupportsSyntheticRegister. 0 nebo neplatné (neexistující)
        /// číslo se tiše ignoruje - ToneEngine.SetSoleActiveRegister sám nic
        /// nezmění, zůstane hrát to, co bylo aktivní předtím.
        /// </summary>
        public void SetSyntheticRegister(int registerNumber)
        {
            if (registerNumber <= 0) return;
            _engine.SetSoleActiveRegister(new PS150.Core.Input.RegisterId(_bankNumber, registerNumber));
        }

        public void SendEvent(MidiEvent midiEvent)
        {
            switch (midiEvent)
            {
                case NoteOnEvent n when n.Velocity > 0:
                    _engine.NoteOn((int)n.NoteNumber, (int)n.Channel, (int)n.Velocity / 127.0);
                    break;

                case NoteOnEvent n:   // velocity 0 = NoteOff
                    _engine.NoteOff((int)n.NoteNumber, (int)n.Channel);
                    break;

                case NoteOffEvent n:
                    _engine.NoteOff((int)n.NoteNumber, (int)n.Channel);
                    break;

                case ControlChangeEvent cc when cc.ControlNumber == VolumeControlNumber:
                    double vol = (int)cc.ControlValue / 127.0;
                    _volumeGain = (float)(vol * vol);   // GM křivka hlasitosti (kvadratická)
                    break;

                // Program Change / ostatní Control Change / pitch bend zatím
                // záměrně ignorovány - viz komentář u třídy výš.
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            float gain = _volumeGain;
            int frames = count / 2;
            for (int i = 0; i < frames; i++)
            {
                float s = (float)_engine.GenerateNextSample() * gain;   // mono, ToneEngine ořezává na -1..1 ještě před hlasitostí
                buffer[offset + 2 * i] = s;
                buffer[offset + 2 * i + 1] = s;
            }
            return count;
        }

        public void Dispose()
        {
            var waveOut = _waveOut;
            _waveOut = null;
            waveOut?.Stop();
            waveOut?.Dispose();
        }
    }
}
