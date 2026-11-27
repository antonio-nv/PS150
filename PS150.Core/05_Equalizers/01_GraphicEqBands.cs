using System;
using System.Collections.Generic;

namespace PS150.Core.Equalizers
{
    /// <summary>
    /// krok82: kmitočty 32 pásem grafického ekvalizéru - velká tercie (4 půltóny,
    /// poměr přesně 2^(1/3)), odvozeno od komorního A4 = 440Hz, podle vlastní
    /// tabulky uživatele (C♯0 = 17,3Hz ... F10 = 22350,6Hz). PŮVODNĚ (krok79) se
    /// použila ISO třetinooktávová řada (decimální "pěkná čísla" 20, 25, 31.5...),
    /// ale ta je založená na desítkové "preferred number" řadě a s hudbou nemá nic
    /// společného - proto nahrazeno skutečnou temperovanou řadou.
    ///
    /// Tohle je JEDINÉ místo, odkud se tyhle kmitočty berou - jak pro popisky ve
    /// formuláři (PS150.UI.Windows.FileBrowserWindow.RenderEqBands), tak pro
    /// skutečné filtrování zvuku (GraphicEqualizer32Band), aby se ty dvě věci
    /// nikdy nerozjely (zobrazený kmitočet pásma musí vždy sedět s tím, co se
    /// doopravdy filtruje).
    /// </summary>
    public static class GraphicEqBands
    {
        public const int Count = 32;

        public static readonly double[] CenterFreqHz = BuildFrequencies();

        private static double[] BuildFrequencies()
        {
            const double a4 = 440.0;
            var list = new List<double>();
            for (int semitonesFromA4 = -56; semitonesFromA4 <= 68; semitonesFromA4 += 4)
                list.Add(a4 * Math.Pow(2.0, semitonesFromA4 / 12.0));
            return list.ToArray();
        }
    }
}
