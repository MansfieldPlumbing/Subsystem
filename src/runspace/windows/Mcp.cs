using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Subsystem.Windows;

// `ss mcp` — a Model-Context-Protocol server over stdio. ADDITIVE: an extra mount beside the existing ss
// surfaces (the REPL, contextualize, build), never a replacement. It speaks newline-delimited JSON-RPC so
// an external coding agent drives ss through TYPED tools instead of scraping console text. Home-rolled
// against the protocol shape (study-align, do not import the SDK) — the same registry the on-device agent
// reads is what a remote agent reaches here ("one JSON, N consumers").
//
// Tools (rung 1):
//   ss_contextualize — the system contract as JSON (components, the dependency DAG, the closed vocabulary).
//   ss_run           — run a command in the hosted runspace (pwsh built-ins + project cmdlets:
//                      Get-Request, Get-CodeContext, and the vom: provider). stdout is the protocol channel, so the
//                      runspace's own output is CAPTURED as text, never written to the console.
internal static class Mcp
{
    private const string DefaultProtocol = "2024-11-05";
    // Every tool result is clipped to this before it crosses the JSON-RPC wire. An unbounded Out-String (e.g.
    // `Get-Request` fat-projected, or `ss_git` deep-JSON) used to return 60-75k chars and blow the CALLER's
    // token budget — a hard error that returns NOTHING useful. A clipped result with a "narrow it" footer is
    // strictly better than that error. ~40k chars ≈ 10k tokens; the curated tools (onboard/contextualize) fit under it.
    private const int MaxToolChars = 40000;

    public static int Run(string[] args)
    {
        // One warm runspace for the session — project cmdlets load once, reused on every tools/call.
        var pwshMount = global::Subsystem.Vom.Vom.CreateOwner("\\Device\\Pwsh");
        using var rs = OpenRunspace(pwshMount);
        try
        {
            // Shorthand: `ss mcp call <tool> [-key val ...]` drives ONE tool directly and prints the result — no
            // hand-written JSON-RPC handshake. e.g. `ss mcp call ss_git -gitRoot S:\subsystem-project\subsystem-main`.
            if (args.Length > 0 && args[0].Equals("call", StringComparison.OrdinalIgnoreCase))
                return CallShorthand(args[1..], rs);
            string? line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                JsonDocument req;
                try { req = JsonDocument.Parse(line); } catch { continue; }   // ignore non-JSON noise
                using (req)
                {
                    var root = req.RootElement;
                    var method = root.TryGetProperty("method", out var m) ? (m.GetString() ?? "") : "";
                    // A request carries an id and gets a reply; a notification (initialized/cancelled) does not.
                    if (!root.TryGetProperty("id", out var idEl)) continue;
                    object? result = null, error = null;
                    try { result = Resolve(method, root, rs); }
                    catch (Exception ex) { error = new { code = -32603, message = ex.Message }; }
                    Write(idEl, result, error);
                }
            }
        }
        finally
        {
            global::Subsystem.Vom.Vom.Terminate(pwshMount);
        }
        return 0;
    }

    private static object Resolve(string method, JsonElement root, Runspace rs) => method switch
    {
        "initialize" => new
        {
            protocolVersion = root.TryGetProperty("params", out var p)
                              && p.TryGetProperty("protocolVersion", out var v)
                              && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? DefaultProtocol) : DefaultProtocol,
            capabilities = new { tools = new { } },
            serverInfo = new { name = "ss", version = ReadVersion() },
        },
        "ping"       => new { },
        "tools/list" => new { tools = ProjectTools() },
        "tools/call" => InvokeTool(root, rs),
        _            => throw new InvalidOperationException("unknown method: " + method),
    };

    private static object[] ProjectTools() => new object[]
    {
        new
        {
            name = "ss_contextualize",
            description = "The Subsystem contract as JSON: components, the dependency DAG, and the closed verb/type vocabulary. Read this first to understand the system from the binary.",
            inputSchema = new { type = "object", properties = new { } },
        },
        new
        {
            name = "ss_run",
            description = "Run a command in the ss runspace (PowerShell 7 built-ins + the project cmdlets: Get-Request/Remedy-*Request, Get-CodeContext, and the vom: provider — `Get-ChildItem vom:\\` walks the live VOM kernel). Returns the formatted output as text, clipped to ~40k chars — narrow the command (Select-Object -First N / Where-Object) if you hit the clip footer.",
            inputSchema = new
            {
                type = "object",
                properties = new { command = new { type = "string", description = "The command line to run." } },
                required = new[] { "command" },
            },
        },
        new
        {
            name = "ss_cmdlets",
            description = "The command surface at a glance: every ss PROJECT cmdlet (Get-Request, Remedy-ChangeRequest, Close-Request, Add-EosLog, Get-CodeContext, Get-GitGraphContext, Invoke-ModelAnalysis, Restore-CodeContext) plus any built-in cmdlet/function matching `filter`. Use this to discover what you can drive through ss_run instead of guessing cmdlet names.",
            inputSchema = new
            {
                type = "object",
                properties = new { filter = new { type = "string", description = "Name wildcard to match, e.g. '*Request' or 'Get-*' (default '*' = the whole surface)." } },
            },
        },
        new
        {
            name = "ss_git",
            description = "The git graph as OBJECTS — branch, HEAD, branches, a recent commit log, and the staged index — parsed natively from .git (no git.exe, no bash). Optional gitRoot (default: the server's working dir).",
            inputSchema = new
            {
                type = "object",
                properties = new { gitRoot = new { type = "string", description = "Repo root (must contain .git). Defaults to the working dir." } },
            },
        },
        new
        {
            name = "ss_map",
            description = "The LIVE architecture map of a source tree: every file, its top-level types, and its internal include edges — re-derived from source on each call (never stale). Optional path (default: the working dir, scanned recursively).",
            inputSchema = new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Source tree to map. Defaults to the working dir." } },
            },
        },
        new
        {
            name = "ss_onboard",
            description = "The one-shot alignment package — telos, invariants, settled decisions, conventions, live state, the FULL file manifest (every file in the tree — a file not listed does not exist), and the contract. Call this FIRST on a cold session to understand the system from the binary.",
            inputSchema = new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Source tree repository root. Optional." } }
            },
        },
    };

    private static object InvokeTool(JsonElement root, Runspace rs)
    {
        var p = root.GetProperty("params");
        var name = p.GetProperty("name").GetString() ?? "";
        var a = p.TryGetProperty("arguments", out var av) ? av : default;
        var raw = RunTool(name, a, rs);
        var text = ApplyClip(raw, MaxToolChars);
        return new { content = new object[] { new { type = "text", text } }, isError = raw.StartsWith("ERROR:", StringComparison.Ordinal) };
    }

    // The one tool dispatcher — shared by the JSON-RPC path (InvokeTool) and the `ss mcp call` shorthand.
    private static string RunTool(string name, JsonElement a, Runspace rs)
    {
        string? GetArg(string key) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return name switch
        {
            "ss_contextualize" => ReadContract(),
            "ss_run"           => Invoke(GetArg("command") ?? "", rs),
            "ss_cmdlets"       => Invoke(CmdletQuery(GetArg("filter")), rs),
            "ss_git"           => Invoke($"Get-GitGraphContext{(GetArg("gitRoot") is string gr && gr.Length > 0 ? $" -GitRoot '{gr.Replace("'", "''")}'" : "")} | ConvertTo-Json -Depth 6", rs),
            "ss_map"           => CaptureConsole(() => LiveMap.Run(GetArg("path") ?? "", ReadContract())),
            "ss_onboard"       => CaptureConsole(() => Onboard.Run(GetArg("path") is string p && p.Length > 0 ? new[] { "--path", p } : Array.Empty<string>())),
            _                  => "unknown tool: " + name,
        };
    }

    // `ss mcp call <tool> [-key val ...]` — invoke one tool from the CLI, no protocol handshake. Prints the text.
    private static int CallShorthand(string[] args, Runspace rs)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: ss mcp call <tool> [-key val ...]\n  tools: ss_onboard · ss_contextualize · ss_cmdlets [-filter <wildcard>] · ss_map [-path <dir>] · ss_git [-gitRoot <dir>] · ss_run -command <cmd>");
            return 2;
        }
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
            if (args[i].StartsWith("-", StringComparison.Ordinal) && i + 1 < args.Length) dict[args[i].TrimStart('-')] = args[++i];
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dict));
        var text = RunTool(args[0], doc.RootElement, rs);
        Console.WriteLine(ApplyClip(text, MaxToolChars));
        return text.StartsWith("ERROR:", StringComparison.Ordinal) ? 1 : 0;
    }

    // The embedded contract — the same resource `ss contextualize --json` prints.
    private static string ReadContract()
    {
        var asm = Assembly.GetExecutingAssembly();
        var n = asm.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("SystemCatalog.json", StringComparison.OrdinalIgnoreCase));
        if (n == null) return "{}";
        using var s = asm.GetManifestResourceStream(n);
        if (s == null) return "{}";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // Run a command in the hosted runspace and CAPTURE the output as text (stdout is the JSON-RPC channel,
    // so the result is gathered via Out-String and returned, never written to the console).
    private static string Invoke(string command, Runspace rs)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(command).AddCommand("Out-String");
        var sb = new StringBuilder();
        try { foreach (var o in ps.Invoke()) sb.Append(o?.ToString()); }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
        foreach (var e in ps.Streams.Error) sb.Append("\n[error] ").Append(e.ToString());
        return sb.ToString();
    }

    // Apply a ceiling to a tool result, with a "narrow it" footer — see MaxToolChars. Never throws; passes short text through.
    private static string ApplyClip(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, max) + $"\n\n[output clipped: {max:N0} of {s.Length:N0} chars shown. Narrow the command (Select-Object -First N / Where-Object / a tighter filter) to see the rest.]";
    }

    // ss_cmdlets — the command surface at a glance: the ss project cmdlets ALWAYS (Source is blank for ours), then
    // everything matching `filter`. So an agent discovers what it can drive without guessing cmdlet names.
    private static string CmdletQuery(string? filter)
    {
        var f = (string.IsNullOrWhiteSpace(filter) ? "*" : filter!).Replace("'", "''");
        return
            "$ours = Get-Command -CommandType Cmdlet,Function | Where-Object { -not $_.Source }; " +
            "$m = @(Get-Command -Name '" + f + "' -CommandType Cmdlet,Function -ErrorAction SilentlyContinue); " +
            "\"== ss project cmdlets ($($ours.Count)) ==\"; " +
            "$ours | Sort-Object Name | Format-Table Name,CommandType -AutoSize | Out-String; " +
            "\"== matching '" + f + "' ($($m.Count)) ==\"; " +
            "$m | Sort-Object Source,Name | Format-Table Name,Source -AutoSize | Out-String";
    }

    private static Runspace OpenRunspace(global::Subsystem.Vom.Owner pwshMount)
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        // The SAME loader the console host uses — it scans both the CodeContext assembly AND this host assembly
        // (where Remedy-*Request / Get-Request / Close-Request / Add-EosLog live), so the MCP cmdlet surface
        // matches the shell. This used to scan only the CodeContext assembly, which is why the request/EOS
        // cmdlets were invisible over MCP — the agent could not drive a request or write an EOS log. That was #39.
        Shim.LoadProjectCmdlets(iss);
        var rs = RunspaceFactory.CreateRunspace(iss);
        rs.Open();
        global::Subsystem.Vom.Vom.Register(pwshMount, "PwshRuntime", rs, onReclaim: rs.Dispose, name: "Runspace");
        return rs;
    }

    private static void Write(JsonElement id, object? result, object? error)
    {
        var payload = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = ParseId(id) };
        if (error != null) payload["error"] = error; else payload["result"] = result;
        Console.Out.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }

    private static object? ParseId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.GetInt64(),
        JsonValueKind.String => id.GetString(),
        _ => id.ToString(),
    };

    // Some verbs (the map, onboard) write to Console — but stdout IS the JSON-RPC channel here. Redirect it to
    // a string for the duration of the call so their output is captured and returned, never leaked to the protocol.
    private static string CaptureConsole(Action act)
    {
        var orig = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { act(); }
        catch (Exception ex) { Console.SetOut(orig); return "ERROR: " + ex.Message; }
        finally { Console.SetOut(orig); }
        return sw.ToString();
    }

    private static string ReadVersion() => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0";
}
