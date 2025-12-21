using Ason.CodeGen;
using Ason.Tests.Operators;
using Ason.Tests.Orchestration;
using AsonRemoteRunner;
using AsonRunner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;

namespace Ason.Tests.RemoteExecution;

public class TestServerHost : IAsyncDisposable {
    private WebApplication? _app;
    public string? ServerUrl { get; private set; }

    public async Task StartAsync() {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAsonScriptRunner();

        var port = GetFreePort();
        ServerUrl = $"http://localhost:{port}";
        builder.WebHost.UseUrls(ServerUrl);

        _app = builder.Build();
        _app.MapAson("/scriptRunnerHub", requireAuthorization: false);

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync() {
        if (_app != null) {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static int GetFreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}


public class RemoteExecutionTests {
    [Theory]
    [MemberData(nameof(TestData.RemoteExecutionTestData), new object[] { new[] { ExecutionMode.ExternalProcess, ExecutionMode.Docker } }, MemberType = typeof(TestData))]
    public async Task E2E_RemoteExecution(ExecutionMode executionMode, string testName, string scriptReply, string expectedReply, string receptionReply) {
        _ = testName;
        await using var server = new TestServerHost();
        await server.StartAsync();

        Ason.AsonClient? client = null;
        try {
            client = await AsonClientEndToEndTests.E2E_Core_WithClientReturn(new AsonClientOptions() {
                ExecutionMode = executionMode,
                UseRemoteRunner = true,
                RemoteRunnerBaseUrl = server.ServerUrl
            }, scriptReply, expectedReply, receptionReply);
        }
        finally {
            if (client != null) {
                ((IDisposable)client).Dispose();
            }
            await server.DisposeAsync();
        }
    }
}
