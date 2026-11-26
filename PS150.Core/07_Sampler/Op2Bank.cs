using System;
using System.IO;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok35: nastavení jednoho operátoru (modulátor nebo nosič) z .op2 banky -
    /// přímo odpovídá šesti bajtům v souboru, žádné přepočty. Jména polí podle
    /// formální specifikace (https://formats.kaitai.io/genmidi_op2, CC0-1.0;
    /// zdroj: https://moddingwiki.shikadi.net/wiki/OP2_Bank_Format).
    /// </summary>
    public readonly struct Op2Operator
    {
        public readonly byte TremVibr;   // reg 0x20+op: AM(7)/VIB(6)/EG-type(5)/KSR(4)/Multi(3-0)
        public readonly byte AttDec;     // reg 0x60+op: Attack(7-4)/Decay(3-0)
        public readonly byte SustRel;    // reg 0x80+op: Sustain(7-4)/Release(3-0)
        public readonly byte Wave;       // reg 0xE0+op: Waveform(2-0)
        public readonly byte Scale;      // reg 0x40+op horní 2 bity: Key Scale Level
        public readonly byte Level;      // reg 0x40+op dolní 6 bitů: Total Level (0=nejhlasitěji, 63=ticho)

        public Op2Operator(byte tremVibr, byte attDec, byte sustRel, byte wave, byte scale, byte level)
        {
            TremVibr = tremVibr; AttDec = attDec; SustRel = sustRel; Wave = wave; Scale = scale; Level = level;
        }
    }

    /// <summary>krok35: jeden hlas (modulátor+nosič+feedback) - instrument má 1 nebo 2 (double-voice).</summary>
    public readonly struct Op2Voice
    {
        public readonly Op2Operator Modulator;
        public readonly byte Feedback;   // reg 0xC0+kanál: Feedback(3-1)/Connection(0) - sdílené pro oba operátory kanálu
        public readonly Op2Operator Carrier;
        public readonly short NoteOffset;   // posun noty pro tenhle hlas (detune u double-voice), v půltónech*? - viz Op2Bank.NoteOffsetSemitones

        public Op2Voice(Op2Operator modulator, byte feedback, Op2Operator carrier, short noteOffset)
        {
            Modulator = modulator; Feedback = feedback; Carrier = carrier; NoteOffset = noteOffset;
        }
    }

    /// <summary>krok35: jeden nástroj z .op2 banky (175 celkem: 0-127 GM melodické, 128-174 bicí).</summary>
    public sealed class Op2Instrument
    {
        public ushort Flags { get; }
        public bool FixedNote => (Flags & 0x0001) != 0;
        public bool DoubleVoice => (Flags & 0x0004) != 0;
        public byte Finetune { get; }       // krok35: detune druhého hlasu u double-voice, 128 = beze změny
        public byte Note { get; }           // pevná nota (bicí, nebo FixedNote nástroje)
        public Op2Voice Voice1 { get; }
        public Op2Voice Voice2 { get; }      // platný jen když DoubleVoice
        public string Name { get; }

        public Op2Instrument(ushort flags, byte finetune, byte note, Op2Voice voice1, Op2Voice voice2, string name)
        {
            Flags = flags; Finetune = finetune; Note = note; Voice1 = voice1; Voice2 = voice2; Name = name;
        }
    }

    /// <summary>
    /// krok35: načtená .op2 banka (DMX/Doom GENMIDI formát) - 128 GM melodických
    /// nástrojů (index 0-127, pořadí přesně podle GM) + 47 bicích (index 128-174,
    /// odpovídá GM notám 35-81). Binární formát ověřený proti formální
    /// specifikaci (ne z paměti) - viz komentáře u Op2Operator/Op2Voice.
    /// </summary>
    public sealed class Op2Bank
    {
        private const int InstrumentCount = 175;
        private const int PercussionBase = 128;
        public const int PercussionFirstNote = 35;   // Acoustic Bass Drum
        public const int PercussionLastNote = 81;    // Open Triangle

        public Op2Instrument[] Melodic { get; } = new Op2Instrument[128];
        public Op2Instrument[] Percussion { get; } = new Op2Instrument[PercussionLastNote - PercussionFirstNote + 1];

        private Op2Bank() { }

        /// <summary>Nástroj pro bicí notu (GM 35-81), nebo null mimo rozsah.</summary>
        public Op2Instrument? PercussionForNote(int note)
        {
            int i = note - PercussionFirstNote;
            return (i >= 0 && i < Percussion.Length) ? Percussion[i] : null;
        }

        public static Op2Bank Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            byte[] magic = reader.ReadBytes(8);
            string magicText = System.Text.Encoding.ASCII.GetString(magic);
            if (magicText != "#OPL_II#")
                throw new InvalidDataException($"'{Path.GetFileName(path)}' nevypadá jako .op2 banka (chybí hlavička #OPL_II#).");

            var entries = new (ushort Flags, byte Finetune, byte Note, Op2Voice V1, Op2Voice V2)[InstrumentCount];
            for (int i = 0; i < InstrumentCount; i++)
            {
                ushort flags = reader.ReadUInt16();
                byte finetune = reader.ReadByte();
                byte note = reader.ReadByte();
                var v1 = ReadVoice(reader, finetune);
                var v2 = ReadVoice(reader, finetune);
                entries[i] = (flags, finetune, note, v1, v2);
            }

            var names = new string[InstrumentCount];
            for (int i = 0; i < InstrumentCount; i++)
            {
                byte[] raw = reader.ReadBytes(32);
                int len = Array.IndexOf(raw, (byte)0);
                if (len < 0) len = raw.Length;
                names[i] = System.Text.Encoding.ASCII.GetString(raw, 0, len).Trim();
            }

            var bank = new Op2Bank();
            for (int i = 0; i < 128; i++)
            {
                var e = entries[i];
                bank.Melodic[i] = new Op2Instrument(e.Flags, e.Finetune, e.Note, e.V1, e.V2, names[i]);
            }
            for (int i = PercussionBase; i < InstrumentCount; i++)
            {
                var e = entries[i];
                bank.Percussion[i - PercussionBase] = new Op2Instrument(e.Flags, e.Finetune, e.Note, e.V1, e.V2, names[i]);
            }
            return bank;
        }

        private static Op2Voice ReadVoice(BinaryReader reader, byte finetune)
        {
            var modulator = ReadOperator(reader);
            byte feedback = reader.ReadByte();
            var carrier = ReadOperator(reader);
            reader.ReadByte();              // unused
            short noteOffset = reader.ReadInt16();
            return new Op2Voice(modulator, feedback, carrier, noteOffset);
        }

        private static Op2Operator ReadOperator(BinaryReader reader)
        {
            byte tremVibr = reader.ReadByte();
            byte attDec = reader.ReadByte();
            byte sustRel = reader.ReadByte();
            byte wave = reader.ReadByte();
            byte scale = reader.ReadByte();
            byte level = reader.ReadByte();
            return new Op2Operator(tremVibr, attDec, sustRel, wave, scale, level);
        }
    }
}
