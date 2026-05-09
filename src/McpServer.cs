using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp {

// MCP JSON-RPC 2.0 server over HTTP (Streamable HTTP transport subset).
// Listens on http://localhost:<port>/
//   GET  /health  → {"status":"ok", "pid":<PID>}
//   POST /        → JSON-RPC 2.0 (initialize / tools/list / tools/call)
public sealed class McpServer : IDisposable {
    readonly IDebuggerBridge _bridge;
    readonly int             _port;
    readonly HttpListener    _listener;
    readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public McpServer(IDebuggerBridge bridge, int port = 4444) {
        _bridge   = bridge;
        _port     = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start(IOutputTextPane? output = null) {
        try {
            _listener.Start();
            Task.Run(AcceptLoop);
            output?.WriteLine(TextColor.Gray, $"MCP server listening on http://localhost:{_port}/");
        } catch (HttpListenerException ex) {
            output?.WriteLine(TextColor.Error, $"[ERROR] Could not start MCP server on port {_port}: {ex.Message} (Error 0x{ex.ErrorCode:X})");
            if (ex.ErrorCode == 32 || ex.ErrorCode == 183) { // Port in use or Prefix already exists
                _ = TryDetectConflict(output);
            }
        } catch (Exception ex) {
            output?.WriteLine(TextColor.Error, $"[ERROR] Unexpected error starting MCP server: {ex.Message}");
        }
    }

    async Task TryDetectConflict(IOutputTextPane? output) {
        try {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            var json = await client.GetStringAsync($"http://localhost:{_port}/health");
            var obj  = JObject.Parse(json);
            var pid  = obj["pid"]?.ToString() ?? "unknown";
            output?.WriteLine(TextColor.Gray, $"[DIAG] Port {_port} is already occupied by another MCP server (PID: {pid}).");
        } catch {
            output?.WriteLine(TextColor.Gray, $"[DIAG] Port {_port} is occupied, but the existing server is not responding. It might be a 'ghost' listener from a crashed process.");
        }
    }

    public void Dispose() {
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        _bridge.Dispose();
    }

    // -----------------------------------------------------------------------
    // HTTP accept loop
    // -----------------------------------------------------------------------

    async Task AcceptLoop() {
        while (!_cts.IsCancellationRequested) {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx) {
        var req  = ctx.Request;
        var resp = ctx.Response;
        resp.Headers["Access-Control-Allow-Origin"] = "*";

        try {
            if (req.HttpMethod == "OPTIONS") { resp.StatusCode = 204; resp.Close(); return; }

            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/health") {
                Write(resp, 200, JsonConvert.SerializeObject(new { status = "ok", pid = Process.GetCurrentProcess().Id }));
                return;
            }

            if (req.HttpMethod == "POST") {
                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    body = sr.ReadToEnd();

                var result = Dispatch(body);
                Write(resp, 200, result);
                return;
            }

            resp.StatusCode = 404;
            resp.Close();
        } catch (Exception ex) {
            try { Write(resp, 500, JsonError(null, -32603, ex.Message)); } catch { }
        }
    }

    static void Write(HttpListenerResponse resp, int status, string json) {
        resp.StatusCode  = status;
        resp.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    // -----------------------------------------------------------------------
    // JSON-RPC 2.0 dispatcher
    // -----------------------------------------------------------------------

    string Dispatch(string body) {
        JObject req;
        try { req = JObject.Parse(body); }
        catch { return JsonError(null, -32700, "Parse error"); }

        var id     = req["id"];
        var method = req["method"]?.ToString();
        var parms  = req["params"] as JObject ?? new JObject();

        if (method == null) return JsonError(id, -32600, "Missing method");

        try {
            object? result = method switch {
                "initialize"  => HandleInitialize(parms),
                "tools/list"  => HandleToolsList(),
                "tools/call"  => HandleToolCall(parms),
                _             => null
            };

            if (result == null) return JsonError(id, -32601, $"Method not found: {method}");
            return JsonSuccess(id, result);
        } catch (Exception ex) {
            return JsonError(id, -32603, ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // MCP protocol handlers
    // -----------------------------------------------------------------------

    static object HandleInitialize(JObject _) => new {
        protocolVersion = "2024-11-05",
        capabilities    = new { tools = new { } },
        serverInfo      = new { name = "dnspy-mcp-plugin", version = "1.0.0" }
    };

    static object HandleToolsList() => new {
        tools = new[] {
            Tool("list_processes",   "List .NET processes attached to the dnSpyEx debugger", new{}),
            Tool("list_modules",     "List loaded modules in all attached processes",         new{}),
            Tool("set_breakpoint",   "Set a .NET code breakpoint by type and method name",
                new { assemblyPath=S(), typeName=S(), methodName=S() }),
            Tool("remove_breakpoint","Remove a breakpoint by id",         new { bpId=S() }),
            Tool("list_breakpoints", "List all active breakpoints",       new{}),
            Tool("wait_for_breakpoint","Wait for a breakpoint to fire",
                new { bpId=S(), timeoutMs=new{ type="integer", description="milliseconds to wait" } }),
            Tool("continue",         "Resume all paused processes",       new{}),
            Tool("get_stack_trace",  "Get call stack of the paused thread", new{}),
            Tool("step_into",        "Step into next instruction",        new{}),
            Tool("step_over",        "Step over next instruction",        new{}),
            Tool("step_out",         "Step out of current frame",         new{}),
            Tool("start_debugging",  "Launch an EXE under the dnSpy debugger, pausing at entry point",
                new { exePath=S() }),
            Tool("attach_to_process","Attach the dnSpy debugger to a running .NET process by PID",
                new { pid=new{ type="integer", description="Process ID to attach to" } })
        }
    };

    static object S() => new { type = "string" };
    static object Tool(string name, string desc, object props) => new {
        name,
        description = desc,
        inputSchema = new { type = "object", properties = props }
    };

    object HandleToolCall(JObject parms) {
        var name = parms["name"]?.ToString()
            ?? throw new ArgumentException("Missing tool name");
        var args = parms["arguments"] as JObject ?? new JObject();

        string S(string k) => args[k]?.ToString() ?? "";
        int    I(string k, int def) => args[k]?.ToObject<int>() ?? def;

        var inner = name switch {
            "list_processes"    => (object)_bridge.ListAttachedProcesses(),
            "list_modules"      => _bridge.ListModules(),
            "set_breakpoint"    => _bridge.SetBreakpoint(S("assemblyPath"), S("typeName"), S("methodName")),
            "remove_breakpoint" => RemoveBpResult(S("bpId")),
            "list_breakpoints"  => _bridge.ListBreakpoints(),
            "wait_for_breakpoint" => (object?)_bridge.WaitForBreakpoint(S("bpId"), I("timeoutMs", 10000))
                                     ?? new { hit = false },
            "continue"          => RunAndOk(() => _bridge.Continue()),
            "get_stack_trace"   => _bridge.GetStackTrace(),
            "step_into"         => RunAndOk(() => _bridge.StepInto()),
            "step_over"         => RunAndOk(() => _bridge.StepOver()),
            "step_out"          => RunAndOk(() => _bridge.StepOut()),
            "start_debugging"   => RunAndOk(() => _bridge.StartDebugging(S("exePath"))),
            "attach_to_process" => (object)new { ok = _bridge.AttachToProcess(I("pid", 0)) },
            _                   => throw new ArgumentException($"Unknown tool: {name}")
        };

        // MCP tools/call result must be { content: [{ type, text }] }
        return new {
            content = new[] {
                new { type = "text", text = JsonConvert.SerializeObject(inner) }
            }
        };
    }

    object RemoveBpResult(string bpId) {
        _bridge.RemoveBreakpoint(bpId);
        return new { ok = true, bpId };
    }

    static object RunAndOk(Action a) { a(); return new { ok = true }; }

    // -----------------------------------------------------------------------
    // JSON-RPC 2.0 envelope helpers
    // -----------------------------------------------------------------------

    static string JsonSuccess(JToken? id, object result) =>
        JsonConvert.SerializeObject(new {
            jsonrpc = "2.0",
            id,
            result
        });

    static string JsonError(JToken? id, int code, string message) =>
        JsonConvert.SerializeObject(new {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        });
}

}
