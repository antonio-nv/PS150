using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PS150.Core.Banks;

namespace PS150.Core.Sampler
{
    /// <summary>
    /// krok17: načítání bank SF2 na pozadí. Zatímco se banka načítá, program normálně hraje
    /// (syntetické zvuky) a v panelu rejstříků se ukazuje "Načítání banky 003 ... NN %".
    /// Po dokončení se presety banky zapíšou do registru rejstříků jako (číslo banky, číslo rejstříku).
    /// </summary>
    public static class Sf2Loader
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<int, int> _percent = new Dictionary<int, int>();
        private static readonly List<string> _errors = new List<string>();
        private static readonly Dictionary<int, Sf2Bank> _banks = new Dictionary<int, Sf2Bank>();   // krok18: načtené banky (pro přehrávač .mid)

        /// <summary>Načtená banka podle čísla banky, nebo null (ještě se načítá / není v settings.json).</summary>
        public static Sf2Bank? GetBank(int bankNumber)
        {
            lock (_lock) { return _banks.TryGetValue(bankNumber, out var bank) ? bank : null; }
        }

        /// <summary>Text o rozběhlém načítání, nebo null když nic neběží.</summary>
        public static string? StatusText
        {
            get
            {
                lock (_lock)
                {
                    if (_percent.Count == 0) return null;
                    return string.Join("; ", _percent.Select(kv =>
                    {
                        string name = BankTable.Find(kv.Key)?.FullName ?? kv.Key.ToString("000");
                        return $"Načítání banky {name} {kv.Value} %";
                    }));
                }
            }
        }

        /// <summary>Poslední chyba načítání (špatná cesta, poškozený soubor), nebo null.</summary>
        public static string? LastError
        {
            get { lock (_lock) { return _errors.Count > 0 ? _errors[_errors.Count - 1] : null; } }
        }

        public static void StartLoad(int bankNumber, string path)
        {
            lock (_lock) { _percent[bankNumber] = 0; }

            Task.Run(() =>
            {
                try
                {
                    // Relativní cesta (např. "sf2\\MuseScore_General.sf2") se bere od složky s exe.
                    if (!Path.IsPathRooted(path))
                        path = Path.Combine(AppContext.BaseDirectory, path);

                    if (!File.Exists(path))
                        throw new FileNotFoundException("Banka SF2 nenalezena: " + path);

                    var bank = Sf2Bank.Load(path, pct => { lock (_lock) { _percent[bankNumber] = pct; } });
                    lock (_lock) { _banks[bankNumber] = bank; }
                    int count = PS150.Core.ToneEngine.ToneEngine.RegisterSf2Bank(bankNumber, bank);
                    System.Diagnostics.Debug.WriteLine($"[Sf2Loader] Banka {bankNumber:000}: {count} rejstříků z {path}");
                }
                catch (Exception ex)
                {
                    lock (_lock) { _errors.Add($"Banka {bankNumber:000}: {ex.Message}"); }
                }
                finally
                {
                    lock (_lock) { _percent.Remove(bankNumber); }
                }
            });
        }
    }
}
