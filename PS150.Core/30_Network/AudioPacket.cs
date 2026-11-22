using System;
using System.Buffers.Binary;

namespace PS150.Core.Network
{
    /// <summary>Formát vzorků v paketu.</summary>
    public enum AudioSampleFormat : byte
    {
        /// <summary>32bitový float, little endian (výchozí; přesně to, co dává zachytávání zvuku ve Windows).</summary>
        Float32 = 0,

        /// <summary>16bitové celé číslo se znaménkem, little endian (poloviční přenosová rychlost).</summary>
        Int16 = 1
    }

    /// <summary>
    /// Jeden paket zvuku pro přenos PC -> externí zvuková karta (ASRock, RP5) po UDP.
    ///
    /// Záměrně vlastní jednoduchý formát (ne RTP): máme jen jednoho odesílatele
    /// a jednoho příjemce v domácí síti a potřebujeme přesně tohle:
    /// číslo paketu (poznáme ztrátu), pozici ve zvukovém proudu v počtu vzorků
    /// (příjemce z ní sestaví souvislý zvuk i při přeházených paketech) a
    /// identifikátor relace (poznáme restart odesílatele).
    ///
    /// Rozložení hlavičky (28 bajtů, vše little endian):
    ///   0   2  magie 'P','S'
    ///   2   1  verze protokolu (1)
    ///   3   1  formát vzorků (AudioSampleFormat)
    ///   4   1  počet kanálů
    ///   5   1  příznaky (zatím 0)
    ///   6   2  počet snímků (vzorků na kanál) v paketu
    ///   8   4  vzorkovací kmitočet v Hz
    ///   12  4  identifikátor relace (náhodné číslo, mění se při každém spuštění odesílatele)
    ///   16  4  pořadové číslo paketu (od 0, přetéká)
    ///   20  8  číslo prvního snímku v proudu (od 0, nepřetéká)
    ///   28  .. vzorky, prokládaně po kanálech
    ///
    /// Pro 2 kanály a 128 snímků je to 28 + 1024 = 1052 bajtů, takže se paket
    /// vejde do jednoho ethernetového rámce (MTU 1500) bez fragmentace.
    /// </summary>
    public sealed class AudioPacket
    {
        public const int HeaderSize = 28;
        public const byte ProtocolVersion = 1;

        public AudioSampleFormat Format { get; init; }
        public int Channels { get; init; }
        public int SampleRate { get; init; }
        public uint StreamId { get; init; }
        public uint Sequence { get; init; }
        public long FirstFrame { get; init; }
        public int FrameCount { get; init; }

        /// <summary>Vzorky jako float -1..1, prokládaně (FrameCount * Channels hodnot).</summary>
        public float[] Samples { get; init; } = Array.Empty<float>();

        public static int BytesPerSample(AudioSampleFormat format) =>
            format == AudioSampleFormat.Int16 ? 2 : 4;

        /// <summary>Kolik bajtů zabere paket s daným počtem snímků a kanálů.</summary>
        public static int EncodedSize(int frames, int channels, AudioSampleFormat format) =>
            HeaderSize + frames * channels * BytesPerSample(format);

        /// <summary>
        /// Zapíše paket do <paramref name="destination"/> a vrátí počet zapsaných bajtů.
        /// Počet vzorků musí být násobkem počtu kanálů.
        /// </summary>
        public static int Encode(
            Span<byte> destination,
            uint streamId, uint sequence, long firstFrame,
            int sampleRate, int channels, AudioSampleFormat format,
            ReadOnlySpan<float> samples)
        {
            if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
            if (samples.Length == 0 || samples.Length % channels != 0)
                throw new ArgumentException("Počet vzorků musí být nenulový násobek počtu kanálů.", nameof(samples));

            int frames = samples.Length / channels;
            if (frames > ushort.MaxValue) throw new ArgumentException("Příliš mnoho snímků v jednom paketu.", nameof(samples));

            int total = EncodedSize(frames, channels, format);
            if (destination.Length < total) throw new ArgumentException("Cílový buffer je malý.", nameof(destination));

            destination[0] = (byte)'P';
            destination[1] = (byte)'S';
            destination[2] = ProtocolVersion;
            destination[3] = (byte)format;
            destination[4] = (byte)channels;
            destination[5] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6), (ushort)frames);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(8), sampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12), streamId);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(20), firstFrame);

            int offset = HeaderSize;
            if (format == AudioSampleFormat.Float32)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(offset), samples[i]);
                    offset += 4;
                }
            }
            else
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    float v = samples[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(offset), (short)(v * 32767f));
                    offset += 2;
                }
            }

            return total;
        }

        /// <summary>
        /// Přečte paket. Vrací false, pokud data nejsou platný paket (špatná magie,
        /// verze, délka...). Nikdy nevyhazuje výjimku kvůli cizím datům v síti.
        /// </summary>
        public static bool TryDecode(ReadOnlySpan<byte> data, out AudioPacket? packet)
        {
            packet = null;

            if (data.Length < HeaderSize) return false;
            if (data[0] != (byte)'P' || data[1] != (byte)'S') return false;
            if (data[2] != ProtocolVersion) return false;

            var format = (AudioSampleFormat)data[3];
            if (format != AudioSampleFormat.Float32 && format != AudioSampleFormat.Int16) return false;

            int channels = data[4];
            if (channels < 1 || channels > 32) return false;

            int frames = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
            if (frames == 0) return false;

            int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8));
            if (sampleRate < 8000 || sampleRate > 384000) return false;

            if (data.Length != EncodedSize(frames, channels, format)) return false;

            uint streamId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16));
            long firstFrame = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(20));
            if (firstFrame < 0) return false;

            var samples = new float[frames * channels];
            int offset = HeaderSize;

            if (format == AudioSampleFormat.Float32)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    float v = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset));
                    // cizí/poškozená data: NaN a nekonečno by otrávily celý výstup
                    samples[i] = float.IsFinite(v) ? v : 0f;
                    offset += 4;
                }
            }
            else
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset)) / 32768f;
                    offset += 2;
                }
            }

            packet = new AudioPacket
            {
                Format = format,
                Channels = channels,
                SampleRate = sampleRate,
                StreamId = streamId,
                Sequence = sequence,
                FirstFrame = firstFrame,
                FrameCount = frames,
                Samples = samples
            };
            return true;
        }
    }
}
