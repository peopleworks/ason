using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ason.Client.Execution;
using Ason.Tests.Infrastructure;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ason.Tests.Execution;

public class ResultExplainerTests {
    private static ChatCompletionAgent Agent(IChatCompletionService svc) {
        var builder = Kernel.CreateBuilder(); builder.Services.AddSingleton<IChatCompletionService>(svc); var kernel = builder.Build();
        return new ChatCompletionAgent { Name = "Explainer", Instructions = "expl", Kernel = kernel };
    }

    [Fact]
    public async Task ExplainAsync_Basic() {
        var explainer = new ResultExplainer();
        var svc = new StubChatCompletionService(defaultReply: string.Empty);
        svc.Enqueue("This is explanation.");
        var ag = Agent(svc);
        string result = await explainer.ExplainAsync("task", "42", ag, (l,m,e)=>{}, CancellationToken.None);
        Assert.Contains("explanation", result, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainAsync_EmptyRawResult() {
        var explainer = new ResultExplainer();
        var svc = new StubChatCompletionService(defaultReply: string.Empty);
        svc.Enqueue("Handled empty");
        var ag = Agent(svc);
        string result = await explainer.ExplainAsync("task", string.Empty, ag, (l,m,e)=>{}, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(result));
    }


    [Fact]
    public async Task ExplainAsync_LogsDebug() {
        LogLevel? captured = null; string? msg = null;
        var explainer = new ResultExplainer();
        var svc = new StubChatCompletionService(defaultReply: string.Empty);
        svc.Enqueue("done");
        var ag = Agent(svc);
        string result = await explainer.ExplainAsync("t","r", ag, (l,m,e)=> { captured = l; msg = m; }, CancellationToken.None);
        Assert.Equal(LogLevel.Debug, captured);
        Assert.False(string.IsNullOrEmpty(msg));
    }

    [Fact]
    public async Task ExplainAsync_EmptyModelResponse_FallbacksToRaw() {
        LogLevel? captured = null; string? logMsg = null;
        var explainer = new ResultExplainer();
        var svc = new StubChatCompletionService(defaultReply: string.Empty);
        svc.Enqueue("   ");
        var ag = Agent(svc);
        string raw = "RAW_OUTPUT";
        string result = await explainer.ExplainAsync("task", raw, ag, (lvl,msg,ex)=> { captured = lvl; logMsg = msg; }, CancellationToken.None);
        Assert.Equal(raw, result);
        Assert.Equal(LogLevel.Information, captured);
        Assert.False(string.IsNullOrEmpty(logMsg));
    }
}
