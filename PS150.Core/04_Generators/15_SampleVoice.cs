using System;
using System.Collections.Generic;
using PS150.Core.Sampler;

namespace PS150.Core.Generators
{
    /// <summary>
    /// krok17: hlas přehrávající vzorky ze SF2. Jedna nota = jedna až několik zón (stereo pár L+R,
    /// případně vrstvy). Výška se počítá z přesného kmitočtu, který dodá ToneEngine (už včetně
    /// temperatury / ladění A4 / AJI), takže vzorky ladí stejně jako syntetické rejstříky.
    ///
    /// Umí: smyčku (režim 1 a 3), nesmyčkované jednorázové vzorky (klavír, bicí), dobu náběhu a
    /// doznění z banky, exkluzivní třídy (hi-hat). Stereo se zatím sčítá do mona.
    /// Neumí: modulátory SF2, filtr, vibrato LFO, decay/sustain obálku (jen náběh a doznění).
    /// </summary>
    public class SampleVoice : SynthVoice
    {
        private sealed class Player
        {
            public Sf2Zone Zone = null!;
            public short[] Pcm = Array.Empty<short>();   // krok21: společné vzorky banky
            public int Start;                            // začátek vzorku v Pcm
            public int Length;
            public double Pos;
            public bool Ended;
            public bool Looping;
            public double RootFreq;
            public double CentsFactor;
            public double SrcRate;
            public double Gain;
        }

        private readonly List<Player> _players = new List<Player>();
        private bool _allEnded;

        /// <summary>Exkluzivní třída (0 = žádná). Nová nota stejné třídy v témže rejstříku tuhle umlčí.</summary>
        public int ExclusiveClass { get; }

        public SampleVoice(IReadOnlyList<Sf2Zone> zones, double sampleRate) : base(sampleRate)
        {
            float attack = 0.002f, release = 0.01f;
            int excl = 0;

            foreach (var z in zones)
            {
                if (z.Length < 2) continue;

                _players.Add(new Player
                {
                    Zone = z,
                    Pcm = z.Pcm,
                    Start = z.Start,
                    Length = z.Length,
                    Looping = z.Loops,
                    RootFreq = 440.0 * Math.Pow(2.0, (z.RootKey - 69) / 12.0),
                    CentsFactor = Math.Pow(2.0, z.TuneCents / 1200.0),
                    SrcRate = z.SampleRate,
                    Gain = z.Gain * (z.Stereo ? 0.5 : 1.0)   // stereo pár L+R se sčítá do mona
                });

                if (_players.Count == 1) { attack = z.AttackSec; release = z.ReleaseSec; }
                if (z.ExclusiveClass > 0) excl = z.ExclusiveClass;
            }

            ExclusiveClass = excl;

            NoteEnvelope.AttackTime = attack;
            NoteEnvelope.DecayTime = 0.0f;
            NoteEnvelope.SustainLevel = 1.0f;
            NoteEnvelope.ReleaseTime = release;

            if (_players.Count == 0) _allEnded = true;
        }

        public override bool IsFinished => HasStarted && (_allEnded || !NoteEnvelope.IsActive);

        public override void NoteOn()
        {
            foreach (var p in _players) { p.Pos = 0; p.Ended = false; }
            _allEnded = _players.Count == 0;
            base.NoteOn();
        }

        public override void NoteOff()
        {
            // Režim 3: po puštění klávesy se smyčka opustí a vzorek dohraje do konce.
            foreach (var p in _players)
                if ((p.Zone.Mode & 3) == 3) p.Looping = false;

            base.NoteOff();
        }

        /// <summary>Rychle umlčí hlas (exkluzivní třída, opakovaný úder stejné noty).</summary>
        public void Choke()
        {
            NoteEnvelope.ReleaseTime = 0.02f;
            base.NoteOff();
        }

        protected override double CalculateWaveform(double frequency)
        {
            double sum = 0.0;
            bool anyActive = false;

            foreach (var p in _players)
            {
                if (p.Ended) continue;

                int i = (int)p.Pos;
                if (i + 1 >= p.Length) { p.Ended = true; continue; }

                anyActive = true;

                double frac = p.Pos - i;
                int idx = p.Start + i;
                double s0 = p.Pcm[idx], s1 = p.Pcm[idx + 1];
                sum += (s0 + (s1 - s0) * frac) * (p.Gain / 32768.0);

                // Poměr čtení: požadovaný kmitočet / kmitočet kořenové noty, přepočet vzorkovacích kmitočtů.
                double ratio = frequency / p.RootFreq * p.CentsFactor * p.SrcRate / SampleRate;
                p.Pos += ratio;

                if (p.Looping)
                {
                    int loopLen = p.Zone.LoopEnd - p.Zone.LoopStart;
                    while (p.Pos >= p.Zone.LoopEnd) p.Pos -= loopLen;
                }
            }

            if (!anyActive) _allEnded = true;
            return sum;
        }
    }
}
