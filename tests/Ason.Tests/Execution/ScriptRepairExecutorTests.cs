using System.Collections.Concurrent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ason;
using Ason.Client.Execution;
using Ason.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System.Text;

namespace Ason.Tests.Execution;

public class ScriptRepairExecutorTests {

    private static ChatCompletionAgent CreateAgent(IChatCompletionService svc) {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(svc);
        var kernel = builder.Build();
        return new ChatCompletionAgent { Name = "ScriptGenerator", Instructions = "gen", Kernel = kernel };
    }

    private static RunnerClient CreateInProcRunner() => new RunnerClient(new ConcurrentDictionary<string, OperatorBase>(), synchronizationContext: null) { Mode = AsonRunner.ExecutionMode.InProcess };

    private sealed class TestValidator : IScriptValidator {
        private readonly Func<string, string?> _fn; public TestValidator(Func<string,string?> fn) { _fn = fn; }
        public string? Validate(string script) => _fn(script);
    }

    private static string GenerateProxies() => ProxySerializer.SerializeAll(typeof(RootOperator).Assembly);

    private static ScriptRepairExecutor Executor() => new();

    private static Task<ExecOutcome> RunAsync(ScriptRepairExecutor exec, string userTask, int maxAttempts, string[] agentReplies, IScriptValidator validator, string? proxies = null, CancellationToken ct = default) {
        var svc = new StubChatCompletionService("// default script\nreturn null;");
        svc.Enqueue(agentReplies);
        var agent = CreateAgent(svc);
        var runner = CreateInProcRunner();
        return exec.ExecuteWithRepairsAsync(userTask, maxAttempts, proxies, agent, runner, validator, (lvl,msg,ex)=>{}, ct);
    }

    [Fact]
    public async Task Exceeds_Max_Attempts_Fails() {
        var outcome = await RunAsync(Executor(), "task", 1, new[]{"BAD 1;","BAD 2;"}, new TestValidator(_=>"always bad"), GenerateProxies());
        Assert.False(outcome.Success);
        Assert.Equal(2, outcome.Attempts); // attempts = maxAttempts + 1
        Assert.Contains("always bad", outcome.ErrorMessage);
    }


    [Fact]
    public async Task Proxies_Missing_Fails() {
        var outcome = await RunAsync(Executor(), "task", 0, new[]{"return 1;"}, new TestValidator(_=>null), proxies:null);
        Assert.False(outcome.Success);
        Assert.Equal("Proxies not initialized", outcome.ErrorMessage);
    }

    [Fact]
    public async Task Cancellation_Throws() {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async ()=> {
            await RunAsync(Executor(), "task", 2, new[]{"return 1;"}, new TestValidator(_=>null), GenerateProxies(), cts.Token);
        });
    }
}
