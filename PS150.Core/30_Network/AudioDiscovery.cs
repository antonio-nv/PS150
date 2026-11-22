using System;
using System.Buffers.Binary;

namespace PS150.Core.Network
{
    /// <summary>
    /// Pevné nastavení síťového přenosu zvuku (natvrdo v kódu).
    /// </summary>
    public static class NetworkDefaults
    {
        /// <summary>Adresa externí zvukovky (ASRock, později RP5).</summary>
        public const string ReceiverAddress = "192.168.0.240";

        /// <summary>UDP port, na kterém příjemce poslouchá (zvuk i dotazy "jsi tam?").</summary>
        public const int Port = 5150;

        /// <summary>Počet snímků v jednom paketu (128 při 48 kHz = 2,67 ms).</summary>
        public const int PacketFrames = 128;

        /// <summary>Jak často se příjemce ptáme, jestli je na síti.</summary>
        public const int ProbeIntervalMs = 1000;

        /// <summary>
        /// Když od příjemce tak dlouho nepřijde odpověď, považujeme ho za
        /// nedostupného a přehrávač se vrátí na zvukovku počítače.
        /// </summary>
        public const int TimeoutMs = 3000;

        /// <summary>Kolik odpovědí za sebou musí přijít, než se výstup přepne na síť (aby se nepřepínalo sem a tam).</summary>
        public const int RepliesToActivate = 2;
    }

    /// <summary>Co o sobě příjemce prozradí v odpovědi: s čím umí pracovat a jaké zpoždění přidá.</summary>
    public sealed class ReceiverInfo
    {
        /// <summary>Vzorkovací kmitočet, který příjemce očekává (Hz).</summary>
        public int SampleRate { get; }

        /// <summary>Počet kanálů přijímaného zvuku (zatím 2 = stereo).</summary>
        public int Channels { get; }

        /// <summary>Celkové zpoždění zvuku na straně příjemce v ms (rezerva proti kolísání sítě + buffer karty).</summary>
        public int LatencyMs { get; }

        public ReceiverInfo(int sampleRate, int channels, int latencyMs)
        {
            SampleRate = sampleRate;
            Channels = channels;
            LatencyMs = latencyMs;
        }
    }

    /// <summary>
    /// Maličké zprávy pro zjištění, že je příjemce na síti: dotaz ("jsi tam?")
    /// posílá přehrávač, odpověď ("jsem tu" + parametry) příjemce.
    /// Dotaz vychází z PC, takže odpověď projde i firewallem Windows
    /// (je to odpověď na odchozí spojení) a na PC nemusí nic poslouchat.
    ///
    /// Dotaz (8 bajtů):    'P','Q', verze, 0, náhodné číslo (u32)
    /// Odpověď (16 bajtů): 'P','A', verze, 0, totéž číslo (u32),
    ///                     vzorkovací kmitočet (i32), kanály (u8), 0, zpoždění v ms (u16)
    /// </summary>
    public static class AudioDiscovery
    {
        public const int ProbeSize = 8;
        public const int ReplySize = 16;
        private const byte Version = 1;

        public static int EncodeProbe(Span<byte> destination, uint nonce)
        {
            if (destination.Length < ProbeSize) throw new ArgumentException("Buffer je malý.", nameof(destination));

            destination[0] = (byte)'P';
            destination[1] = (byte)'Q';
            destination[2] = Version;
            destination[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4), nonce);
            return ProbeSize;
        }

        public static bool TryDecodeProbe(ReadOnlySpan<byte> data, out uint nonce)
        {
            nonce = 0;
            if (data.Length != ProbeSize) return false;
            if (data[0] != (byte)'P' || data[1] != (byte)'Q' || data[2] != Version) return false;

            nonce = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
            return true;
        }

        public static int EncodeReply(Span<byte> destination, uint nonce, ReceiverInfo info)
        {
            if (destination.Length < ReplySize) throw new ArgumentException("Buffer je malý.", nameof(destination));

            destination[0] = (byte)'P';
            destination[1] = (byte)'A';
            destination[2] = Version;
            destination[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4), nonce);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(8), info.SampleRate);
            destination[12] = (byte)info.Channels;
            destination[13] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(14), (ushort)Math.Clamp(info.LatencyMs, 0, ushort.MaxValue));
            return ReplySize;
        }

        public static bool TryDecodeReply(ReadOnlySpan<byte> data, out uint nonce, out ReceiverInfo? info)
        {
            nonce = 0;
            info = null;

            if (data.Length != ReplySize) return false;
            if (data[0] != (byte)'P' || data[1] != (byte)'A' || data[2] != Version) return false;

            nonce = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
            int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8));
            int channels = data[12];
            int latencyMs = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14));

            if (sampleRate < 8000 || sampleRate > 384000 || channels < 1 || channels > 32) return false;

            info = new ReceiverInfo(sampleRate, channels, latencyMs);
            return true;
        }
    }
}
