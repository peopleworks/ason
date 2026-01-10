using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Ason.Invocation;
using AsonRunner;
using AsonRunner.Protocol;
using Ason.Transport;

namespace Ason;

public sealed class RunnerClient {
    public readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    readonly InvocationPipeline _pipeline;
    readonly ExecutionDispatcher _executionDispatcher;
    readonly RunnerTransportSettings _transportSettings;
    readonly IRunnerTransportManager _transportManager;
    readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    readonly Dictionary<Type, Func<IRunnerMessage, CancellationToken, Task>> _messageHandlers;
    readonly ILogHandler _logHandler;
    readonly IExecResultHandler _execResultHandler;
    readonly IInvokeRequestHandler _invokeRequestHandler;
    readonly IMcpInvokeRequestHandler _mcpInvokeRequestHandler;

    readonly ConcurrentDictionary<string, IMcpClient> _mcpClients = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Assembly> _assemblies = new() { typeof(ProxySerializer).Assembly };

    public event EventHandler<AsonLogEventArgs>? Log;
    public event EventHandler<RunnerMethodInvokingEventArgs>? MethodInvoking;

    public RunnerClient(ConcurrentDictionary<string, OperatorBase> handleToObject, SynchronizationContext? synchronizationContext) {
        _pipeline = new InvocationPipeline(handleToObject, JsonOptions, synchronizationContext, _mcpClients);
        _executionDispatcher = new ExecutionDispatcher(DebugLog);
        _logHandler = new LogHandler(RaiseLogEvent);
        _execResultHandler = new ExecResultHandler(_executionDispatcher, JsonOptions);

        _transportSettings = new RunnerTransportSettings(RaiseLogEvent) { Mode = ExecutionMode.ExternalProcess };
        _transportManager = new RunnerTransportManager(_transportSettings);
        _transportManager.LineReceived += OnTransportLine;
        _transportManager.TransportClosed += OnTransportClosed;

        _invokeRequestHandler = new InvokeRequestHandler(_pipeline, RaiseMethodInvoking, SendInvokeResultAsync, JsonOptions);
        _mcpInvokeRequestHandler = new McpInvokeRequestHandler(_pipeline, RaiseMethodInvoking, SendInvokeResultAsync, JsonOptions);

        _messageHandlers = new Dictionary<Type, Func<IRunnerMessage, CancellationToken, Task>> {
            { typeof(LogMessage), (msg, _) => { _logHandler.Handle((LogMessage)msg); return Task.CompletedTask; } },
            { typeof(ExecResult), (msg, _) => { _execResultHandler.Handle((ExecResult)msg); return Task.CompletedTask; } },
            { typeof(InvokeRequest), (msg, ct) => _invokeRequestHandler.HandleAsync((InvokeRequest)msg, ct) },
            { typeof(McpInvokeRequest), (msg, ct) => _mcpInvokeRequestHandler.HandleAsync((McpInvokeRequest)msg, ct) }
        };
    }

    public IOperatorMethodCache? MethodCache {
        get => _methodCache;
        set {
            _methodCache = value;
            _pipeline.UpdateMethodCache(value);
        }
    }
    IOperatorMethodCache? _methodCache;

    public ExecutionMode Mode {
        get => _transportSettings.Mode;
        set => _transportSettings.Mode = value;
    }

    public bool UseRemote {
        get => _transportSettings.UseRemote;
        set => _transportSettings.UseRemote = value;
    }

    public string? RemoteUrl {
        get => _transportSettings.RemoteUrl;
        set => _transportSettings.RemoteUrl = value;
    }

    public string DockerImage {
        get => _transportSettings.DockerImage;
        set => _transportSettings.DockerImage = value;
    }

    public string? RunnerExecutablePath {
        get => _transportSettings.RunnerExecutablePath;
        set => _transportSettings.RunnerExecutablePath = value;
    }

    void DebugLog(string msg) => RaiseLogEvent(LogLevel.Debug, msg, source: nameof(RunnerClient));

    public void RaiseLogEvent(LogLevel level, string message, string? exception = null, string? source = null) =>
        Log?.Invoke(this, new AsonLogEventArgs(level, message, exception, source));

    public void RegisterMcpClient(IMcpClient client) {
        if (client is null) throw new ArgumentNullException(nameof(client));
        string name = client.ServerInfo.Name;
        _mcpClients[name] = client;
        _pipeline.InvalidateMcpInvoker();
        RaiseLogEvent(LogLevel.Information, $"MCP client registered: {name}", source: nameof(RunnerClient));
    }

    public Task StartProcessAsync() => EnsureTransportReadyAsync(CancellationToken.None);

    public async Task StopAsync() {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try {
            await _transportManager.StopAsync().ConfigureAwait(false);
            _executionDispatcher.FailAll(new InvalidOperationException("Runner stopped."));
        }
        finally {
            _lifecycleGate.Release();
        }
    }

    public void RegisterAssemblies(params Assembly[] assemblies) {
        if (assemblies == null || assemblies.Length == 0) return;
        foreach (var asm in assemblies) if (asm != null && !_assemblies.Contains(asm)) _assemblies.Add(asm);
    }

    public Task<JsonElement?> ExecuteAsync(string code) => ExecuteAsync(code, CancellationToken.None);

    public async Task<JsonElement?> ExecuteAsync(string code, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_transportManager.RequiresTransport) {
            return await ExecuteInProcessAsync(code, cancellationToken).ConfigureAwait(false);
        }

        await EnsureTransportReadyAsync(cancellationToken).ConfigureAwait(false);
        return await _executionDispatcher.DispatchAsync(
            code,
            request => SendMessageAsync(request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    async Task<JsonElement?> ExecuteInProcessAsync(string code, CancellationToken cancellationToken) {
        var host = new InProcHost(this);
        var result = await ScriptExecutor.EvaluateAsync(code, host, cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        return JsonSerializer.SerializeToElement(result, JsonOptions);
    }

    internal Task<object?> InternalInvokeOperatorAsync(string target, string method, string? handleId, object?[] args) =>
        _pipeline.InvokeOperatorAsync(target, method, handleId, args);

    internal Task<object?> InternalInvokeMcpAsync(string server, string tool, IDictionary<string, object?>? args) =>
        _pipeline.InvokeMcpAsync(server, tool, args);

    public async Task<T?> InvokeOperatorAsync<T>(string target, string method, string? handleId, object?[]? args = null) {
        object? result = await InternalInvokeOperatorAsync(target, method, handleId, args ?? Array.Empty<object?>()).ConfigureAwait(false);
        if (result == null) return default;
        if (typeof(T) == typeof(object)) return (T)result;
        if (result is JsonElement je && typeof(T) == typeof(JsonElement)) return (T)(object)je;
        if (typeof(T) == typeof(string) && result is string s) return (T)(object)s;
        var jsonElem = JsonSerializer.SerializeToElement(result, JsonOptions);
        if (typeof(T) == typeof(JsonElement)) return (T)(object)jsonElem;
        return jsonElem.Deserialize<T>(JsonOptions)!;
    }

    public async Task<T?> InvokeMcpToolAsync<T>(string server, string tool, IDictionary<string, object?>? arguments = null) {
        object? result = await InternalInvokeMcpAsync(server, tool, arguments).ConfigureAwait(false);
        if (result == null) return default;
        if (result is JsonElement je && typeof(T) == typeof(JsonElement)) return (T)(object)je;
        if (typeof(T) == typeof(string) && result is string s) return (T)(object)s;
        var jsonElem = JsonSerializer.SerializeToElement(result, JsonOptions);
        if (typeof(T) == typeof(JsonElement)) return (T)(object)jsonElem;
        return jsonElem.Deserialize<T>(JsonOptions)!;
    }

    void OnTransportLine(string line) {
        if (string.IsNullOrWhiteSpace(line)) return;
        DebugLog($"RX: {Truncate(line, 300)}");
        try {
            var msg = RunnerMessageSerializer.Deserialize(line);
            if (msg is null) return;
            _ = HandleMessageAsync(msg, CancellationToken.None);
        }
        catch (Exception ex) {
            RaiseLogEvent(LogLevel.Error, "Protocol deserialize error", ex.ToString());
        }
    }

    void OnTransportClosed(string reason) {
        RaiseLogEvent(LogLevel.Information, $"Runner transport closed: {reason}");
        _executionDispatcher.FailAll(new InvalidOperationException(reason));
    }

    async Task HandleMessageAsync(IRunnerMessage msg, CancellationToken cancellationToken) {
        if (_messageHandlers.TryGetValue(msg.GetType(), out var handler)) {
            await handler(msg, cancellationToken).ConfigureAwait(false);
        }
        else {
            RaiseLogEvent(LogLevel.Warning, $"Unknown message type: {msg.Type}");
        }
    }

    bool RaiseMethodInvoking(RunnerMethodInvokingEventArgs args) {
        try { MethodInvoking?.Invoke(this, args); }
        catch (Exception ex) { RaiseLogEvent(LogLevel.Error, "MethodInvoking handler error", ex.ToString(), nameof(RunnerClient)); }
        return args.Cancel;
    }

    async Task SendInvokeResultAsync(InvokeResult result, CancellationToken cancellationToken) {
        await SendMessageAsync(result, cancellationToken).ConfigureAwait(false);
    }

    async Task SendMessageAsync(IRunnerMessage message, CancellationToken cancellationToken) {
        if (!_transportManager.RequiresTransport)
            throw new InvalidOperationException("Transport not available in in-process mode.");
        var json = RunnerMessageSerializer.Serialize(message);
        DebugLog($"TX: {json}");
        await _transportManager.SendAsync(json, cancellationToken).ConfigureAwait(false);
    }

    async Task EnsureTransportReadyAsync(CancellationToken cancellationToken) {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await _transportManager.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally {
            _lifecycleGate.Release();
        }
    }

    public void RegisterAssembliesForCompatibility(params Assembly[] assemblies) => RegisterAssemblies(assemblies);

    static string Truncate(string value, int max) => value.Length <= max ? value : value.Substring(0, max) + "...";
}

internal sealed class EmptyMethodCache : IOperatorMethodCache {
    public OperatorMethodEntry GetOrAddClosedGeneric(OperatorMethodEntry openEntry, Type[] typeArguments) => openEntry;
    public bool TryGet(Type declaringType, string name, int argCount, out OperatorMethodEntry entry) { entry = null!; return false; }
}

public sealed class RunnerMethodInvokingEventArgs : CancelEventArgs {
    public string InvocationKind { get; }
    public string? Target { get; }
    public string? Method { get; }
    public string? HandleId { get; }
    public string? Server { get; }
    public string? Tool { get; }
    public object? ArgumentsObject { get; }
    public object?[]? ArgumentsArray { get; }
    public string? UserTask { get; set; }
    public RunnerMethodInvokingEventArgs(string invocationKind, string? target = null, string? method = null, string? handleId = null, string? server = null, string? tool = null, object?[]? arguments = null, IDictionary<string, object?>? argumentsDict = null) {
        InvocationKind = invocationKind;
        Target = target;
        Method = method;
        HandleId = handleId;
        Server = server;
        Tool = tool;
        ArgumentsArray = arguments;
        ArgumentsObject = argumentsDict;
    }
    public IReadOnlyList<object?>? GetArguments() => ArgumentsArray;
    public IDictionary<string, object?>? GetArgumentsDictionary() => ArgumentsObject as IDictionary<string, object?>;
}