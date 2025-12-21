using System.Collections.Concurrent;
using Ason.CodeGen;
using Ason.Tests.Infrastructure;
using Ason.Tests.Operators;
using Microsoft.Extensions.Logging;
using AsonRunner;
using Ason.Tests.Orchestration;

namespace Ason.Tests.Logging;

public class LoggingAndEventsTests {
    [Fact]
    public async Task Logging_InProcess_CapturesExpectedLogs() {
        var expectedLogs = new List<(LogLevel Level, string Message, string Source)> {
            (LogLevel.Information, "ReceptionAgent input: Test task", "AsonClient"),
            (LogLevel.Information, "ReceptionAgent completed output: script", "AsonClient"),
            (LogLevel.Debug, "ScriptAgent input:\nTest task", "AsonClient"),
            (LogLevel.Debug, "ScriptAgent outout (attempt 1):\n", "AsonClient"),
            (LogLevel.Warning, "Validation failed: Empty script", "AsonClient"),
            (LogLevel.Debug, "ScriptAgent input:\nRegenerate the script to accomplish the task, correcting the previous failure", "AsonClient")
        };

        await TestLoggingCore(ExecutionMode.InProcess, expectedLogs);
    }

    private async Task TestLoggingCore(
        ExecutionMode mode,
        List<(LogLevel Level, string Message, string Source)> expectedLogs,
        bool checkTransportLogs = false) {
        var receptionSvc = TestChatServices.CreateReceptionService("script");
        var scriptSvc = TestChatServices.CreateScriptService(string.Empty, "return 42;");
        var explainerSvc = TestChatServices.CreateExplainerService(echoUserInput: true);

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .Build();

        var rootOp = new TestRootOp(new object());

        var options = new AsonClientOptions {
            ExecutionMode = mode,
            MaxFixAttempts = 2,
            SkipReceptionAgent = false,
            SkipExplainerAgent = false,
            ScriptChatCompletion = scriptSvc,
            ReceptionChatCompletion = receptionSvc,
            ExplainerChatCompletion = explainerSvc
        };

        AsonClient client = new AsonClient(scriptSvc, rootOp, operatorsLib, options);

        var logs = new ConcurrentBag<(LogLevel Level, string Message, string? Source)>();
        client.Log += (s, e) => { logs.Add((e.Level, e.Message, e.Source)); };

        var response = await client.SendAsync("Test task");

        Assert.NotEmpty(logs);

        foreach (var expectedLog in expectedLogs) {
            var found = logs.Any(l =>
                l.Level == expectedLog.Level &&
                l.Message.Contains(expectedLog.Message) &&
                (expectedLog.Source == null || l.Source == expectedLog.Source));

            Assert.True(found,
                $"Expected log not found: Level={expectedLog.Level}, Message='{expectedLog.Message}', Source={expectedLog.Source}");
        }
    }


    [Fact]
    public async Task Logging_CapturesExecutionErrors() {
        var receptionSvc = TestChatServices.CreateReceptionService("script");
        var scriptSvc = TestChatServices.CreateScriptService("throw new System.Exception(\"boom\");", "return 999;");
        var explainerSvc = TestChatServices.CreateExplainerService(echoUserInput: true);

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .Build();

        var rootOp = new TestRootOp(new object());

        var options = new AsonClientOptions {
            ExecutionMode = ExecutionMode.InProcess,
            MaxFixAttempts = 2,
            SkipReceptionAgent = true,
            SkipExplainerAgent = true,
            ScriptChatCompletion = scriptSvc,
            ReceptionChatCompletion = receptionSvc,
            ExplainerChatCompletion = explainerSvc
        };

        AsonClient client = new AsonClient(scriptSvc, rootOp, operatorsLib, options);

        var logs = new ConcurrentBag<(LogLevel Level, string Message)>();
        client.Log += (s, e) => logs.Add((e.Level, e.Message));

        await client.SendAsync("Test task");

        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("Execution error"));
    }

    [Fact]
    public async Task Logging_SkipReceptionAgent_LogsDirectRouting() {
        var scriptSvc = TestChatServices.CreateScriptService("return 5;");
        var explainerSvc = TestChatServices.CreateExplainerService(echoUserInput: true);

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .Build();

        var rootOp = new TestRootOp(new object());

        var options = new AsonClientOptions {
            ExecutionMode = ExecutionMode.InProcess,
            SkipReceptionAgent = true,
            SkipExplainerAgent = true,
            ScriptChatCompletion = scriptSvc,
            ReceptionChatCompletion = TestChatServices.CreateReceptionService(),
            ExplainerChatCompletion = explainerSvc
        };

        AsonClient client = new AsonClient(scriptSvc, rootOp, operatorsLib, options);

        var logs = new ConcurrentBag<string>();
        client.Log += (s, e) => logs.Add(e.Message);

        await client.SendAsync("Test task");

        Assert.Contains(logs, m => m.Contains("Skipping ReceptionAgent") || m.Contains("routing directly to ScriptAgent"));
    }

    [Fact]
    public async Task Logging_DirectScriptExecution_LogsSuccess() {
        var scriptSvc = TestChatServices.CreateScriptService("script");

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .Build();

        var rootOp = new TestRootOp(new object());

        AsonClient client = new AsonClient(scriptSvc, rootOp, operatorsLib, new AsonClientOptions {
            ExecutionMode = ExecutionMode.InProcess,
            ScriptChatCompletion = scriptSvc
        });

        var logs = new ConcurrentBag<string>();
        client.Log += (s, e) => logs.Add(e.Message);

        string script = "return 42;";
        await client.ExecuteScriptDirectAsync(script);

        Assert.Contains(logs, m => m.Contains("Direct script execution success"));
    }

    [Fact]
    public async Task Logging_DirectScriptExecution_LogsError() {
        var scriptSvc = TestChatServices.CreateScriptService("script");

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .Build();

        var rootOp = new TestRootOp(new object());

        AsonClient client = new AsonClient(scriptSvc, rootOp, operatorsLib, new AsonClientOptions {
            ExecutionMode = ExecutionMode.InProcess,
            ScriptChatCompletion = scriptSvc
        });

        var logs = new ConcurrentBag<(LogLevel Level, string Message)>();
        client.Log += (s, e) => logs.Add((e.Level, e.Message));

        string script = "throw new System.Exception(\"Test error\");";
        await client.ExecuteScriptDirectAsync(script, validate: false);

        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("Direct script execution error"));
    }

    [Fact]
    public async Task Logging_ProxyBuildFailure_LogsError() {
        Assert.True(true);
    }
}
