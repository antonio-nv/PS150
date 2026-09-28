using System.Collections.Generic;

namespace PS150.Core
{
    /// <summary>
    /// Čistě textové pomocné funkce pro zobrazení cesty k souboru a zarovnávání
    /// na pevnou šířku - používá je PS150.UI.Windows (FileBrowserWindow) i
    /// PS150.UI.Linux (ať to je terminálová appka, nebo cokoliv jiného), žádná
    /// závislost na konkrétním UI frameworku.
    /// </summary>
    public static class TextLayout
    {
        public static List<string> WrapPath(string text, int width)
        {
            var lines = new List<string>();
            int pos = 0;
            while (pos < text.Length)
            {
                int remaining = text.Length - pos;
                if (remaining <= width)
                {
                    lines.Add(text.Substring(pos));
                    break;
                }

                int breakAt = text.LastIndexOf('\\', pos + width - 1, width);
                if (breakAt <= pos)
                {
                    breakAt = pos + width - 1;
                }
                else
                {
                    breakAt++;
                }

                lines.Add(text.Substring(pos, breakAt - pos));
                pos = breakAt;
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        public static string FitWidth(string text, int width)
        {
            if (width <= 0) return "";
            if (text.Length <= width) return text.PadRight(width);
            return width == 1 ? text.Substring(0, 1) : text.Substring(0, width - 1) + "…";
        }
    }
}
