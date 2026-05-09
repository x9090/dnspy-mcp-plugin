using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Text;

namespace DnSpyMcp {

[ExportDsLoader]
public sealed class McpPlugin : IDsLoader {
    readonly DbgManager                 _dbgManager;
    readonly DbgCodeBreakpointsService  _bpService;
    readonly DbgDotNetBreakpointFactory _bpFactory;
    readonly IOutputService             _outputService;
    readonly AttachableProcessesService _attachService;
    McpServer? _server;

    static readonly Guid McpOutputGuid = new Guid("4A5D9A5F-8F8D-4C9D-B5B1-460B29618C1D");

    [ImportingConstructor]
    public McpPlugin(
        DbgManager dbgManager,
        DbgCodeBreakpointsService bpService,
        DbgDotNetBreakpointFactory bpFactory,
        IOutputService outputService,
        AttachableProcessesService attachService) {
        _dbgManager    = dbgManager;
        _bpService     = bpService;
        _bpFactory     = bpFactory;
        _outputService = outputService;
        _attachService = attachService;
    }

    public IEnumerable<object?> Load(ISettingsService settingsService, IAppCommandLineArgs args) {
        var pane = _outputService.GetTextPane(McpOutputGuid);
        pane.WriteLine(TextColor.Gray, "Loading MCP Plugin...");

        var bridge = new DebuggerBridge(_dbgManager, _bpService, _bpFactory, _attachService);
        _server = new McpServer(bridge, port: 4445);
        _server.Start(pane);

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        yield break;
    }

    private void OnProcessExit(object? sender, EventArgs e) {
        _server?.Dispose();
    }

    public void OnAppLoaded() { }

    public void Save(ISettingsService settingsService) {
        _server?.Dispose();
        _server = null;
    }
}

}
