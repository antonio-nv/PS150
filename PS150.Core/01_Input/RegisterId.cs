namespace PS150.Core.Input
{
    /// <summary>
    /// krok15: jednoznačná identita rejstříku = BANKA + ČÍSLO v bance.
    /// Čísla uvnitř banky zůstávají původní (Melzer 1-200, GM program, MuseScore B000...),
    /// žádné přečíslování ani rozsahy. Hodnotový typ - hodí se jako klíč do Dictionary/HashSet.
    /// Banky viz PS150.Core.Banks.
    /// </summary>
    public readonly record struct RegisterId(int Bank, int Number)
    {
        /// <summary>Krátký zápis, např. "001/085" (banka/rejstřík).</summary>
        public override string ToString() => $"{Bank:000}/{Number:000}";
    }
}
