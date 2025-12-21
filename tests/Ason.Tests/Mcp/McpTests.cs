using Ason.CodeGen;
using Ason.Tests.Infrastructure;
using Ason.Tests.Operators;
using Ason.Tests.Orchestration;
using AsonRunner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ason.Tests.Mcp;

public class McpClientTests  {

    [Fact]
    public async Task AddTool_ShouldReturnCorrectResult() {
        await E2E_Core_Mcp(new AsonClientOptions {
            ExecutionMode = ExecutionMode.InProcess,
        });
    }


    public async Task E2E_Core_Mcp(AsonClientOptions options) {
        string receptionReply = """
             script
             <task>
             some task description
             </task>
        """;
        string scriptReply = """
             TestModel model = new TestModel() { A = 2, B = 3 };
             return TestMcpServerMcp.Add(model);
        """;
        string expectedReply = "<task>\nsome task description\n</task>\n<result>\n5\n</result>";
        var receptionSvc = TestChatServices.CreateReceptionService(receptionReply);
        var scriptSvc = TestChatServices.CreateScriptService(scriptReply);
        var explainerSvc = TestChatServices.CreateExplainerService(echoUserInput: true);

        OperatorsLibrary operatorsLib = new OperatorBuilder()
            .AddAssemblies(typeof(TestRootOp).Assembly)
            .AddMcp(await CreateTestMcpClientAsync())
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

        var reply = await client.SendAsync("A");

        Assert.Equal(reply, expectedReply);
    }


    async Task<McpClient> CreateTestMcpClientAsync() {
        return await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions {
                Name = "TestMcpServer",
                Command = "dotnet",
                Arguments = ["TestMcpServer.dll"]
            }));
    }
}