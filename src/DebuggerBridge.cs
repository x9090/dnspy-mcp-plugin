using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Metadata;
using dnlib.DotNet;

namespace DnSpyMcp {

// ---------------------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------------------

public sealed class ProcessInfo {
    public int    Pid      { get; set; }
    public string Name     { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class ModuleInfo {
    public string Name     { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class BpHandle {
    public string Id         { get; set; } = "";
    public string TypeName   { get; set; } = "";
    public string MethodName { get; set; } = "";
    public uint   Token      { get; set; }
}

public sealed class BpInfo {
    public string Id         { get; set; } = "";
    public string TypeName   { get; set; } = "";
    public string MethodName { get; set; } = "";
    public bool   IsEnabled  { get; set; }
}

public sealed class BpHit {
    public string Id            { get; set; } = "";
    public string TypeName      { get; set; } = "";
    public string MethodName    { get; set; } = "";
    public uint   FunctionToken { get; set; }
    public uint   Offset        { get; set; }
    public string ModuleFile    { get; set; } = "";
}

public sealed class FrameInfo {
    public uint   FunctionToken { get; set; }
    public uint   Offset        { get; set; }
    public string ModuleFile    { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

public interface IDebuggerBridge : IDisposable {
    List<ProcessInfo> ListAttachedProcesses();
    List<ModuleInfo>  ListModules();
    BpHandle          SetBreakpoint(string assemblyPath, string typeName, string methodName);
    void              RemoveBreakpoint(string bpId);
    List<BpInfo>      ListBreakpoints();
    BpHit?            WaitForBreakpoint(string bpId, int timeoutMs);
    void              Continue();
    List<FrameInfo>   GetStackTrace();
    void              StepInto();
    void              StepOver();
    void              StepOut();
    void              StartDebugging(string exePath);
    bool              AttachToProcess(int pid);
}

// ---------------------------------------------------------------------------
// Implementation
// ---------------------------------------------------------------------------

public sealed class DebuggerBridge : IDebuggerBridge {
    readonly DbgManager                  _dbgManager;
    readonly DbgCodeBreakpointsService   _bpService;
    readonly DbgDotNetBreakpointFactory  _bpFactory;
    readonly AttachableProcessesService? _attachService;

    readonly Dictionary<string, BpEntry> _bps  = new Dictionary<string, BpEntry>();
    readonly object                      _lock = new object();
    int _seq;

    sealed class BpEntry {
        public DbgCodeBreakpoint Bp         { get; }
        public string            TypeName   { get; }
        public string            MethodName { get; }
        public uint              Token      { get; }
        public Queue<BpHit>      Hits       { get; } = new Queue<BpHit>();
        public SemaphoreSlim     Signal     { get; } = new SemaphoreSlim(0);

        public BpEntry(DbgCodeBreakpoint bp, string tn, string mn, uint tok) {
            Bp = bp; TypeName = tn; MethodName = mn; Token = tok;
        }
    }

    public DebuggerBridge(
        DbgManager dbgManager,
        DbgCodeBreakpointsService bpService,
        DbgDotNetBreakpointFactory bpFactory,
        AttachableProcessesService? attachService = null) {
        _dbgManager    = dbgManager;
        _bpService     = bpService;
        _bpFactory     = bpFactory;
        _attachService = attachService;
    }

    public void Dispose() {
        BpEntry[] entries;
        lock (_lock) {
            entries = _bps.Values.ToArray();
            _bps.Clear();
        }
        if (entries.Length > 0) {
            try { _bpService.Remove(entries.Select(e => e.Bp).ToArray()); } catch { }
        }
    }

    // -----------------------------------------------------------------------
    // Process & module enumeration
    // -----------------------------------------------------------------------

    public List<ProcessInfo> ListAttachedProcesses() {
        var result = new List<ProcessInfo>();
        try {
            foreach (var proc in _dbgManager.Processes) {
                result.Add(new ProcessInfo {
                    Pid      = proc.Id,
                    Name     = proc.Name ?? System.IO.Path.GetFileName(proc.Filename) ?? "",
                    FileName = proc.Filename ?? ""
                });
            }
        } catch { }
        return result;
    }

    public List<ModuleInfo> ListModules() {
        var result = new List<ModuleInfo>();
        try {
            foreach (var proc in _dbgManager.Processes) {
                foreach (var rt in proc.Runtimes) {
                    foreach (var mod in rt.Modules) {
                        result.Add(new ModuleInfo {
                            Name     = mod.Name     ?? "",
                            FileName = mod.Filename ?? ""
                        });
                    }
                }
            }
        } catch { }
        return result;
    }

    // -----------------------------------------------------------------------
    // Breakpoints
    // -----------------------------------------------------------------------

    public BpHandle SetBreakpoint(string assemblyPath, string typeName, string methodName) {
        uint token    = ResolveMethodToken(assemblyPath, typeName, methodName);
        var  moduleId = ModuleId.Create(assemblyPath); // Create(string) takes a file path

        // Remove any BP from dnSpy's global service that our plugin doesn't own.
        // dnSpy persists BPs across restarts; a duplicate at the same location causes
        // _bpFactory.Create() to return null (the service deduplicates by location).
        lock (_lock) {
            var owned = new System.Collections.Generic.HashSet<DbgCodeBreakpoint>(
                _bps.Values.Select(e => e.Bp));
            var stale = _bpService.Breakpoints.Where(b => !owned.Contains(b)).ToArray();
            if (stale.Length > 0) try { _bpService.Remove(stale); } catch { }
        }

        var  bp       = _bpFactory.Create(moduleId, token, offset: 0)
                        ?? throw new InvalidOperationException("Breakpoint creation returned null after removing stale BPs");
        var  id       = "bp" + Interlocked.Increment(ref _seq);
        var  entry    = new BpEntry(bp, typeName, methodName, token);

        bp.Hit += (_, e) => {
            DbgStackFrame? frame = null;
            try { frame = e.Thread?.GetFrames(1)?.FirstOrDefault(); } catch { }

            var hit = new BpHit {
                Id            = id,
                TypeName      = typeName,
                MethodName    = methodName,
                FunctionToken = frame?.FunctionToken ?? 0,
                Offset        = frame?.FunctionOffset ?? 0,
                ModuleFile    = frame?.Module?.Filename ?? ""
            };

            lock (_lock) { entry.Hits.Enqueue(hit); }
            entry.Signal.Release();
        };

        lock (_lock) { _bps[id] = entry; }
        return new BpHandle { Id = id, TypeName = typeName, MethodName = methodName, Token = token };
    }

    public void RemoveBreakpoint(string bpId) {
        BpEntry? entry;
        lock (_lock) {
            if (!_bps.TryGetValue(bpId, out entry)) return;
            _bps.Remove(bpId);
        }
        try { _bpService.Remove(new[] { entry!.Bp }); } catch { }
    }

    public List<BpInfo> ListBreakpoints() {
        lock (_lock) {
            return _bps.Select(kv => new BpInfo {
                Id         = kv.Key,
                TypeName   = kv.Value.TypeName,
                MethodName = kv.Value.MethodName,
                IsEnabled  = kv.Value.Bp.IsEnabled
            }).ToList();
        }
    }

    public BpHit? WaitForBreakpoint(string bpId, int timeoutMs) {
        BpEntry? entry;
        lock (_lock) { if (!_bps.TryGetValue(bpId, out entry)) return null; }

        if (!entry.Signal.Wait(timeoutMs)) return null;

        lock (_lock) { return entry.Hits.Count > 0 ? entry.Hits.Dequeue() : null; }
    }

    // -----------------------------------------------------------------------
    // Execution control
    // -----------------------------------------------------------------------

    public void Continue() {
        try {
            foreach (var proc in _dbgManager.Processes)
                proc.Run();
        } catch { }
    }

    public List<FrameInfo> GetStackTrace() {
        try {
            foreach (var proc in _dbgManager.Processes) {
                foreach (var thread in proc.Threads) {
                    var frames = thread.GetFrames(20);
                    if (frames == null || frames.Length == 0) continue;
                    return frames.Select(f => new FrameInfo {
                        FunctionToken = f.FunctionToken,
                        Offset        = f.FunctionOffset,
                        ModuleFile    = f.Module?.Filename ?? ""
                    }).ToList();
                }
            }
        } catch { }
        return new List<FrameInfo>();
    }

    public void StepInto() => RunStep(DbgStepKind.StepInto);
    public void StepOver() => RunStep(DbgStepKind.StepOver);
    public void StepOut()  => RunStep(DbgStepKind.StepOut);

    public void StartDebugging(string exePath) {
        var opts = new DotNetFrameworkStartDebuggingOptions {
            Filename  = exePath,
            BreakKind = PredefinedBreakKinds.EntryPoint
        };
        _dbgManager.Start(opts);
    }

    public bool AttachToProcess(int pid) {
        if (_attachService == null)
            throw new InvalidOperationException("AttachableProcessesService not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = _attachService.GetAttachableProcessesAsync(
            null, new[] { pid }, null, cts.Token);
        task.Wait(cts.Token);
        foreach (var proc in task.Result) {
            if (proc.ProcessId == pid) {
                proc.Attach();
                return true;
            }
        }
        return false;
    }

    void RunStep(DbgStepKind kind) {
        try {
            foreach (var proc in _dbgManager.Processes) {
                foreach (var thread in proc.Threads) {
                    // autoClose=true: stepper disposes itself when step completes
                    thread.CreateStepper().Step(kind, autoClose: true);
                    return;
                }
            }
        } catch { }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static uint ResolveMethodToken(string assemblyPath, string typeName, string methodName) {
        using var module = ModuleDefMD.Load(assemblyPath, new ModuleContext());
        var typeDef = module.Find(typeName, isReflectionName: false)
            ?? throw new InvalidOperationException($"Type '{typeName}' not found");
        var method = typeDef.Methods.FirstOrDefault(m => m.Name == methodName)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found in type '{typeName}'");
        return method.MDToken.Raw;
    }
}

}
