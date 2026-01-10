using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ason.Invocation;
using ModelContextProtocol.Client;

namespace Ason;

internal sealed class InvocationPipeline {
    readonly ConcurrentDictionary<string, OperatorBase> _handleToObject;
    readonly JsonSerializerOptions _jsonOptions;
    readonly SynchronizationContext? _capturedContext;
    readonly ConcurrentDictionary<string, IMcpClient> _mcpClients;

    IInvocationScheduler _scheduler = new PassthroughInvocationScheduler();
    IOperatorInvoker? _operatorInvoker;
    IMcpToolInvoker? _mcpInvoker;
    IOperatorMethodCache? _methodCache;
    IOperatorMethodCache? _invokerCache;
    readonly object _gate = new();

    public InvocationPipeline(ConcurrentDictionary<string, OperatorBase> handleToObject,
        JsonSerializerOptions jsonOptions,
        SynchronizationContext? capturedContext,
        ConcurrentDictionary<string, IMcpClient> mcpClients) {
        _handleToObject = handleToObject;
        _jsonOptions = jsonOptions;
        _capturedContext = capturedContext;
        _mcpClients = mcpClients;
    }

    public JsonSerializerOptions JsonOptions => _jsonOptions;

    public void UpdateMethodCache(IOperatorMethodCache? cache) {
        lock (_gate) {
            _methodCache = cache;
            _operatorInvoker = null;
        }
    }

    public void InvalidateMcpInvoker() {
        lock (_gate) {
            _mcpInvoker = null;
        }
    }

    void EnsureOperatorInvoker() {
        if (_operatorInvoker != null && ReferenceEquals(_invokerCache, _methodCache)) return;
        lock (_gate) {
            if (_operatorInvoker != null && ReferenceEquals(_invokerCache, _methodCache)) return;
            if (_methodCache == null) throw new InvalidOperationException("Method cache not initialized yet");
            _scheduler = _capturedContext != null
                ? new SynchronizationContextInvocationScheduler(_capturedContext)
                : new PassthroughInvocationScheduler();
            _invokerCache = _methodCache;
            _operatorInvoker = new OperatorInvoker(_handleToObject, _scheduler, _jsonOptions, _invokerCache);
        }
    }

    void EnsureMcpInvoker() {
        if (_mcpInvoker != null) return;
        lock (_gate) {
            _mcpInvoker ??= new McpToolInvoker(_mcpClients, _jsonOptions);
        }
    }

    public Task<object?> InvokeOperatorAsync(string target, string method, string? handleId, object?[] args) {
        EnsureOperatorInvoker();
        return _operatorInvoker!.InvokeAsync(target, method, handleId, args);
    }

    public Task<object?> InvokeMcpAsync(string server, string tool, IDictionary<string, object?>? arguments) {
        EnsureMcpInvoker();
        return _mcpInvoker!.InvokeAsync(server, tool, arguments);
    }
}
