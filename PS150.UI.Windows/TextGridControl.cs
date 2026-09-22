using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PS150.UI.Windows
{
    /// <summary>
    /// Vlastní textová mřížka (znak + barva popředí/pozadí na buňku),
    /// vykreslovaná monospace fontem přímo přes DrawingContext - náhrada za
    /// Console.SetCursorPosition/Write/ForegroundColor z dřívějšího VgaEngine.
    /// ÚMYSLNĚ napodobuje jen tuhle malou podmnožinu Console API (Write,
    /// WriteLine, ForegroundColor, BackgroundColor, ResetColor, Clear), aby
    /// se veškerá obsahová logika (RenderDashboard/RenderMetersOnly/...)
    /// dala převzít skoro beze změny - liší se jen to, KAM se kreslí, ne CO.
    ///
    /// Na rozdíl od Windows konzole tu žádný "fantomový" zalomený řádek
    /// nehrozí - mřížka má pevný počet sloupců/řádků jako 2D pole buněk,
    /// žádné skutečné zalamování textu se nekoná.
    /// </summary>
    public class TextGridControl : FrameworkElement
    {
        private struct Cell
        {
            public char Ch;
            public ConsoleColor Fg;
            public ConsoleColor Bg;
        }

        // Buňka bez explicitně nastavené barvy pozadí (Console.BackgroundColor
        // se nikdy nenastavuje) se kreslí jako "průhledná" - používáme null
        // jako "žádné pozadí", proto Bg je nullable přes zvláštní hodnotu.
        private const ConsoleColor NoBackground = (ConsoleColor)(-1);

        private Cell[,] _cells;

        public int Cols { get; }
        public int Rows { get; private set; }

        public int CursorX { get; set; }
        public int CursorY { get; set; }

        public ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
        public ConsoleColor BackgroundColor { get; set; } = NoBackground;

        public double CellWidth { get; }
        public double CellHeight { get; }

        private readonly Typeface _typeface;
        private const double FontSize = 16.0;

        public TextGridControl(int cols, int rows)
        {
            Cols = cols;
            Rows = rows;
            _cells = new Cell[rows, cols];

            // Seznam záložních fontů oddělený čárkou - WPF postupně zkusí
            // každý, dokud nenajde nainstalovaný. PxPlus IBM VGA 8x16 je
            // sice instalovaný automaticky (viz instalátor), ale kdyby
            // náhodou nebyl, spadne to na běžný systémový monospace font -
            // mřížka bude vypadat jinak, ale nic nespadne ani se neutrhne.
            var family = new FontFamily("PxPlus IBM VGA 8x16, Cascadia Mono, Consolas, Courier New");
            _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // Šířka/výška buňky se změří podle skutečně použitého fontu (ať
            // sedí přesně, ať se nakonec použil kterýkoliv font ze seznamu
            // výše) - měříme širší text, ať i případný proporcionální fallback
            // dá rozumný odhad na znak.
            var probe = new FormattedText(
                "MMMMMMMMMM",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                FontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            CellWidth = Math.Ceiling(probe.Width / 10.0);
            CellHeight = Math.Ceiling(probe.Height);

            SnapsToDevicePixels = true;
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Aliased);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Clear();
        }

        /// <summary>Přemění potřebný počet řádků - obsah se při tom vždy zahodí (stejně jako EnsureWindowHeight+Console.Clear v původním VgaEngine).</summary>
        public void SetRows(int rows)
        {
            rows = Math.Max(1, rows);
            if (rows == Rows) return;
            Rows = rows;
            _cells = new Cell[rows, Cols];
            Clear();
        }

        public void Clear()
        {
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                    _cells[y, x] = new Cell { Ch = ' ', Fg = ConsoleColor.Gray, Bg = NoBackground };

            CursorX = 0;
            CursorY = 0;
            InvalidateVisual();
        }

        public void ResetColor()
        {
            ForegroundColor = ConsoleColor.Gray;
            BackgroundColor = NoBackground;
        }

        public void Write(string text)
        {
            if (CursorY < 0 || CursorY >= Rows) return;

            foreach (char c in text)
            {
                if (CursorX >= 0 && CursorX < Cols)
                {
                    _cells[CursorY, CursorX] = new Cell { Ch = c, Fg = ForegroundColor, Bg = BackgroundColor };
                }
                CursorX++;
            }
        }

        public void WriteLine(string text = "")
        {
            Write(text);
            CursorX = 0;
            CursorY++;
        }

        public void SetCursorPosition(int x, int y)
        {
            CursorX = x;
            CursorY = y;
        }

        public void Redraw() => InvalidateVisual();

        protected override Size ArrangeOverride(Size finalSize) => new Size(Cols * CellWidth, Rows * CellHeight);

        protected override Size MeasureOverride(Size availableSize) => new Size(Cols * CellWidth, Rows * CellHeight);

        protected override void OnRender(DrawingContext dc)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int y = 0; y < Rows; y++)
            {
                int x = 0;
                while (x < Cols)
                {
                    // Sloučit souvislý úsek stejné barvy (popředí i pozadí) do
                    // jednoho FormattedText - kreslit po jednom znaku by pro
                    // celou mřížku (desítky x desítky buněk, 30x/s) zbytečně
                    // zatěžovalo GPU/CPU.
                    ConsoleColor fg = _cells[y, x].Fg;
                    ConsoleColor bg = _cells[y, x].Bg;
                    int runStart = x;
                    var sb = new System.Text.StringBuilder();
                    while (x < Cols && _cells[y, x].Fg == fg && _cells[y, x].Bg == bg)
                    {
                        sb.Append(_cells[y, x].Ch);
                        x++;
                    }

                    double px = runStart * CellWidth;
                    double py = y * CellHeight;

                    if (bg != NoBackground)
                    {
                        dc.DrawRectangle(ConsoleColorPalette.ToBrush(bg), null,
                            new Rect(px, py, (x - runStart) * CellWidth, CellHeight));
                    }

                    var ft = new FormattedText(
                        sb.ToString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        FontSize,
                        ConsoleColorPalette.ToBrush(fg),
                        dpi);

                    dc.DrawText(ft, new Point(px, py));
                }
            }
        }
    }

    /// <summary>Klasická 16barevná paleta Windows konzole - ať mřížka vypadá barevně přesně jako předtím.</summary>
    public static class ConsoleColorPalette
    {
        private static readonly Dictionary<ConsoleColor, Color> Map = new()
        {
            [ConsoleColor.Black] = Color.FromRgb(0x00, 0x00, 0x00),
            [ConsoleColor.DarkBlue] = Color.FromRgb(0x00, 0x00, 0x80),
            [ConsoleColor.DarkGreen] = Color.FromRgb(0x00, 0x80, 0x00),
            [ConsoleColor.DarkCyan] = Color.FromRgb(0x00, 0x80, 0x80),
            [ConsoleColor.DarkRed] = Color.FromRgb(0x80, 0x00, 0x00),
            [ConsoleColor.DarkMagenta] = Color.FromRgb(0x80, 0x00, 0x80),
            [ConsoleColor.DarkYellow] = Color.FromRgb(0x80, 0x80, 0x00),
            [ConsoleColor.Gray] = Color.FromRgb(0xC0, 0xC0, 0xC0),
            [ConsoleColor.DarkGray] = Color.FromRgb(0x80, 0x80, 0x80),
            [ConsoleColor.Blue] = Color.FromRgb(0x00, 0x00, 0xFF),
            [ConsoleColor.Green] = Color.FromRgb(0x00, 0xFF, 0x00),
            [ConsoleColor.Cyan] = Color.FromRgb(0x00, 0xFF, 0xFF),
            [ConsoleColor.Red] = Color.FromRgb(0xFF, 0x00, 0x00),
            [ConsoleColor.Magenta] = Color.FromRgb(0xFF, 0x00, 0xFF),
            [ConsoleColor.Yellow] = Color.FromRgb(0xFF, 0xFF, 0x00),
            [ConsoleColor.White] = Color.FromRgb(0xFF, 0xFF, 0xFF),
        };

        private static readonly Dictionary<ConsoleColor, SolidColorBrush> BrushCache = new();

        public static Brush ToBrush(ConsoleColor color)
        {
            if (!Map.TryGetValue(color, out var rgb))
            {
                // "NoBackground" nebo cokoliv neznámého - nekreslit vůbec
                // (volající si to ověří přes bg != NoBackground dřív, tohle
                // je jen pojistka pro popředí, kde by k tomu nemělo dojít).
                rgb = Colors.Gray;
            }

            if (!BrushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(rgb);
                brush.Freeze();
                BrushCache[color] = brush;
            }
            return brush;
        }
    }
}
