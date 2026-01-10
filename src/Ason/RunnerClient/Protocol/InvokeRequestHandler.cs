using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsonRunner.Protocol;

namespace Ason;

internal sealed class InvokeRequestHandler : IInvokeRequestHandler {
    readonly InvocationPipeline _pipeline;
    readonly Func<RunnerMethodInvokingEventArgs, bool> _preInvoke;
    readonly Func<InvokeResult, CancellationToken, Task> _responseWriter;
    readonly JsonSerializerOptions _jsonOptions;

    public InvokeRequestHandler(
        InvocationPipeline pipeline,
        Func<RunnerMethodInvokingEventArgs, bool> preInvoke,
        Func<InvokeResult, CancellationToken, Task> responseWriter,
        JsonSerializerOptions jsonOptions) {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _preInvoke = preInvoke ?? throw new ArgumentNullException(nameof(preInvoke));
        _responseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    public async Task HandleAsync(InvokeRequest message, CancellationToken cancellationToken) {
        if (message is null) throw new ArgumentNullException(nameof(message));
        object?[] args = message.Args ?? Array.Empty<object?>();
        var invokingArgs = new RunnerMethodInvokingEventArgs("operator", target: message.Target, method: message.Method, handleId: message.HandleId, arguments: args);
        if (_preInvoke(invokingArgs)) {
            await _responseWriter(new InvokeResult(message.Id, null, "Task was cancelled"), cancellationToken).ConfigureAwait(false);
            return;
        }

        try {
            object? result = await _pipeline.InvokeOperatorAsync(message.Target, message.Method, message.HandleId, args).ConfigureAwait(false);
            object? payload = result is null ? null : JsonSerializer.SerializeToElement(result, _jsonOptions);
            await _responseWriter(new InvokeResult(message.Id, payload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) {
            await _responseWriter(new InvokeResult(message.Id, null, ex.ToString()), cancellationToken).ConfigureAwait(false);
        }
    }
}
