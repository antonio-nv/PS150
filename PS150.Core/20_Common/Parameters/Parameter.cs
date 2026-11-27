using System;
using System.Globalization;

namespace PS150.Core.Parameters
{
    /// <summary>Druh parametru: číslo (kolečko, +/-, zadání číslem) nebo přepínač zap/vyp.</summary>
    public enum ParameterKind
    {
        Number,
        Toggle
    }

    /// <summary>
    /// Jeden ovladatelný parametr (hlasitost, ladění, AJI, později zesílení kanálu,
    /// kmitočet ekvalizéru...). Parametr sám nevlastní hodnotu, jen ví, jak ji
    /// přečíst a zapsat (delegáty Get/Set) a jaká má pravidla: rozsah, kroky,
    /// počet desetinných míst, jednotku. Textové pole (FieldForm) ho podle toho
    /// vykreslí a ovládá - stejné pro okno ve Windows i pro terminál přes SSH.
    ///
    /// Žádná závislost na WPF ani na konkrétním UI, patří do Core.
    /// </summary>
    public sealed class Parameter
    {
        public string Name { get; }
        public string Unit { get; }
        public ParameterKind Kind { get; }

        public double Min { get; }
        public double Max { get; }

        /// <summary>Počet desetinných míst při zobrazení (a zaokrouhlení výsledku).</summary>
        public int Decimals { get; }

        /// <summary>Kroky: [0] jemný (Ctrl), [1] normální, [2] hrubý (Shift).</summary>
        public double[] Steps { get; }

        /// <summary>Zobrazit i znaménko plus (u transpozice: +0.50).</summary>
        public bool ShowSign { get; }

        /// <summary>ZMĚNA: výchozí hodnota pro '*' / Delete (440 Hz, 0 st...). Null = parametr ji nemá.</summary>
        public double? DefaultValue { get; }

        public string OnText { get; }
        public string OffText { get; }

        /// <summary>krok16: zobrazit celé číslo s vedoucími nulami (banka 001, rejstřík 085). 0 = bez nul.</summary>
        public int ZeroPad { get; private set; }

        /// <summary>
        /// krok16: vlastní krok po existujících hodnotách (např. další existující rejstřík v bance).
        /// Parametry: aktuální hodnota, směr (+1/-1); vrací novou hodnotu. Null = obyčejný krok.
        /// </summary>
        public Func<double, int, double>? Stepper { get; set; }

        /// <summary>krok16: zavolá se po potvrzení ručně zadaného čísla klávesou Enter (ne při +/- ani kolečku).</summary>
        public Action<double>? OnTypedCommit { get; set; }

        /// <summary>
        /// krok70: zavolá se po KAŽDÉM kroku hodnoty (klávesy +/-, kolečko, Ctrl+PgUp/PgDn) -
        /// na rozdíl od OnTypedCommit (jen Enter po ručně vypsaném čísle) sem patří akce,
        /// které se mají spustit hned při krokování, bez čekání na potvrzení. Používá pole
        /// Rej (FileBrowserWindow) pro rychlé "proklikání" celé banky rejstříků.
        /// </summary>
        public Action<double>? OnStepCommit { get; set; }

        private readonly Func<double> _get;
        private readonly Action<double> _set;

        private Parameter(
            string name, string unit, ParameterKind kind, double min, double max, int decimals,
            double[] steps, bool showSign, string onText, string offText,
            Func<double> get, Action<double> set, double? defaultValue = null)
        {
            DefaultValue = defaultValue;
            Name = name;
            Unit = unit;
            Kind = kind;
            Min = min;
            Max = max;
            Decimals = decimals;
            Steps = steps;
            ShowSign = showSign;
            OnText = onText;
            OffText = offText;
            _get = get;
            _set = set;
        }

        /// <param name="steps">Tři kroky: jemný, normální, hrubý.</param>
        public static Parameter Number(
            string name, string unit, double min, double max, int decimals, double[] steps,
            Func<double> get, Action<double> set, bool showSign = false, double? defaultValue = null, int zeroPad = 0)
        {
            if (steps == null || steps.Length != 3)
                throw new ArgumentException("Parametr potřebuje právě tři kroky (jemný, normální, hrubý).", nameof(steps));

            var parameter = new Parameter(name, unit, ParameterKind.Number, min, max, decimals, steps, showSign, "", "", get, set, defaultValue);
            parameter.ZeroPad = zeroPad;   // krok16
            return parameter;
        }

        public static Parameter Toggle(
            string name, Func<bool> get, Action<bool> set, string onText = "zap", string offText = "vyp")
        {
            return new Parameter(
                name, "", ParameterKind.Toggle, 0, 1, 0, new[] { 1.0, 1.0, 1.0 }, false, onText, offText,
                () => get() ? 1.0 : 0.0,
                v => set(v >= 0.5));
        }

        public double Value => _get();

        public bool IsOn => Value >= 0.5;

        /// <summary>Hodnota jako text, vždy s tečkou (ne s čárkou), ať nezáleží na jazyku systému.</summary>
        public string FormatValue(double value)
        {
            if (Kind == ParameterKind.Toggle) return value >= 0.5 ? OnText : OffText;

            // krok16: celé číslo s vedoucími nulami
            if (ZeroPad > 0 && Decimals == 0 && !ShowSign)
                return ((long)Math.Round(value)).ToString("D" + ZeroPad, CultureInfo.InvariantCulture);

            string format = ShowSign
                ? "+0." + new string('0', Decimals) + ";-0." + new string('0', Decimals) + ";+0." + new string('0', Decimals)
                : "0." + new string('0', Decimals);

            if (Decimals == 0)
            {
                format = ShowSign ? "+0;-0;+0" : "0";
            }

            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        public string FormatValue() => FormatValue(Value);

        /// <summary>Šířka textu uvnitř závorek: tolik, aby se vešla nejdelší možná hodnota.</summary>
        public int TextWidth
        {
            get
            {
                if (Kind == ParameterKind.Toggle) return Math.Max(OnText.Length, OffText.Length);
                return Math.Max(FormatValue(Min).Length, FormatValue(Max).Length);
            }
        }

        /// <summary>Přečte text zadaný uživatelem. Desetinná tečka i čárka jsou povolené.</summary>
        public bool TryParse(string? text, out double value, out string error)
        {
            value = 0;
            error = "";

            text = (text ?? "").Trim().Replace(',', '.');
            if (text.Length == 0)
            {
                error = "Prázdné zadání";
                return false;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "Neplatné číslo";
                return false;
            }

            if (value < Min || value > Max)
            {
                error = $"{Name}: {FormatValue(Min)} až {FormatValue(Max)} {Unit}".TrimEnd();
                return false;
            }

            return true;
        }

        /// <summary>ZMĚNA: vrátí parametr na výchozí hodnotu (když ji má).</summary>
        public void ResetToDefault()
        {
            if (DefaultValue.HasValue) SetValue(DefaultValue.Value);
        }

        /// <summary>Nastaví hodnotu (oříznutou na rozsah a zaokrouhlenou na zobrazená desetinná místa).</summary>
        public void SetValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;

            value = Math.Clamp(value, Min, Max);
            value = Math.Round(value, Math.Max(Decimals, 0));
            _set(value);
        }

        /// <summary>
        /// Posune hodnotu o <paramref name="multiplier"/> kroků vybrané velikosti
        /// (<paramref name="stepIndex"/>: 0 jemný, 1 normální, 2 hrubý) nahoru (+1)
        /// nebo dolů (-1). U přepínače přepne hodnotu.
        /// </summary>
        public void Nudge(int direction, int stepIndex, int multiplier = 1)
        {
            if (direction == 0) return;

            if (Kind == ParameterKind.Toggle)
            {
                SetValue(IsOn ? 0.0 : 1.0);
                return;
            }

            // krok16: krok po existujících hodnotách (hrubý krok = 10 položek najednou)
            if (Stepper != null)
            {
                double v = Value;
                int count = (stepIndex == 2 ? 10 : 1) * Math.Max(1, multiplier);
                for (int i = 0; i < count; i++) v = Stepper(v, Math.Sign(direction));
                SetValue(v);
                OnStepCommit?.Invoke(Value);   // krok70
                return;
            }

            double step = Steps[Math.Clamp(stepIndex, 0, Steps.Length - 1)];
            double next = Value + Math.Sign(direction) * step * Math.Max(1, multiplier);
            SetValue(next);
            OnStepCommit?.Invoke(Value);   // krok70
        }
    }
}
