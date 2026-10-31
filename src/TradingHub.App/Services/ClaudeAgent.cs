using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>Un pedido a un agente de Claude Code (la CLI con la sesión del usuario).</summary>
public sealed class ClaudeAgentRequest
{
    public required string Role { get; init; }
    public required string Prompt { get; init; }
    public required string SystemPrompt { get; init; }
    public required string WorkDir { get; init; }

    /// <summary>Herramientas propias de Claude Code habilitadas (Read, WebSearch…); vacío = ninguna.</summary>
    public string[] Tools { get; init; } = Array.Empty<string>();

    /// <summary>Herramientas del laboratorio de Trading Hub (sin el prefijo mcp__tradinghub__).</summary>
    public string[] LabTools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Comandos permitidos con su patrón (por ejemplo «Bash(dotnet build:*)»): si hay, Bash queda habilitada solo para
    /// esos comandos y cualquier otro se rechaza.
    /// </summary>
    public string[] AllowedPatterns { get; init; } = Array.Empty<string>();

    /// <summary>Variables de entorno extra para el agente (por ejemplo, dónde está dotnet).</summary>
    public Dictionary<string, string> Environment { get; init; } = new();

    /// <summary>acceptEdits para quien escribe archivos; dontAsk para el resto (todo lo no habilitado se rechaza).</summary>
    public string PermissionMode { get; init; } = "dontAsk";
    public string Model { get; init; } = "claude-opus-5-5";
    public string? Effort { get; init; }
    public double MaxUsd { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Sesión nueva con este id (queda guardada para poder seguirla si se corta). null = sin guardar.</summary>
    public string? SessionId { get; init; }

    /// <summary>Seguir una sesión que se cortó (el pedido es el mensaje nuevo).</summary>
    public string? ResumeSessionId { get; init; }
}

/// <summary>Lo que devolvió un agente.</summary>
public sealed record ClaudeAgentResult(bool Ok, string Text, JsonElement? Json, double CostUsd, double Seconds, string? Error, bool NeedsLogin, int ToolCalls);

/// <summary>
/// Corre un agente de Claude: Claude Code sin ventana (claude -p) con la sesión de la suscripción del usuario, el modelo
/// elegido, el rol como instrucciones, solo las herramientas que le tocan y el laboratorio de Trading Hub como servidor MCP.
/// Corre en modo restringido (sin la configuración, los hooks ni los MCP del usuario, archivos solo en su carpeta) y
/// sin guardar la sesión. Al final se lee el bloque ```json de la respuesta.
/// </summary>
public sealed class ClaudeAgent
{

    /// <summary>Usó una herramienta (nombre y detalle). Llega desde otro hilo.</summary>
    public event Action<string, string?>? ToolUsed;

    /// <summary>Escribió texto. Llega desde otro hilo.</summary>
    public event Action<string>? TextWritten;

    /// <summary>Argumentos de la CLI para un pedido (las opciones que la CLI instalada no conoce se omiten).</summary>
    public static List<string> BuildArgs(ClaudeAgentRequest req, string? labConfig, Func<string, bool> supports)
    {
        var args = new List<string>
        {
            "-p", req.Prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--model", req.Model,
            "--append-system-prompt", req.SystemPrompt + Localization.Lang.ForClaude,
            "--permission-mode", req.PermissionMode,
            "--strict-mcp-config",
        };

        // sin la configuración, hooks, plugins ni MCP del usuario; archivos solo en la carpeta del agente
        if (supports("--restricted")) args.Add("--restricted");
        // con sesión guardada (para seguirla si se apaga la Mac) o sin guardar nada
        if (req.ResumeSessionId != null && supports("--resume")) args.AddRange(new[] { "--resume", req.ResumeSessionId });
        else if (req.SessionId != null && supports("--session-id")) args.AddRange(new[] { "--session-id", req.SessionId });
        else if (supports("--no-session-persistence")) args.Add("--no-session-persistence");
        if (supports("--disable-slash-commands")) args.Add("--disable-slash-commands");
        if (req.Effort != null && supports("--effort")) args.AddRange(new[] { "--effort", req.Effort });
        if (req.MaxUsd > 0 && supports("--max-budget-usd")) args.AddRange(new[] { "--max-budget-usd", req.MaxUsd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) });

        args.Add("--tools");
        args.Add(string.Join(",", req.Tools));
        // con patrones, Bash no se habilita entera: solo los comandos de los patrones
        var allowed = req.Tools.Where(t => !(t == "Bash" && req.AllowedPatterns.Length > 0)).Concat(req.AllowedPatterns)
            .Concat(req.LabTools.Select(t => "mcp__tradinghub__" + t)).ToList();
        if (req.LabTools.Length > 0 && labConfig != null)
        {
            args.Add("--mcp-config");
            args.Add(labConfig);
        }

        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(string.Join(",", allowed));
        }

        return args;
    }

    /// <summary>¿La CLI instalada conoce esta opción? (se lee una vez su --help)</summary>
    public static Task<Func<string, bool>> SupportsAsync(string cli) => ClaudeCodeBridge.SupportsAsync(cli);

    public async Task<ClaudeAgentResult> RunAsync(ClaudeAgentRequest req, CancellationToken ct)
    {
        var cli = ClaudeCodeBridge.FindCli();
        if (cli == null) return new ClaudeAgentResult(false, "", null, 0, 0, $"No encontré Claude Code en esta {Os.Device} (instalalo desde claude.com/claude-code).", false, 0);

        var supports = await SupportsAsync(cli);
        Directory.CreateDirectory(req.WorkDir);
        var psi = ClaudeCodeBridge.Prepare(cli, req.WorkDir);
        foreach (var (key, value) in req.Environment) psi.Environment[key] = value;
        string? labConfig = req.LabTools.Length > 0 ? ClaudeCodeBridge.WriteLabConfig() : null;
        foreach (var a in BuildArgs(req, labConfig, supports)) psi.ArgumentList.Add(a);
        if (labConfig != null)
        {
            // una optimización o un walk-forward pueden tardar bastante
            psi.Environment["MCP_TOOL_TIMEOUT"] = "3600000";
            psi.Environment["MCP_TIMEOUT"] = "60000";
        }

        var stdinPrompt = ClaudeCodeBridge.MovePromptToStdinIfTooLong(psi);
        var sw = Stopwatch.StartNew();
        var finalText = new StringBuilder();
        var allText = new StringBuilder();
        var stderr = new StringBuilder();
        bool isError = false, gotResult = false;
        double cost = 0;
        int tools = 0;
        string? subtype = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(req.Timeout);
        Process? proc = null;
        try
        {
            proc = Process.Start(psi)!;
            _ = ClaudeCodeBridge.FeedStdinAsync(proc, stdinPrompt);
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null && stderr.Length < 20000) stderr.AppendLine(e.Data);
            };
            proc.BeginErrorReadLine();
            using var reg = timeout.Token.Register(() =>
            {
                try
                {
                    if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ya terminó
                }
            });

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (type == "assistant") tools += HandleAssistant(root, allText);
                    else if (type == "result")
                    {
                        gotResult = true;
                        isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                        subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                        if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) finalText.Append(r.GetString());
                        if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDouble();
                    }
                }
                catch (JsonException)
                {
                    // línea que no es JSON
                }
            }

            await proc.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new ClaudeAgentResult(false, allText.ToString(), null, cost, sw.Elapsed.TotalSeconds, "No se pudo ejecutar Claude Code: " + ex.Message, false, tools);
        }
        finally
        {
            proc?.Dispose();
        }

        string text = finalText.Length > 0 ? finalText.ToString() : allText.ToString();
        if (ct.IsCancellationRequested) return new ClaudeAgentResult(false, text, null, cost, sw.Elapsed.TotalSeconds, "Detenido.", false, tools);
        if (timeout.IsCancellationRequested)
            return new ClaudeAgentResult(false, text, null, cost, sw.Elapsed.TotalSeconds, $"Se pasó del tiempo máximo ({req.Timeout.TotalMinutes:0} min) y se cortó.", false, tools);

        string? error = null;
        if (!gotResult) error = stderr.Length > 0 ? stderr.ToString().Trim() : "Claude Code terminó sin respuesta.";
        else if (isError) error = subtype is { Length: > 0 } s && s != "success" ? Explain(s, text) : text.Trim();
        string all = (error ?? "") + "\n" + text + "\n" + stderr;
        bool needsLogin = !gotResult || isError
            ? all.Contains("/login", StringComparison.OrdinalIgnoreCase) || all.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
              all.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || all.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
            : false;

        // aunque se haya cortado por el tope de gasto, si ya dejó el JSON se usa
        var json = ExtractJson(text) ?? ExtractJson(allText.ToString());
        bool ok = json != null && (error == null || subtype == "error_max_budget_usd" || subtype == "error_max_turns");
        return new ClaudeAgentResult(ok, text, json, cost, sw.Elapsed.TotalSeconds, ok ? null : error ?? "El agente no devolvió el bloque JSON pedido.", needsLogin, tools);
    }

    private static string Explain(string subtype, string text) => subtype switch
    {
        "error_max_budget_usd" => "Llegó al tope de gasto por tanda antes de terminar.",
        "error_max_turns" => "Llegó al máximo de pasos antes de terminar.",
        "error_during_execution" => "Claude Code falló mientras trabajaba" + (text.Length > 0 ? ": " + text.Trim() : "."),
        _ => text.Length > 0 ? text.Trim() : subtype,
    };

    private int HandleAssistant(JsonElement root, StringBuilder allText)
    {
        int tools = 0;
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return 0;
        foreach (var block in content.EnumerateArray())
        {
            string bt = block.TryGetProperty("type", out var b) ? b.GetString() ?? "" : "";
            if (bt == "text" && block.TryGetProperty("text", out var txt))
            {
                string s = txt.GetString() ?? "";
                allText.AppendLine(s);
                TextWritten?.Invoke(s);
            }
            else if (bt == "tool_use")
            {
                tools++;
                string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                ToolUsed?.Invoke(name, Detail(block));
            }
        }

        return tools;
    }

    /// <summary>Detalle corto de una herramienta para el canal (búsqueda, página, archivo o corrida).</summary>
    private static string? Detail(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return null;
        string S(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        foreach (var key in new[] { "query", "url", "file_path", "pattern" })
            if (S(key) is { Length: > 0 } v) return key == "file_path" ? Path.GetFileName(v) : v;
        var parts = new[] { S("file"), S("feed").Length > 0 ? $"{S("feed")}:{S("symbol")}" : S("symbol"), S("period"), S("from").Length > 0 ? $"{S("from")} → {S("to")}" : "", S("title") }
            .Where(x => x.Length > 0);
        var detail = string.Join(" · ", parts);
        return detail.Length > 0 ? detail : null;
    }

    /// <summary>El último bloque ```json de un texto (o el último objeto JSON que se pueda leer).</summary>
    public static JsonElement? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var opts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        int idx = text.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            int start = idx + 7;
            int end = text.IndexOf("```", start, StringComparison.Ordinal);
            string body = end > start ? text[start..end] : text[start..];
            if (TryParse(body, opts) is { } el) return el;
            idx = idx > 0 ? text.LastIndexOf("```json", idx - 1, StringComparison.OrdinalIgnoreCase) : -1;
        }

        // sin bloque: el último objeto que cierre bien
        int close = text.LastIndexOf('}');
        if (close < 0) return null;
        int tries = 0;
        for (int open = text.LastIndexOf('{', close); open >= 0 && tries < 400; open = open > 0 ? text.LastIndexOf('{', open - 1) : -1, tries++)
            if (TryParse(text[open..(close + 1)], opts) is { } el) return el;
        return null;
    }

    private static JsonElement? TryParse(string body, JsonDocumentOptions opts)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.Trim(), opts);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
