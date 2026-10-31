using System.Globalization;

namespace TradingHub.Common;

/// <summary>
/// Cómo se escriben los números y las fechas en la interfaz: en español «1.234,56» y «lun 30/10», en inglés «1,234.56» y
/// «Mon», en chino «1,234.56» y «周一». Sigue al idioma elegido (lo fija la app al arrancar, antes de crear la interfaz).
/// </summary>
public static class UiCulture
{
    public static CultureInfo Current { get; set; } = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>La cultura de un idioma de la interfaz (es, en, zh-CN).</summary>
    public static CultureInfo For(string language) => CultureInfo.GetCultureInfo(language switch
    {
        "en" => "en-US",
        "zh-CN" => "zh-CN",
        _ => "es-AR",
    });
}
