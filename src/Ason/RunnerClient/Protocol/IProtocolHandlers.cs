using System.Threading;
using System.Threading.Tasks;
using AsonRunner.Protocol;

namespace Ason;

internal interface ILogHandler {
    void Handle(LogMessage message);
}

internal interface IExecResultHandler {
    void Handle(ExecResult message);
}

internal interface IInvokeRequestHandler {
    Task HandleAsync(InvokeRequest message, CancellationToken cancellationToken);
}

internal interface IMcpInvokeRequestHandler {
    Task HandleAsync(McpInvokeRequest message, CancellationToken cancellationToken);
}
