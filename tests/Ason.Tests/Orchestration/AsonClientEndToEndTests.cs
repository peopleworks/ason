using Ason.Client.Execution;
using Ason.CodeGen;
using Ason.Tests.Infrastructure;
using Ason.Tests.Operators;
using AsonRunner;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Reflection;

namespace Ason.Tests.Orchestration;

public class AsonClientEndToEndTests {
    private static OperatorsLibrary Snapshot = new OperatorBuilder()
        .AddAssemblies(typeof(RootOperator).Assembly)
        .SetBaseFilter(mi => mi.GetCustomAttribute<AsonMethodAttribute>() != null)
        .Build();

    private static AsonClient CreateClient(
        IChatCompletionService scriptSvc,
        IChatCompletionService? explainerSvc = null,
        IChatCompletionService? answerSvc = null,
        AsonClientOptions? opts = null,
        IScriptValidator? validator = null) {
        var root = new RootOperator(new object());
        var options = opts ?? new AsonClientOptions();
        options = new AsonClientOptions {
            MaxFixAttempts = options.MaxFixAttempts,
            SkipReceptionAgent = options.SkipReceptionAgent,
            SkipExplainerAgent = options.SkipExplainerAgent,
            ScriptChatCompletion = scriptSvc,
            ReceptionChatCompletion = answerSvc ?? scriptSvc,
            ExplainerChatCompletion = explainerSvc ?? scriptSvc,
            ForbiddenScriptKeywords = new string[0],
            AllowTextExtractor = true,
            ExecutionMode = ExecutionMode.InProcess
        };
        var client = new AsonClient(scriptSvc, root, Snapshot, options, null, validator, null);
        return client;
    }

    [Theory]
    [MemberData(nameof(TestData.RemoteExecutionTestData), new object[] { new[] { ExecutionMode.InProcess, ExecutionMode.ExternalProcess, ExecutionMode.Docker } }, MemberType = typeof(TestData))]
    public async Task E2E_AllExecutionModes(ExecutionMode executionMode, string testName, string scriptReply, string expectedReply, string receptionReply) {
        _ = testName;
        await E2E_Core(new AsonClientOptions {
            ExecutionMode = executionMode
        }, scriptReply, expectedReply, receptionReply);
    }

    public static async Task E2E_Core(AsonClientOptions options, string scriptReply, string expectedReply, string receptionReply) {
        await E2E_Core_WithClientReturn(options, scriptReply, expectedReply, receptionReply);
    }

    public static async Task<AsonClient> E2E_Core_WithClientReturn(AsonClientOptions options, string scriptReply, string expectedReply, string receptionReply) {
        var receptionSvc = TestChatServices.CreateReceptionService(receptionReply);
        var scriptSvc = TestChatServices.CreateScriptService(scriptReply);
        var explainerSvc = TestChatServices.CreateExplainerService(echoUserInput: true);

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .Build();

        var rootOp = new TestRootOp(new object());

        AsonClient client = new AsonClient(scriptSvc, rootOp, operatorsLib, new AsonClientOptions() {
            ExecutionMode = options.ExecutionMode,
            MaxFixAttempts = options.MaxFixAttempts,
            Logger = options.Logger,
            ScriptInstructions = options.ScriptInstructions,
            ReceptionInstructions = options.ReceptionInstructions,
            ExplainerInstructions = options.ExplainerInstructions,
            ScriptChatCompletion = options.ScriptChatCompletion ?? scriptSvc,
            ReceptionChatCompletion = options.ReceptionChatCompletion ?? receptionSvc,
            ExplainerChatCompletion = options.ExplainerChatCompletion ?? explainerSvc,
            SkipReceptionAgent = options.SkipReceptionAgent,
            SkipExplainerAgent = options.SkipExplainerAgent,
            AllowTextExtractor = options.AllowTextExtractor,
            ForbiddenScriptKeywords = options.ForbiddenScriptKeywords,
            UseRemoteRunner = options.UseRemoteRunner,
            RemoteRunnerBaseUrl = options.RemoteRunnerBaseUrl,
            RemoteRunnerDockerImage = options.RemoteRunnerDockerImage,
            StopLocalRunnerWhenEnablingRemote = options.StopLocalRunnerWhenEnablingRemote,
            AdditionalMethodFilter = options.AdditionalMethodFilter,
            RunnerExecutablePath = options.RunnerExecutablePath
        });


        IEnumerable<ChatMessage> messages = [
            new ChatMessage(ChatRole.Assistant, "A"),
            new ChatMessage(ChatRole.User, "B"),
            ];

        string reply = string.Empty;
        await foreach (var chunk in ((IChatClient)client).GetStreamingResponseAsync(messages)) {
            reply += chunk;
        }

        Assert.Equal(expectedReply, reply);
        
        return client;
    }

    [Fact]
    public async Task E2E_HappyPath_ScriptAndExplanation() {
        var scriptSvc = TestChatServices.CreateScriptService("return 5;");
        var explainerSvc = TestChatServices.CreateExplainerService(false, "The result is 5.");
        var validator = new KeywordScriptValidator(System.Array.Empty<string>());
        var client = CreateClient(scriptSvc, explainerSvc: explainerSvc, answerSvc: TestChatServices.CreateReceptionService("script"),
            opts: new AsonClientOptions { SkipReceptionAgent = true, SkipExplainerAgent = false }, validator: validator);
        List<(LogLevel lvl, string msg)> logs = new();
        client.Log += (s, e) => logs.Add((e.Level, e.Message));
        var reply = await client.SendAsync("Compute 2+3");
        Assert.Contains("5", reply);
        Assert.True(reply.IndexOf("result", System.StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Contains(logs, l => l.msg.Contains("Execution success"));
    }

    [Fact]
    public async Task E2E_ValidationFailure_Then_Repair() {
        var scriptSvc = TestChatServices.CreateScriptService("BAD return 1;", "return 2;");
        var validator = new TestValidator(s => s.Contains("BAD") ? "Validation failed" : null);
        var client = CreateClient(scriptSvc, answerSvc: TestChatServices.CreateReceptionService("script"),
            opts: new AsonClientOptions { SkipReceptionAgent = true, SkipExplainerAgent = true, MaxFixAttempts = 2 }, validator: validator);
        List<string> logMessages = new();
        client.Log += (s, e) => logMessages.Add(e.Message);
        var reply = await client.SendAsync("Task");
        Assert.Contains("2", reply);
        Assert.Contains(logMessages, m => m.Contains("Validation failed"));
    }

    [Fact]
    public async Task E2E_DirectScriptExecution() {
        string script = """
             var simpleOp = testRootOp.GetSimpleOperator();
            TestModel model = new TestModel() { A = 2, B = 3 };
            return simpleOp.AddNumbers(model);
        """;
        var defaultSvc = TestChatServices.CreateScriptService();

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .Build();

        var rootOp = new TestRootOp(new object());
        AsonClient client = new AsonClient(defaultSvc, rootOp, operatorsLib, new AsonClientOptions() { });
        string reply = await client.ExecuteScriptDirectAsync(script);
        Assert.Equal("5", reply);
    }

    [Fact]
    public async Task E2E_RuntimeException_Then_Repair() {
        var scriptSvc = TestChatServices.CreateScriptService("throw new System.Exception(\"boom\");", "return 7;");
        var validator = new TestValidator(_ => null);
        var client = CreateClient(scriptSvc, answerSvc: TestChatServices.CreateReceptionService("script"),
            opts: new AsonClientOptions { SkipReceptionAgent = true, SkipExplainerAgent = true, MaxFixAttempts = 2 }, validator: validator);
        List<string> logs = new();
        client.Log += (s, e) => logs.Add(e.Message);
        var reply = await client.SendAsync("Compute");
        Assert.Contains("7", reply);
        Assert.Contains(logs, m => m.Contains("Execution error"));
    }

    [Fact]
    public async Task E2E_Explanation_Fallback_When_Empty() {
        var scriptSvc = TestChatServices.CreateScriptService("return 9;");
        var explainerSvc = TestChatServices.CreateExplainerService(false, "   ");
        var validator = new TestValidator(_ => null);
        var client = CreateClient(scriptSvc, explainerSvc: explainerSvc, answerSvc: TestChatServices.CreateReceptionService("script"),
            opts: new AsonClientOptions { SkipReceptionAgent = true, SkipExplainerAgent = false }, validator: validator);
        var reply = await client.SendAsync("Compute");
        Assert.Equal("9", reply);
    }

    // Simple validator wrapper for tests
    private sealed class TestValidator : IScriptValidator {
        private readonly System.Func<string, string?> _fn;
        public TestValidator(System.Func<string, string?> fn) { _fn = fn; }
        public string? Validate(string script) => _fn(script);
    }
}
public class StubView(RootOperator rootOperator) {
    public void CompleteInitialization() {
        rootOperator.AttachChildOperator<SimpleOp>(this);
    }
}