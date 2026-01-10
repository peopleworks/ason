using System;
using Microsoft.Extensions.Logging;
using AsonRunner.Protocol;

namespace Ason;

internal sealed class LogHandler : ILogHandler {
    readonly Action<LogLevel, string, string?, string?> _logger;

    public LogHandler(Action<LogLevel, string, string?, string?> logger) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Handle(LogMessage message) {
        if (message is null) throw new ArgumentNullException(nameof(message));
        var level = LogHelper.ParseLogLevel(message.Level ?? "Information");
        _logger(level, message.Message ?? string.Empty, message.Exception, message.Source);
    }
}
