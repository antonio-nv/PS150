using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace PS150.Core.Mixers
{
    /// <summary>
    /// krok104: sečte NĚKOLIK zvukových zdrojů (stejný vzorkovací kmitočet i
    /// počet kanálů) do JEDNOHO proudu, vzorek po vzorku. Vznikl pro .mid
    /// přehrávač (GmPianoMidiPlayer) - ten má dnes DVA nezávislé enginy
    /// najednou (tónový + bicí, viz DualEngineMidiOut), každý se svým vlastním
    /// zvukovým výstupem (ISampleProvider), ale na zvukovku (přes sdílený
    /// mixpult+ekvalizér) má jít jen JEDEN společný proud - přesně na to je
    /// tahle třída.
    ///
    /// Žádné měkké mixování ani komprese - čisté sečtení vzorků. Přebuzení
    /// (součet dvou už samy o sobě hlasitých enginů) se nechá být slyšet/
    /// vidět na VU metru, stejně jako všude jinde v projektu (viz MonoChannel,
    /// Sf2MidiSynth atd.) - NEOŘEZÁVÁ se tu natvrdo na -1..1, to dořeší až
    /// navazující zpracování (MonoChannel/EQ) nebo zvuková karta, stejně jako
    /// u .mp3 řetězce v AudioPlayer.
    /// </summary>
    public sealed class SummingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider[] _sources;
        private float[] _scratch = Array.Empty<float>();

        public WaveFormat WaveFormat { get; }

        public SummingSampleProvider(IReadOnlyList<ISampleProvider> sources)
        {
            if (sources == null || sources.Count == 0)
                throw new ArgumentException("Musí být zadán aspoň jeden zdroj.", nameof(sources));

            WaveFormat = sources[0].WaveFormat;
            for (int i = 1; i < sources.Count; i++)
            {
                if (sources[i].WaveFormat.SampleRate != WaveFormat.SampleRate
                    || sources[i].WaveFormat.Channels != WaveFormat.Channels)
                {
                    throw new ArgumentException(
                        "Všechny zdroje musí mít stejný vzorkovací kmitočet i počet kanálů.", nameof(sources));
                }
            }

            _sources = new ISampleProvider[sources.Count];
            for (int i = 0; i < sources.Count; i++) _sources[i] = sources[i];
        }

        /// <summary>Přečte ze VŠECH zdrojů a sečte je do společného bufferu. Vrací nejdelší skutečně přečtený počet vzorků (kratší zdroje dál mlčí nulami).</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);

            if (_scratch.Length < count) _scratch = new float[count];

            int maxRead = 0;
            foreach (var source in _sources)
            {
                int read = source.Read(_scratch, 0, count);
                for (int i = 0; i < read; i++) buffer[offset + i] += _scratch[i];
                if (read > maxRead) maxRead = read;
            }

            return maxRead;
        }
    }
}
