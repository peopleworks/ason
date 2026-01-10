using System;
using System.Text.Json;
using AsonRunner.Protocol;

namespace Ason;

internal sealed class ExecResultHandler : IExecResultHandler {
    readonly ExecutionDispatcher _dispatcher;
    readonly JsonSerializerOptions _jsonOptions;

    public ExecResultHandler(ExecutionDispatcher dispatcher, JsonSerializerOptions jsonOptions) {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    public void Handle(ExecResult message) {
        if (message is null) throw new ArgumentNullException(nameof(message));
        _dispatcher.Complete(message, _jsonOptions);
    }
}
