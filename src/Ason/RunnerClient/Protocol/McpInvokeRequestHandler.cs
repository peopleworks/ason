using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsonRunner.Protocol;

namespace Ason;

internal sealed class McpInvokeRequestHandler : IMcpInvokeRequestHandler {
    readonly InvocationPipeline _pipeline;
    readonly Func<RunnerMethodInvokingEventArgs, bool> _preInvoke;
    readonly Func<InvokeResult, CancellationToken, Task> _responseWriter;
    readonly JsonSerializerOptions _jsonOptions;

    public McpInvokeRequestHandler(
        InvocationPipeline pipeline,
        Func<RunnerMethodInvokingEventArgs, bool> preInvoke,
        Func<InvokeResult, CancellationToken, Task> responseWriter,
        JsonSerializerOptions jsonOptions) {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _preInvoke = preInvoke ?? throw new ArgumentNullException(nameof(preInvoke));
        _responseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    public async Task HandleAsync(McpInvokeRequest message, CancellationToken cancellationToken) {
        if (message is null) throw new ArgumentNullException(nameof(message));
        var invokingArgs = new RunnerMethodInvokingEventArgs("mcp", server: message.Server, tool: message.Tool, argumentsDict: message.Arguments);
        if (_preInvoke(invokingArgs)) {
            await _responseWriter(new InvokeResult(message.Id, null, "Task was cancelled"), cancellationToken).ConfigureAwait(false);
            return;
        }

        try {
            object? result = await _pipeline.InvokeMcpAsync(message.Server, message.Tool, message.Arguments).ConfigureAwait(false);
            object? payload = result is null ? null : JsonSerializer.SerializeToElement(result, _jsonOptions);
            await _responseWriter(new InvokeResult(message.Id, payload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) {
            await _responseWriter(new InvokeResult(message.Id, null, ex.ToString()), cancellationToken).ConfigureAwait(false);
        }
    }
}
