using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;

namespace TradingHub.App.Localization;

/// <summary>
/// Idioma de la interfaz. La app está escrita en español; en inglés o en chino traduce cada texto en el momento de
/// mostrarlo (en los controles y en lo que se dibuja con Skia): los textos fijos con un diccionario y los que llevan datos
/// («Conexión «Rithmic» guardada») con plantillas («Conexión «{0}» guardada»). Las traducciones están en
/// Localization/en.json y zh-CN.json; las arma tools/TradingHub.I18n leyendo el código. Lo que no tiene traducción se
/// ve en español. Se elige en Ajustes y se aplica al volver a abrir.
/// </summary>
public static class Lang
{
    public const string Spanish = "es", English = "en", Chinese = "zh-CN";

    /// <summary>Los idiomas, cada uno con su nombre en su idioma.</summary>
    public static IReadOnlyList<(string Code, string Name)> Available { get; } = new[]
    {
        (Spanish, "Español"),
        (English, "English"),
        (Chinese, "中文（简体）"),
    };

    public static string Current { get; private set; } = Spanish;

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Upper = new(StringComparer.Ordinal); // «RESULTADO» → la de «Resultado»
    private static readonly Dictionary<string, List<Template>> ByStart = new(StringComparer.Ordinal);
    private static readonly List<Template> Loose = new();
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private const int Key = 3; // caracteres del principio de cada plantilla con que se busca

    // Suffixes: las partes pegadas a una palabra en el español («posición{1}»), que pueden ser la terminación del plural
    private sealed record Template(Regex Pattern, string Output, int Holes, int Literal, HashSet<string> Suffixes);

    /// <summary>
    /// El idioma de una instalación nueva: el del sistema si es inglés o chino (y cualquier otro que no sea español, en
    /// inglés). Quien ya usaba la app (tiene ajustes guardados) sigue en español.
    /// </summary>
    public static string Default(bool existingUser)
    {
        if (existingUser) return Spanish;
        var ui = CultureInfo.CurrentUICulture;
        return ui.TwoLetterISOLanguageName switch
        {
            "es" => Spanish,
            "zh" => Chinese,
            _ => English,
        };
    }

    /// <summary>Carga el idioma (sus traducciones) antes de crear la interfaz.</summary>
    public static void Load(string? code)
    {
        Dictionary<string, string>? map = null;
        if (Available.Any(a => a.Code == code) && code != Spanish)
        {
            using var stream = typeof(Lang).Assembly.GetManifestResourceStream($"TradingHub.App.Localization.{code}.json");
            if (stream != null) map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }

        Use(code, map ?? new Dictionary<string, string>());
        // en inglés o en chino, los números y las fechas de toda la app (también los que no pasan por UiCulture: los
        // campos numéricos, la barra de estado) como en ese idioma; en español quedan como los tenga la computadora
        if (Current != Spanish)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = TradingHub.Common.UiCulture.Current;
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = TradingHub.Common.UiCulture.Current;
        }
    }

    /// <summary>Usa estas traducciones para ese idioma (las pruebas cargan las suyas).</summary>
    internal static void Use(string? code, IReadOnlyDictionary<string, string> map)
    {
        lock (Gate)
        {
            Current = Available.Any(a => a.Code == code) ? code! : Spanish;
            Exact.Clear();
            Upper.Clear();
            ByStart.Clear();
            Loose.Clear();
            Cache.Clear();
            if (Current == Spanish) map = new Dictionary<string, string>();
            foreach (var (key, value) in map)
            {
                if (string.IsNullOrEmpty(value)) continue;
                // igual en los dos idiomas no hace falta, salvo una plantilla con terminaciones del plural («{0} error{1}»)
                if (value == key && !Regex.IsMatch(key, @"\p{L}\{\d+\}")) continue;
                if (!HasHoles(key))
                {
                    Exact[Unescape(key)] = Unescape(value);
                    Upper.TryAdd(Unescape(key).ToUpperInvariant(), Unescape(value));
                    continue;
                }

                var t = Compile(key, value);
                if (t == null) continue;
                string start = LiteralStart(key);
                if (start.Length >= Key)
                {
                    var k = start[..Key];
                    if (!ByStart.TryGetValue(k, out var list)) ByStart[k] = list = new List<Template>();
                    list.Add(t);
                }
                else Loose.Add(t);
            }

            // las más específicas primero: más texto fijo gana («Conexión «{0}» guardada» antes que «{0} guardada»)
            foreach (var list in ByStart.Values) list.Sort((a, b) => b.Literal.CompareTo(a.Literal));
            Loose.Sort((a, b) => b.Literal.CompareTo(a.Literal));
        }

        TradingHub.Rendering.UiText.Translate = Current == Spanish ? null : Translate;
        TradingHub.Common.UiCulture.Current = TradingHub.Common.UiCulture.For(Current);
    }

    /// <summary>
    /// Para las instrucciones de Claude (escritas en español): que le conteste a la persona en el idioma de la interfaz.
    /// </summary>
    public static string ForClaude => Current switch
    {
        English => "\n\nIMPORTANT: the user has Trading Hub in English. Always answer in English, even though these instructions are in Spanish.",
        Chinese => "\n\n重要：用户使用的是中文版 Trading Hub。请始终用简体中文回答（即使上面的说明是西班牙语）。",
        _ => "",
    };

    /// <summary>
    /// Vuelve a abrir Trading Hub (para aplicar el idioma): abre otra copia apenas se cierra esta y cierra esta.
    /// </summary>
    public static void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            if (OperatingSystem.IsMacOS() && exe.Contains(".app/Contents/MacOS/", StringComparison.Ordinal))
            {
                var bundle = exe[..(exe.IndexOf(".app/", StringComparison.Ordinal) + 4)];
                System.Diagnostics.Process.Start("/bin/sh", new[] { "-c", $"sleep 1.5; open \"{bundle}\"" });
            }
            else if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            else System.Diagnostics.Process.Start("/bin/sh", new[] { "-c", $"sleep 1.5; \"{exe}\" &" });

            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>El texto en el idioma elegido (el mismo si está en español o no tiene traducción).</summary>
    public static string T(string text) => Translate(text) ?? text;

    /// <summary>La traducción de un texto en español, o null si no tiene (o si el idioma es español).</summary>
    public static string? Translate(string? text)
    {
        if (Current == Spanish || string.IsNullOrEmpty(text) || Available.Any(a => a.Name == text)) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(text, out var cached)) return cached;
            var result = Find(text);
            // los textos con datos cambian siempre (precios, horas): el recuerdo no crece sin fin
            if (Cache.Count > 20_000) Cache.Clear();
            Cache[text] = result;
            return result;
        }
    }

    private static string? Find(string text)
    {
        if (Exact.TryGetValue(text, out var hit)) return hit;
        if (!HasLetter(text)) return null;
        // un título pasado a mayúsculas («RESULTADO»): el texto del mismo título, también en mayúsculas
        if (text == text.ToUpperInvariant() && Upper.TryGetValue(text, out var upper)) return Current == English ? upper.ToUpperInvariant() : upper;
        // en minúscula lo que el diccionario tiene con mayúscula («conectado», «hoy»)
        if (char.IsLower(text[0]) && Part(text) is var part && !ReferenceEquals(part, text)) return part;

        // con espacios o saltos de línea alrededor: se traduce lo de adentro y se dejan igual
        var trimmed = text.Trim();
        if (trimmed.Length != text.Length && trimmed.Length > 0)
        {
            var inner = Find(trimmed);
            if (inner != null)
            {
                int lead = text.IndexOf(trimmed, StringComparison.Ordinal);
                return text[..lead] + inner + text[(lead + trimmed.Length)..];
            }
        }

        if (Match(text) is { } templated) return templated;

        // varias líneas, o partes separadas con «·», que cada una es un texto: se traduce cada una
        return Pieces(text, "\n") ?? Pieces(text, " · ");
    }

    private static string? Pieces(string text, string separator)
    {
        if (!text.Contains(separator, StringComparison.Ordinal)) return null;
        var parts = text.Split(separator);
        bool any = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (Find(parts[i]) is { } part)
            {
                parts[i] = part;
                any = true;
            }
        }

        return any ? string.Join(separator, parts) : null;
    }

    private static string? Match(string text)
    {
        IEnumerable<Template> candidates = text.Length >= Key && ByStart.TryGetValue(text[..Key], out var list) ? list.Concat(Loose) : Loose;
        foreach (var t in candidates)
        {
            var m = t.Pattern.Match(text);
            if (!m.Success) continue;
            // cada parte que cambia, traducida si es un texto conocido («Compra», «sí», «rojo»)
            return Regex.Replace(t.Output, @"(?<!\{)\{(\d+)\}(?!\})", h =>
            {
                var name = "p" + h.Groups[1].Value;
                var g = m.Groups[name];
                if (!g.Success) return h.Value;
                // pegada a una palabra en el español y con «s», «es», «n» o nada: la terminación del plural («día{2}» con
                // «s», «se detiene{3}» con «n»). En inglés es una «s» donde la traducción la pega a una palabra
                // («trade{1}{2}»: la segunda va pegada a la primera y no suma otra; «{1}{0} stopped»: ninguna); en chino no hay
                if (t.Suffixes.Contains(name) && g.Value is "" or "s" or "es" or "n")
                    return Current == English && g.Value is "s" or "es" && h.Index > 0 && char.IsLetter(t.Output[h.Index - 1]) ? "s" : "";

                return Part(g.Value);
            }).Replace("{{", "{").Replace("}}", "}");
        }

        return null;
    }

    // una parte conocida, también con otra mayúscula al principio («rojo» → «Rojo» → «Red» → «red») o toda en mayúsculas
    private static string Part(string value)
    {
        if (value.Length == 0) return value;
        if (Exact.TryGetValue(value, out var hit)) return hit;
        if (value == value.ToUpperInvariant() && HasLetter(value) && Upper.TryGetValue(value, out var caps)) return Current == English ? caps.ToUpperInvariant() : caps;
        if (char.IsLower(value[0]) && Exact.TryGetValue(char.ToUpperInvariant(value[0]) + value[1..], out var upper))
            return upper.Length > 0 && char.IsUpper(upper[0]) && Current == English ? char.ToLowerInvariant(upper[0]) + upper[1..] : upper;
        return value;
    }

    // «Conexión «{0}» guardada» → ^Conexión «(?<p0>.*?)» guardada$ ; una misma parte repetida tiene que coincidir. Con
    // casi nada de texto fijo («{0} de {1}», «{0} · {1}») las partes son solo números: si no, se comería cualquier frase
    private static Template? Compile(string key, string output)
    {
        try
        {
            string hole = Regex.Replace(key, @"\{\d+\}", "").Count(char.IsLetter) < 3 ? @"[-+−]?\d[\d.,]*" : ".*?";
            var sb = new StringBuilder("^");
            var seen = new HashSet<string>();
            var suffixes = new HashSet<string>();
            int literal = 0, holes = 0;
            foreach (Match tok in Regex.Matches(key, @"\{\{|\}\}|\{(\d+)\}|[^{}]+|[{}]"))
            {
                if (tok.Groups[1].Success)
                {
                    var name = "p" + tok.Groups[1].Value;
                    sb.Append(seen.Add(name) ? $"(?<{name}>{hole})" : $@"\k<{name}>");
                    if (tok.Index > 0 && char.IsLetter(key[tok.Index - 1])) suffixes.Add(name);
                    holes++;
                }
                else
                {
                    var lit = tok.Value == "{{" ? "{" : tok.Value == "}}" ? "}" : tok.Value;
                    sb.Append(Regex.Escape(lit));
                    literal += lit.Length;
                }
            }

            // un texto que es todo partes («{0} {1}») no es una plantilla útil
            if (literal < 2) return null;
            sb.Append('$');
            return new Template(new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), output, holes, literal, suffixes);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool HasHoles(string key) => Regex.IsMatch(key, @"(?<!\{)\{\d+\}(?!\})");

    private static string Unescape(string s) => s.Replace("{{", "{").Replace("}}", "}");

    private static string LiteralStart(string key)
    {
        var m = Regex.Match(key, @"^((?:\{\{|\}\}|[^{}])*)");
        return Unescape(m.Groups[1].Value);
    }

    private static bool HasLetter(string s)
    {
        foreach (char c in s)
            if (char.IsLetter(c)) return true;
        return false;
    }

    // ------------------------------------------------------------------ en los controles

    [ThreadStatic] private static bool _applying;

    /// <summary>
    /// Engancha la traducción en los controles: cada texto que se les asigna (en el código o por un enlace) se cambia por
    /// el del idioma elegido. Se llama una vez, antes de crear la interfaz, y solo si el idioma no es español.
    /// </summary>
    public static void Install()
    {
        if (Current == Spanish) return;
        Hook<TextBlock>(TextBlock.TextProperty);
        Hook<Run>(Run.TextProperty);
        Hook<ContentControl>(ContentControl.ContentProperty);
        Hook<HeaderedContentControl>(HeaderedContentControl.HeaderProperty);
        Hook<HeaderedItemsControl>(HeaderedItemsControl.HeaderProperty);
        Hook<HeaderedSelectingItemsControl>(HeaderedSelectingItemsControl.HeaderProperty);
        Hook<TextBox>(TextBox.WatermarkProperty);
        Hook<ComboBox>(ComboBox.PlaceholderTextProperty);
        Hook<Window>(Window.TitleProperty);
        Hook<NativeMenuItem>(NativeMenuItem.HeaderProperty);
        Hook<TrayIcon>(TrayIcon.ToolTipTextProperty);
        Hook<Control>(ToolTip.TipProperty);
        Hook<ToggleSwitch>(ToggleSwitch.OnContentProperty);
        Hook<ToggleSwitch>(ToggleSwitch.OffContentProperty);
    }

    private static void Hook<T>(AvaloniaProperty property) where T : AvaloniaObject
    {
        property.Changed.AddClassHandler<T>((o, e) =>
        {
            if (_applying || e.NewValue is not string s || Translate(s) is not { } t || t == s) return;
            _applying = true;
            try
            {
                o.SetCurrentValue(property, t);
            }
            finally
            {
                _applying = false;
            }
        });
    }
}
