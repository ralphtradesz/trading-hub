using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Textos de la interfaz para traducir. La app traduce lo que muestra en el momento de mostrarlo (ver
// src/TradingHub.App/Localization/Lang.cs), así que el catálogo son los textos en español tal como salen en pantalla:
// literales y plantillas («Conexión «{0}» guardada»: lo de las llaves es lo que cambia).
//
//   extract <carpeta src> <catalogo.json>        arma el catálogo leyendo el código
//   pending <catalogo.json> <idioma.json> <carpeta> [tamaño]
//                                                 deja en <carpeta> los textos sin traducir, en tandas de [tamaño]
//   merge <idioma.json> <tandas…>                 suma las tandas traducidas al idioma
//   check <catalogo.json> <idioma.json>           revisa que cada traducción tenga los mismos {n} que su texto
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

switch (args.FirstOrDefault())
{
    case "extract":
        Extract(args[1], args[2]);
        break;
    case "pending":
        Pending(args[1], args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 400);
        break;
    case "merge":
        Merge(args[1], args.Skip(2).ToArray());
        break;
    case "check":
        Environment.ExitCode = Check(args[1], args[2]);
        break;
    default:
        Console.WriteLine("uso: extract <src> <catalogo.json> | pending <catalogo.json> <idioma.json> <carpeta> [tamaño] | merge <idioma.json> <tandas…> | check <catalogo.json> <idioma.json>");
        Environment.ExitCode = 1;
        break;
}

void Extract(string src, string output)
{
    var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
        if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/Atas/") || rel.EndsWith(".g.cs")) continue;
        // el servidor de herramientas para Claude habla con Claude, no con la persona
        if (rel.EndsWith("Lab/LabServer.cs")) continue;
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            string? key = node switch
            {
                InterpolatedStringExpressionSyntax i => Template(i),
                LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) && !InsideConcatOrInterpolation(l) => Escape(l.Token.ValueText),
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && b.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } => Concat(b),
                _ => null,
            };
            if (key == null || !IsText(key)) continue;
            if (!found.TryGetValue(key, out var where)) found[key] = where = new SortedSet<string>(StringComparer.Ordinal);
            if (where.Count < 3) where.Add($"{rel}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
        }
    }

    var catalog = found.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value));
    File.WriteAllText(output, JsonSerializer.Serialize(catalog, json));
    Console.WriteLine($"{catalog.Count} textos ({catalog.Keys.Count(k => Regex.IsMatch(k, @"\{\d+\}"))} con partes que cambian) → {output}");
}

// los literales sueltos de una suma o de una plantilla ya están en la plantilla entera, pero igual se guardan solos: en
// pantalla a veces aparecen tal cual (un botón que dice «Comprar» y un aviso «Comprar 2»)
static bool InsideConcatOrInterpolation(SyntaxNode n) => false;

// en el catálogo, las llaves literales van dobles ({{ }}) para no confundirlas con las partes que cambian
static string Escape(string s) => s.Replace("{", "{{").Replace("}", "}}");

static string Template(InterpolatedStringExpressionSyntax s)
{
    var sb = new StringBuilder();
    int n = 0;
    foreach (var part in s.Contents)
    {
        if (part is InterpolatedStringTextSyntax t) sb.Append(Escape(Unescape(t.TextToken.ValueText)));
        else sb.Append('{').Append(n++).Append('}');
    }

    return sb.ToString();
}

static string Unescape(string text) => text;

// "Precio: " + precio + " (" + x + ")" → «Precio: {0} ({1})»; sin ningún literal no es texto
static string? Concat(BinaryExpressionSyntax b)
{
    var parts = new List<ExpressionSyntax>();
    void Walk(ExpressionSyntax e)
    {
        if (e is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } bb)
        {
            Walk(bb.Left);
            Walk(bb.Right);
        }
        else parts.Add(e);
    }

    Walk(b);
    if (!parts.Any(p => p is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } or InterpolatedStringExpressionSyntax)) return null;
    var sb = new StringBuilder();
    int n = 0;
    foreach (var p in parts)
    {
        if (p is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } l) sb.Append(Escape(l.Token.ValueText));
        else if (p is InterpolatedStringExpressionSyntax i)
        {
            // la plantilla de adentro, con sus partes renumeradas detrás de las que ya hay
            var inner = Template(i);
            sb.Append(Regex.Replace(inner, @"(?<!\{)\{(\d+)\}(?!\})", m => "{" + (int.Parse(m.Groups[1].Value) + n) + "}"));
            n += Regex.Matches(inner, @"(?<!\{)\{\d+\}(?!\})").Count;
        }
        else sb.Append('{').Append(n++).Append('}');
    }

    var s = sb.ToString();
    // una suma de partes sin texto propio («a + b») o de un solo carácter no es texto de la interfaz
    return Regex.Replace(s, @"\{\d+\}", "").Trim().Length < 2 ? null : s;
}

// Texto que alguien lee: tres letras seguidas y que no sea una ruta, una dirección, un formato de número ni un nombre
// técnico (minúsculas pegadas, con guiones bajos o puntos). Lo que sobra no molesta: la app solo traduce lo que muestra.
static bool IsText(string key)
{
    // las instrucciones para Claude y las plantillas de código o de HTML no se muestran: no se traducen
    if (key.Length > 700 || key.Contains("using TradingHub") || key.Contains("using Vela") || key.Contains("<!doctype") || key.Contains("<html") || key.Contains("public class ")) return false;
    var plain = Regex.Replace(key, @"\{\d+\}", " ").Trim();
    if (!Regex.IsMatch(plain, @"\p{L}{3}")) return false;
    if (Regex.IsMatch(plain, @"^(https?|wss?|file)://") || plain.Contains("/usr/") || plain.StartsWith("--")) return false;
    if (!plain.Contains(' '))
    {
        // una sola palabra: solo si arranca con mayúscula o tiene acentos (Guardar, Precio, Sesión), no identificadores
        if (Regex.IsMatch(plain, @"[_./\\:=<>#@\[\]]")) return false;
        if (!char.IsUpper(plain[0]) && !Regex.IsMatch(plain, "[áéíóúñü]")) return false;
        if (Regex.IsMatch(plain, @"^[A-Z0-9]+$")) return false; // NQ, ES, BTCUSDT, OK…
        if (Regex.IsMatch(plain, @"[a-z][A-Z]")) return false;  // nombres de propiedades: AllowRealOrders
    }

    return true;
}

void Pending(string catalogPath, string langPath, string folder, int size)
{
    var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(catalogPath))!;
    var done = File.Exists(langPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(langPath))! : new();
    var missing = catalog.Where(kv => !done.ContainsKey(kv.Key)).OrderBy(kv => kv.Value, StringComparer.Ordinal).ToList();
    Directory.CreateDirectory(folder);
    int n = 0;
    foreach (var chunk in missing.Chunk(size))
    {
        var path = Path.Combine(folder, $"tanda-{++n:00}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(chunk.ToDictionary(kv => kv.Key, kv => kv.Value), json));
    }

    Console.WriteLine($"{missing.Count} sin traducir en {n} tandas → {folder}");
}

void Merge(string langPath, string[] chunks)
{
    var done = File.Exists(langPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(langPath))! : new();
    int added = 0;
    foreach (var c in chunks)
        foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(c))!)
            if (!string.IsNullOrWhiteSpace(v))
            {
                done[k] = v;
                added++;
            }

    var sorted = done.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
    File.WriteAllText(langPath, JsonSerializer.Serialize(sorted, json));
    Console.WriteLine($"{added} traducciones sumadas; {sorted.Count} en {langPath}");
}

int Check(string catalogPath, string langPath)
{
    var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(catalogPath))!;
    var lang = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(langPath))!;
    static string Holes(string s) => string.Join(",", Regex.Matches(s, @"(?<!\{)\{(\d+)\}(?!\})").Select(m => m.Groups[1].Value).OrderBy(x => x));
    int bad = 0;
    foreach (var (k, v) in lang)
    {
        if (Holes(k) != Holes(v))
        {
            Console.WriteLine($"partes distintas: «{k}» → «{v}»");
            bad++;
        }
    }

    int missing = catalog.Keys.Count(k => !lang.ContainsKey(k));
    Console.WriteLine($"{lang.Count} traducidos, {missing} sin traducir, {bad} con partes distintas");
    return bad > 0 ? 1 : 0;
}
