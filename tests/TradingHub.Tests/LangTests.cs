using TradingHub.App.Localization;
using TradingHub.Rendering;

namespace TradingHub.Tests;

/// <summary>
/// El idioma de la interfaz: los textos fijos por diccionario, los que llevan datos por plantillas (con las partes en
/// otro orden, llaves literales y partes que también se traducen), y en español todo queda igual.
/// </summary>
[Collection("Idioma")] // el idioma es uno solo para toda la app: estas pruebas no corren en paralelo
public sealed class LangTests : IDisposable
{
    private static readonly Dictionary<string, string> English = new()
    {
        ["Guardar"] = "Save",
        ["Cancelar"] = "Cancel",
        ["Mercado"] = "Market",
        ["Conexión {0}"] = "Connection {0}",
        ["Conexión «{0}» guardada"] = "Connection “{0}” saved",
        ["Compra {0}"] = "Buy {0}",
        ["Orden {0} de {1}"] = "{1}'s order {0}",
        ["{0} de {1}"] = "{0} of {1}",
        ["{0} detenida{1}"] = "{1}{0} stopped",
        ["Usá {{x}} y {0}"] = "Use {{x}} and {0}",
        ["Leyenda del gráfico"] = "Chart legend",
        ["{0} cuenta{1} activa{2}"] = "{0} active account{1}{2}",
        ["{0} ejecutada{1} hoy"] = "{0} fill{1} today",
        ["{0} error{1}"] = "{0} error{1}",
        ["Práctica"] = "Practice",
        ["Resultado del día"] = "Day result",
        ["Rojo"] = "Red",
        ["Vinculado al grupo {0}"] = "Linked to the {0} group",
    };

    public void Dispose() => Lang.Use(Lang.Spanish, new Dictionary<string, string>());

    [Fact]
    public void Traduce_los_textos_fijos_y_los_que_llevan_datos()
    {
        Lang.Use(Lang.English, English);
        Assert.Equal("Save", Lang.T("Guardar"));
        // la plantilla más específica gana
        Assert.Equal("Connection “Rithmic” saved", Lang.T("Conexión «Rithmic» guardada"));
        Assert.Equal("Connection Rithmic", Lang.T("Conexión Rithmic"));
        // las partes pueden cambiar de orden, y una parte que es un texto conocido también se traduce
        Assert.Equal("Ana's order 7", Lang.T("Orden 7 de Ana"));
        // con casi nada de texto fijo, solo con números: una frase cualquiera con «de» no se toca
        Assert.Equal("12 of 30", Lang.T("12 de 30"));
        Assert.Equal("Ruptura de rango", Lang.T("Ruptura de rango"));
        Assert.Equal("Buy Market", Lang.T("Compra Mercado"));
        // llaves literales
        Assert.Equal("Use {x} and 5", Lang.T("Usá {x} y 5"));
        // espacios alrededor y varias líneas
        Assert.Equal("  Save ", Lang.T("  Guardar "));
        Assert.Equal("Save\nCancel", Lang.T("Guardar\nCancelar"));
        // lo que no tiene traducción (o no tiene letras) queda igual
        Assert.Equal("Algo nuevo", Lang.T("Algo nuevo"));
        Assert.Null(Lang.Translate("87456.25"));
        // los nombres de los idiomas se ven siempre en su idioma
        Assert.Null(Lang.Translate("Español"));
        // lo que se dibuja con Skia usa lo mismo
        Assert.Equal("Chart legend", UiText.Tr("Leyenda del gráfico"));
        // plurales armados con terminaciones del español: una sola «s» aunque vengan dos seguidas, y el singular
        Assert.Equal("3 active accounts", Lang.T("3 cuentas activas"));
        Assert.Equal("1 active account", Lang.T("1 cuenta activa"));
        Assert.Equal("4 fills today", Lang.T("4 ejecutadas hoy"));
        Assert.Equal("1 fill today", Lang.T("1 ejecutada hoy"));
        // una plantilla que se escribe igual en inglés igual cambia la terminación
        Assert.Equal("32 errors", Lang.T("32 errores"));
        // una terminación que en inglés no va (un adjetivo): la traducción la deja suelta
        Assert.Equal("Ruptura stopped", Lang.T("Ruptura detenidas"));
        // una parte en minúscula, títulos en mayúsculas y partes separadas con «·»
        Assert.Equal("Linked to the red group", Lang.T("Vinculado al grupo rojo"));
        Assert.Equal("DAY RESULT", Lang.T("RESULTADO DEL DÍA"));
        Assert.Equal("RITHMIC · PRACTICE", Lang.T("RITHMIC · PRÁCTICA"));
        // los números y las fechas, como se escriben en ese idioma
        Assert.Equal("1,234.5", 1234.5.ToString("#,##0.##", TradingHub.Common.UiCulture.Current));
    }

    [Fact]
    public void Lo_que_se_dibuja_con_Skia_se_traduce_y_el_chino_usa_una_letra_del_sistema()
    {
        Lang.Use(Lang.Chinese, new Dictionary<string, string> { ["Guardar"] = "保存" });
        using var bitmap = new SkiaSharp.SKBitmap(120, 30);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White };
        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.FromFamilyName("Menlo") ?? SkiaSharp.SKTypeface.Default, 14);
        canvas.Clear(SkiaSharp.SKColors.Black);
        canvas.DrawUiText("Guardar", 2, 20, font, paint);
        // algo se dibujó (con la letra del sistema que tiene los caracteres chinos) y mide lo que mide «保存»
        Assert.Contains(Enumerable.Range(0, 120).SelectMany(x => Enumerable.Range(0, 30).Select(y => bitmap.GetPixel(x, y))), c => c.Red > 128);
        Assert.True(font.MeasureUiText("Guardar") > 0);
    }

    [Fact]
    public void En_español_no_cambia_nada()
    {
        Lang.Use(Lang.Spanish, English);
        Assert.Equal(Lang.Spanish, Lang.Current);
        Assert.Equal("Guardar", Lang.T("Guardar"));
        Assert.Equal("Leyenda del gráfico", UiText.Tr("Leyenda del gráfico"));
        Assert.Equal("", Lang.ForClaude);
        Assert.Equal("1.234,5", 1234.5.ToString("#,##0.##", TradingHub.Common.UiCulture.Current));
    }

    [Fact]
    public void Una_instalación_nueva_toma_el_idioma_del_sistema_y_quien_ya_la_usaba_sigue_en_español()
    {
        Assert.Equal(Lang.Spanish, Lang.Default(existingUser: true));
        var before = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-CN");
            Assert.Equal(Lang.Chinese, Lang.Default(existingUser: false));
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(Lang.English, Lang.Default(existingUser: false));
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("es-AR");
            Assert.Equal(Lang.Spanish, Lang.Default(existingUser: false));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void Los_archivos_de_traducción_tienen_las_mismas_partes_que_el_español()
    {
        foreach (var code in new[] { Lang.English, Lang.Chinese })
        {
            using var stream = typeof(Lang).Assembly.GetManifestResourceStream($"TradingHub.App.Localization.{code}.json");
            Assert.NotNull(stream);
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
            static string Holes(string s) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"(?<!\{)\{(\d+)\}(?!\})")
                .Select(m => m.Groups[1].Value).OrderBy(x => x));
            var bad = map.Where(kv => Holes(kv.Key) != Holes(kv.Value)).Select(kv => kv.Key).Take(5).ToList();
            Assert.True(bad.Count == 0, $"{code}: partes distintas en {string.Join(" | ", bad)}");
        }
    }
}
