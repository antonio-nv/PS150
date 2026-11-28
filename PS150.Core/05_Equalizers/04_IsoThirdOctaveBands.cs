using System;

namespace PS150.Core.Equalizers
{
    /// <summary>
    /// krok99: standardní zvukařská/hifistická třetinooktávová řada kmitočtů (ISO 266 /
    /// IEC 61260, tzv. "preferred numbers"). PŮVODNĚ (krok79) to byla JEDINÁ tabulka
    /// kmitočtů ekvalizéru, pak nahrazená hudební velkou tercií (krok80, viz
    /// GraphicEqBands), protože s hudbou nemá nic společného. Teď se vrací zpátky
    /// jako DRUHÁ, rovnocenná, ŽIVĚ PŘEPÍNATELNÁ tabulka vedle té hudební (viz
    /// FileBrowserWindow._useIsoBands/ApplyActiveBandTable) - NE jako náhrada.
    ///
    /// Na rozdíl od hudební (temperované) řady tahle vychází z kulatých desítkových
    /// čísel (f = 1000 × 10^(n/10), zaokrouhleno na "pěkná" čísla) - schválně tak,
    /// aby na ni padly důležité technické kmitočty (síťový brum 50Hz/60Hz, referenční
    /// 1000Hz tón...). K měření/diagnostice (brum, rezonance aparatury) je tahle
    /// tabulka lepší, k ladění nástroje/sluchu zase ta hudební - obě jsou praktické,
    /// jen na jiné věci (uživatel: "praktické považuju obě varianty").
    ///
    /// Souvislost se sousedním kmitočtovým poměrem hudební řady (2^(1/3)≈1,2599)
    /// a téhle (10^(1/10)≈1,2589) je nápadně blízká, takže i tahle řada vychází na
    /// přesně 32 pásem v podobném rozsahu (16Hz-20kHz) - vejde se tedy do STEJNÉHO
    /// formuláře (32 řádků/polí, 8+8+8+8) beze změny layoutu.
    /// </summary>
    public static class IsoThirdOctaveBands
    {
        public const int Count = 32;

        public static readonly double[] CenterFreqHz =
        {
            16, 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500,
            630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000,
            10000, 12500, 16000, 20000
        };
    }
}
