using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder();

builder.Logging.ClearProviders();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CalculatorTool>();

await builder.Build().RunAsync();

[McpServerToolType]
public class CalculatorTool {
    [McpServerTool, Description("Adds two numbers")]
    public static int Add(MyTestModel model) => model.A + model.B;
}

public class MyTestModel {
    public int A { get; set; }
    public int B { get; set; }
}