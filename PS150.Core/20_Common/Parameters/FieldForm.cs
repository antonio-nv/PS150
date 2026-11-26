using System;
using System.Collections.Generic;
using System.Text;

namespace PS150.Core.Parameters
{
    /// <summary>Jak se má pole právě vykreslit (barvy si podle toho zvolí konkrétní UI).</summary>
    public enum FieldStyle
    {
        Normal,
        Focused,
        Editing,
        Error
    }

    /// <summary>Řídicí klávesy, které UI překládá na tento neutrální tvar.</summary>
    public enum FormKey
    {
        Tab,
        ShiftTab,
        Enter,
        Escape,
        Backspace,
        NudgeUp,
        NudgeDown,
        Reset          // ZMĚNA: návrat na výchozí hodnotu (stejné jako '*')
    }

    /// <summary>ZMĚNA: velikost kroku se nevolí trvale, ale při každém pohybu: Shift = hrubý, Ctrl = jemný.</summary>
    public enum StepSize
    {
        Fine,
        Normal,
        Coarse
    }

    /// <summary>Jedno pole na obrazovce: parametr plus místo, kam se vykreslilo (nastavuje UI při každém vykreslení).</summary>
    public sealed class FormField
    {
        public Parameter Parameter { get; }
        public int Row { get; internal set; }
        public int Col { get; internal set; }
        public int Width { get; internal set; }
        public bool Visible { get; internal set; }

        internal FormField(Parameter parameter)
        {
            Parameter = parameter;
        }
    }

    /// <summary>
    /// Ovládání textových polí - stejné pravidla pro okno ve Windows i pro terminál:
    ///
    ///  - klik myší pole zaostří (přepínač rovnou přepne),
    ///  - kolečko nad polem mění hodnotu (rychlé točení zrychluje),
    ///  - Tab / Shift+Tab přeskakují mezi poli, Esc pole opustí,
    ///  - "+" a "-" mění hodnotu o normální krok, Shift+"+"/"-" (a Shift+kolečko) o hrubý,
    ///    Ctrl+"+"/"-" (a Ctrl+kolečko) o jemný; Ctrl+PgUp / Ctrl+PgDn totéž co "+" a "-",
    ///  - "*" (nebo Delete) vrátí pole na výchozí hodnotu, pokud ji parametr má,
    ///  - číslice zahájí zadání čísla, Enter ho potvrdí, Esc zruší,
    ///  - Enter u zaostřeného pole zahájí prázdné zadání (kvůli záporným číslům).
    ///
    /// Třída nic nekreslí: UI v každém snímku zavolá BeginFrame, pak pro každé pole
    /// Place (kam se vykreslilo) a text si vezme z Display. Vstup (klik, kolečko,
    /// znaky, řídicí klávesy) UI předává metodám Click, Wheel, HandleChar, HandleKey.
    /// </summary>
    public sealed class FieldForm
    {
        private readonly List<FormField> _fields = new List<FormField>();
        private readonly StringBuilder _buffer = new StringBuilder();

        public FormField? Focused { get; private set; }
        public bool Editing { get; private set; }
        public string? Error { get; private set; }

        public bool HasFocus => Focused != null;

        public FormField Add(Parameter parameter)
        {
            var field = new FormField(parameter);
            _fields.Add(field);
            return field;
        }

        public void BeginFrame()
        {
            foreach (var f in _fields) f.Visible = false;
        }

        /// <summary>UI hlásí, kam vykreslilo pole (řádek, sloupec, šířka včetně závorek).</summary>
        public void Place(FormField field, int row, int col, int width)
        {
            field.Row = row;
            field.Col = col;
            field.Width = width;
            field.Visible = true;
        }

        public static int FieldWidth(Parameter p) => p.TextWidth + 2;

        public FormField? HitTest(int row, int col)
        {
            foreach (var f in _fields)
            {
                if (f.Visible && f.Row == row && col >= f.Col && col < f.Col + f.Width) return f;
            }
            return null;
        }

        public FieldStyle StyleOf(FormField field)
        {
            if (field != Focused) return FieldStyle.Normal;
            if (Editing) return Error != null ? FieldStyle.Error : FieldStyle.Editing;
            return FieldStyle.Focused;
        }

        /// <summary>Text pole včetně závorek, přesně <see cref="FormField.Width"/> znaků.</summary>
        public string Display(FormField field)
        {
            int inner = Math.Max(1, field.Width - 2);
            string text;

            if (field == Focused && Editing)
            {
                text = _buffer.ToString() + "_";
                if (text.Length > inner) text = text.Substring(text.Length - inner);
                return "[" + text.PadRight(inner) + "]";
            }

            text = field.Parameter.FormatValue();
            return "[" + text.PadLeft(inner) + "]";
        }

        /// <summary>
        /// krok16: zaostří pole a rovnou do něj vloží první znak (číslici). Slouží k tomu, aby
        /// "85 Enter" fungovalo i bez předchozího Tab/kliknutí - číslice rovnou jdou do pole Rejstřík.
        /// </summary>
        public bool FocusAndType(FormField field, char c)
        {
            if (!field.Visible) return false;
            StopEditing();
            Focused = field;
            return HandleChar(c);
        }

        public void ClearFocus()
        {
            Focused = null;
            StopEditing();
        }

        // ------------------------------------------------------------------
        // Myš
        // ------------------------------------------------------------------

        /// <summary>Klik. Vrací true, když zasáhl pole. Klik mimo pole zruší zadávání a zaostření.</summary>
        public bool Click(int row, int col)
        {
            var field = HitTest(row, col);

            if (field == null)
            {
                ClearFocus();
                return false;
            }

            if (field.Parameter.Kind == ParameterKind.Toggle)
            {
                StopEditing();
                Focused = field;
                field.Parameter.Nudge(+1, (int)StepSize.Normal);
                return true;
            }

            if (field == Focused && !Editing)
            {
                BeginEditing("");
                return true;
            }

            StopEditing();
            Focused = field;
            return true;
        }

        /// <summary>Kolečko nad polem (kladné notches = nahoru). Nemění zaostření. Vrací true, když bylo nad polem.</summary>
        public bool Wheel(int row, int col, int notches, StepSize size = StepSize.Normal)
        {
            var field = HitTest(row, col);
            if (field == null || notches == 0) return false;

            if (field == Focused) StopEditing();

            field.Parameter.Nudge(Math.Sign(notches), (int)size, Math.Abs(notches));
            return true;
        }

        // ------------------------------------------------------------------
        // Klávesnice
        // ------------------------------------------------------------------

        /// <summary>Znak z události TextInput (respektuje rozložení klávesnice). Vrací true, když ho pole spotřebovalo.</summary>
        public bool HandleChar(char c, StepSize size = StepSize.Normal)
        {
            if (Focused == null) return false;

            var p = Focused.Parameter;

            if (p.Kind == ParameterKind.Toggle)
            {
                if (c == '+' || c == '-' || c == '_' || c == ' ')
                {
                    p.Nudge(+1, (int)StepSize.Normal);
                    return true;
                }
                if (c == '1') { p.SetValue(1.0); return true; }
                if (c == '0') { p.SetValue(0.0); return true; }
                return false;
            }

            if (Editing)
            {
                if (char.IsDigit(c))
                {
                    if (_buffer.Length < 10) _buffer.Append(c);
                    Error = null;
                    return true;
                }

                if ((c == '.' || c == ',') && _buffer.ToString().IndexOfAny(new[] { '.', ',' }) < 0)
                {
                    if (_buffer.Length < 10) _buffer.Append('.');
                    Error = null;
                    return true;
                }

                if ((c == '-' || c == '+') && _buffer.Length == 0)
                {
                    _buffer.Append(c);
                    Error = null;
                    return true;
                }

                return false;
            }

            if (char.IsDigit(c))
            {
                BeginEditing(c.ToString());
                return true;
            }

            if (c == '+') { Nudge(+1, size); return true; }
            if (c == '-' || c == '_') { Nudge(-1, size); return true; }   // '_' = Shift+minus na české klávesnici
            if (c == '*') { ResetFocused(); return true; }

            return false;
        }

        /// <summary>Řídicí klávesa. Vrací true, když ji formulář vyřídil (jinak ať ji zpracuje zbytek programu).</summary>
        public bool HandleKey(FormKey key, StepSize size = StepSize.Normal)
        {
            switch (key)
            {
                case FormKey.Tab:
                    return MoveFocus(+1);

                case FormKey.ShiftTab:
                    return MoveFocus(-1);

                case FormKey.Enter:
                    if (Focused == null) return false;
                    if (Editing) return Commit();
                    if (Focused.Parameter.Kind == ParameterKind.Toggle)
                        Focused.Parameter.Nudge(+1, (int)StepSize.Normal);
                    else
                        BeginEditing("");
                    return true;

                case FormKey.Escape:
                    if (Editing) { StopEditing(); return true; }
                    if (Focused != null) { Focused = null; return true; }
                    return false;

                case FormKey.Backspace:
                    if (!Editing) return false;
                    if (_buffer.Length > 0) _buffer.Length--;
                    Error = null;
                    return true;

                case FormKey.NudgeUp:
                    if (Focused == null) return false;
                    Nudge(+1, size);
                    return true;

                case FormKey.NudgeDown:
                    if (Focused == null) return false;
                    Nudge(-1, size);
                    return true;

                case FormKey.Reset:
                    if (Focused == null || Editing) return false;
                    ResetFocused();
                    return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Vnitřní
        // ------------------------------------------------------------------

        private void Nudge(int direction, StepSize size)
        {
            if (Focused == null) return;
            StopEditing();
            Focused.Parameter.Nudge(direction, (int)size);
        }

        /// <summary>ZMĚNA: '*' / Delete vrátí zaostřené pole na výchozí hodnotu (440 Hz, 0 st...), pokud ji má.</summary>
        private void ResetFocused()
        {
            if (Focused == null) return;
            StopEditing();
            Focused.Parameter.ResetToDefault();
        }

        private void BeginEditing(string initial)
        {
            Editing = true;
            Error = null;
            _buffer.Clear();
            _buffer.Append(initial);
        }

        private void StopEditing()
        {
            Editing = false;
            Error = null;
            _buffer.Clear();
        }

        /// <summary>Potvrdí zadané číslo. Při chybě zůstane zadávání otevřené s popisem v Error.</summary>
        private bool Commit()
        {
            if (Focused == null) return false;

            if (_buffer.Length == 0)
            {
                StopEditing();
                return true;
            }

            if (Focused.Parameter.TryParse(_buffer.ToString(), out double value, out string error))
            {
                Focused.Parameter.SetValue(value);
                Focused.Parameter.OnTypedCommit?.Invoke(Focused.Parameter.Value);   // krok16
                StopEditing();
            }
            else
            {
                Error = error;
            }

            return true;
        }

        private bool MoveFocus(int direction)
        {
            // Rozepsané platné číslo se při přeskoku potvrdí, neplatné drží pole otevřené.
            if (Editing)
            {
                Commit();
                if (Editing) return true;
            }

            var visible = _fields.FindAll(f => f.Visible);
            if (visible.Count == 0) return false;

            int index = Focused == null ? -1 : visible.IndexOf(Focused);

            if (index < 0)
            {
                Focused = direction > 0 ? visible[0] : visible[visible.Count - 1];
            }
            else
            {
                index = (index + direction + visible.Count) % visible.Count;
                Focused = visible[index];
            }

            return true;
        }
    }
}
