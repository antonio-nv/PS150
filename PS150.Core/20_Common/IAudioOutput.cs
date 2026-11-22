using System;

namespace PS150.Core.Common
{
    /// <summary>
    /// Výstup zvuku do zvukové karty (nebo kamkoliv jinam), nezávislý na platformě.
    /// Konkrétní implementace leží ve složce 06_Output (ALSA pro Linux, později
    /// třeba síťový výstup). Čistá logika (výhybka, mixér, syntéza) zná jen
    /// tohle rozhraní, takže se dá zkoušet i bez zvukové karty.
    /// </summary>
    public interface IAudioOutput : IDisposable
    {
        /// <summary>Vzorkovací kmitočet v Hz.</summary>
        int SampleRate { get; }

        /// <summary>Počet výstupních kanálů (např. 6 pro 5.1 kartu ASRocku).</summary>
        int Channels { get; }

        /// <summary>
        /// Zapíše vzorky ve formátu "interleaved" (kanál 0, kanál 1, ..., kanál N-1,
        /// pak další snímek). Počet snímků je interleaved.Length / Channels.
        /// Volání BLOKUJE, dokud karta nepřijme všechna data - tím se přirozeně
        /// určuje tempo přehrávání, žádné časovače navíc nejsou potřeba.
        /// </summary>
        void Write(ReadOnlySpan<float> interleaved);
    }
}
