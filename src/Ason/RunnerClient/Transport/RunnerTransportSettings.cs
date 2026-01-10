using System;
using Microsoft.Extensions.Logging;
using AsonRunner;

namespace Ason.Transport;

internal sealed class RunnerTransportSettings {
    public ExecutionMode Mode { get; set; } = ExecutionMode.ExternalProcess;
    public bool UseRemote { get; set; }
    public string? RemoteUrl { get; set; }
    public string DockerImage { get; set; } = DockerInfo.DockerImageString;
    public string? RunnerExecutablePath { get; set; }
    public Action<LogLevel, string, string?, string?> LogCallback { get; }

    public RunnerTransportSettings(Action<LogLevel, string, string?, string?> logCallback) {
        LogCallback = logCallback ?? throw new ArgumentNullException(nameof(logCallback));
    }
}
