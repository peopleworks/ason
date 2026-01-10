using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ason.Transport;

internal interface IRunnerTransportManager {
    event Action<string>? LineReceived;
    event Action<string>? TransportClosed;

    bool RequiresTransport { get; }
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string jsonLine, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
