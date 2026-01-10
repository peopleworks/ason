using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsonRunner.Protocol;

namespace Ason;

internal sealed class ExecutionDispatcher {
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new(StringComparer.Ordinal);
    readonly Action<string>? _trace;

    public ExecutionDispatcher(Action<string>? trace = null) => _trace = trace;

    public async Task<JsonElement?> DispatchAsync(string code, Func<ExecRequest, Task> sender, CancellationToken cancellationToken) {
        if (sender is null) throw new ArgumentNullException(nameof(sender));
        cancellationToken.ThrowIfCancellationRequested();

        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs)) {
            throw new InvalidOperationException("Failed to register execution request.");
        }

        _trace?.Invoke($"Dispatching exec request {id}");

        using var registration = cancellationToken.Register(() => {
            if (_pending.TryRemove(id, out var pending)) {
                pending.TrySetException(new OperationCanceledException(cancellationToken));
            }
        });

        try {
            await sender(new ExecRequest(id, code)).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally {
            _pending.TryRemove(id, out _);
        }
    }

    public void Complete(ExecResult execResult, JsonSerializerOptions jsonOptions) {
        if (execResult is null) throw new ArgumentNullException(nameof(execResult));
        if (_pending.TryRemove(execResult.Id, out var tcs)) {
            if (!string.IsNullOrEmpty(execResult.Error)) {
                tcs.TrySetException(new Exception(execResult.Error));
                return;
            }

            if (execResult.Result is JsonElement je) {
                tcs.TrySetResult(je.Clone());
            }
            else if (execResult.Result is null) {
                tcs.TrySetResult(null);
            }
            else {
                tcs.TrySetResult(JsonSerializer.SerializeToElement(execResult.Result, jsonOptions));
            }
            _trace?.Invoke($"Exec result completed {execResult.Id}");
        }
    }

    public void FailAll(Exception exception) {
        foreach (var kvp in _pending.Keys) {
            if (_pending.TryRemove(kvp, out var tcs)) {
                tcs.TrySetException(exception);
            }
        }
    }
}
