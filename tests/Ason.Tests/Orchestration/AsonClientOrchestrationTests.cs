using Xunit;
using Ason;
using System.Threading.Tasks;
using Ason.Client.Execution;
using AsonRunner;
using System.Collections.Generic;
using Ason.CodeGen;
using System.Reflection;
using Ason.Tests.Infrastructure;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Ason.Tests.Orchestration;

public class AsonClientOrchestrationTests {

    private static readonly OperatorsLibrary Snapshot = new OperatorBuilder()
        .AddAssemblies(typeof(RootOperator).Assembly)
        .SetBaseFilter(mi => mi.GetCustomAttribute<AsonMethodAttribute>() != null)
        .Build();

    private static readonly Func<string, IEnumerable<string>> StreamingChunker = content => {
        if (content == "script") return new[] { "scr", "ipt " };
        if (content == "  SCRIPT\n") return new[] { "  ", "SCRIPT\n" };
        return new[] { content };
    };

    private static StubChatCompletionService CreateService(params string[] responses) {
        var svc = new StubChatCompletionService(chunker: StreamingChunker);
        if (responses is { Length: > 0 }) svc.Enqueue(responses);
        return svc;
    }

    private AsonClient CreateClient(IChatCompletionService svc, AsonClientOptions opts) {
        var root = new RootOperator(new object());
        var client = new AsonClient(svc, root, Snapshot, new AsonClientOptions {
            SkipReceptionAgent = opts.SkipReceptionAgent,
            SkipExplainerAgent = opts.SkipExplainerAgent,
            MaxFixAttempts = opts.MaxFixAttempts,
            ExecutionMode = ExecutionMode.InProcess
        });
        return client;
    }

    [Fact]
    public async Task Route_DirectAnswer_Path() {
        var svc = CreateService("Plain answer");
        var opts = new AsonClientOptions { SkipReceptionAgent = false, SkipExplainerAgent = true };
        var client = CreateClient(svc, opts);
        var result = await client.SendAsync("What is test?");
        Assert.Contains("Plain answer", result);
    }

    [Fact]
    public async Task Route_Script_Path_With_Explanation() {
        var svc = CreateService("script", "return 1;", "Explained result");
        var opts = new AsonClientOptions { SkipReceptionAgent = false, SkipExplainerAgent = false };
        var client = CreateClient(svc, opts);
        var reply = await client.SendAsync("Compute something");
        Assert.Contains("Explained", reply);
    }

    [Fact]
    public async Task Route_Script_Path_When_SkipAnswerAgent() {
        var svc = CreateService("return 2;","Explained 2");
        var opts = new AsonClientOptions { SkipReceptionAgent = true, SkipExplainerAgent = false };
        var client = CreateClient(svc, opts);
        var reply = await client.SendAsync("Do it");
        Assert.Contains("Explained", reply);
    }


    [Fact]
    public async Task Route_Answer_EmptyFallsBack() {
        var svc = CreateService("   ");
        var opts = new AsonClientOptions { SkipReceptionAgent = false, SkipExplainerAgent = true };
        var client = CreateClient(svc, opts);
        var reply = await client.SendAsync("Question");
        Assert.False(string.IsNullOrEmpty(reply));
    }

    [Fact]
    public async Task Streaming_Answer_Path() {
        var svc = CreateService("Plain answer");
        var opts = new AsonClientOptions { SkipReceptionAgent = false, SkipExplainerAgent = true };
        var client = CreateClient(svc, opts);
        var chunks = new List<string>();
        await foreach (var c in client.SendStreamingAsync("hi")) chunks.Add(c);
        Assert.Contains(chunks, c => c.Contains("Plain answer"));
    }

    [Fact]
    public async Task Streaming_Script_Path_DirectSkipAnswer() {
        var svc = CreateService("return 3;", "Explanation 3");
        var opts = new AsonClientOptions { SkipReceptionAgent = true, SkipExplainerAgent = false };
        var client = CreateClient(svc, opts);
        var list = new List<string>();
        await foreach (var c in client.SendStreamingAsync("task")) list.Add(c);
        Assert.True(list.Count > 0);
    }

    [Fact]
    public async Task ExecuteScriptDirectAsync_ValidationBlocks() {
        var svc = CreateService("script");
        var opts = new AsonClientOptions();
        var client = CreateClient(svc, opts);
        var output = await client.ExecuteScriptDirectAsync("System.Reflection.Assembly.Load(\"x\");", validate:true);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task ExecuteScriptDirectAsync_Succeeds() {
        var svc = CreateService("script");
        var opts = new AsonClientOptions();
        var client = CreateClient(svc, opts);
        var script = "return 123;";
        var output = await client.ExecuteScriptDirectAsync(script, validate:true);
        Assert.Contains("123", output);
    }

}
