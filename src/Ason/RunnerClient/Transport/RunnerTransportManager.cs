using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AsonRunner;

namespace Ason.Transport;

internal sealed class RunnerTransportManager : IRunnerTransportManager {
    readonly RunnerTransportSettings _settings;
    readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    IRunnerTransport? _transport;

    public RunnerTransportManager(RunnerTransportSettings settings) {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public event Action<string>? LineReceived;
    public event Action<string>? TransportClosed;

    public bool RequiresTransport => _settings.UseRemote || _settings.Mode != ExecutionMode.InProcess;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default) {
        if (!RequiresTransport) return;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_transport is { IsStarted: true }) return;
            if (_transport != null) {
                _transport.LineReceived -= HandleLine;
                _transport.Closed -= HandleClosed;
            }
            _transport = CreateTransport();
            _transport.LineReceived += HandleLine;
            _transport.Closed += HandleClosed;
            await _transport.StartAsync().ConfigureAwait(false);
            _settings.LogCallback(LogLevel.Debug, $"Transport started (UseRemote={_settings.UseRemote}, Mode={_settings.Mode})", null, nameof(RunnerTransportManager));
        }
        finally {
            _lifecycleGate.Release();
        }
    }

    public async Task SendAsync(string jsonLine, CancellationToken cancellationToken = default) {
        if (!RequiresTransport) throw new InvalidOperationException("Transport is not required in in-process mode.");
        if (_transport is null || !_transport.IsStarted) throw new InvalidOperationException("Transport not started");
        await _transport.SendAsync(jsonLine).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default) {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_transport != null) {
                try {
                    await _transport.StopAsync().ConfigureAwait(false);
                }
                finally {
                    _transport.LineReceived -= HandleLine;
                    _transport.Closed -= HandleClosed;
                    _transport = null;
                }
            }
        }
        finally {
            _lifecycleGate.Release();
        }
    }

    IRunnerTransport CreateTransport() {
        if (_settings.UseRemote) {
            var baseUrl = _settings.RemoteUrl?.TrimEnd('/') ?? throw new InvalidOperationException("RemoteUrl must be configured for remote runner mode.");
            return new SignalRTransport(baseUrl, _settings.Mode, _settings.DockerImage, _settings.LogCallback);
        }
        return new StdIoProcessTransport(_settings.Mode, _settings.DockerImage, _settings.RunnerExecutablePath);
    }

    void HandleLine(string line) => LineReceived?.Invoke(line);

    void HandleClosed(string reason) {
        TransportClosed?.Invoke(reason);
        _settings.LogCallback(LogLevel.Information, $"Runner transport closed: {reason}", null, nameof(RunnerTransportManager));
    }
}
