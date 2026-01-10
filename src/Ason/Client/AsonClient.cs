using System;
using System.Linq;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Ason.Client.Execution;
using Ason.Client.Orchestration;
using Ason.Invocation;
using Ason.CodeGen;
using System.Threading.Channels;

namespace Ason;

internal readonly record struct ExecOutcome(bool Success, string? RawResult, string? ErrorMessage, string? ExecutedScript, int Attempts);
public sealed record OrchestrationResult(bool Success, string Route, string? ResponseText, string? RawResult, string? GeneratedScript, int Attempts);

public class AsonClient : IChatClient {
    readonly Kernel _scriptKernel;
    readonly Kernel _receptionKernel;
    readonly Kernel _explainerKernel;

    readonly RunnerClient _runner;
    Task _runnerStart = Task.CompletedTask;
    string? _proxies;
    string? _signatures;
    Task _proxyAugmentationTask = Task.CompletedTask; // waits for dynamic MCP code if any

    ChatCompletionAgent? _scriptAgent;
    ChatCompletionAgent? _receptionAgent;
    ChatCompletionAgent? _explainerAgent;
    RootOperator _rootOperator;

    readonly AsonClientOptions _options;
    readonly OperatorsLibrary _operatorsLibrary;
    readonly ILogger? _logger;

    readonly IScriptRepairExecutor _repairExecutor;
    readonly IScriptValidator _validator;
    readonly IResultExplainer _resultExplainer;
    readonly IReceptionRouter _receptionRouter;
    readonly IScriptRouteExecutor _scriptRouteExecutor;

    internal static AsonClient? CurrentInstance;

    public event EventHandler<AsonLogEventArgs>? Log;
    public event EventHandler<RunnerMethodInvokingEventArgs>? MethodInvoking;

    ChatHistoryAgentThread? _agentThread;

    public int MaxScriptFixAttempts { get; } = 2;
    public IChatCompletionService DefaultChatCompletion { get; }
    public IChatCompletionService ScriptChatCompletion { get; }
    public IChatCompletionService ReceptionChatCompletion { get; }
    public IChatCompletionService ExplainerChatCompletion { get; }

    internal Kernel ReceptionKernel => _receptionKernel;

    OrchestrationContext CreateContext(ChatHistoryAgentThread thread, string userTask) =>
        new(thread, userTask, _options.SkipReceptionAgent, _options.SkipExplainerAgent);

    public AsonClient(
        IChatCompletionService defaultChatCompletion,
        RootOperator rootOperator,
        OperatorsLibrary operators, AsonClientOptions? options = null) : this(defaultChatCompletion, rootOperator, operators, options, null, null, null) { }

    internal AsonClient(
        IChatCompletionService defaultChatCompletion,
        RootOperator rootOperator,
        OperatorsLibrary operators,
        AsonClientOptions? options,
        IScriptRepairExecutor? repairExecutor,
        IScriptValidator? validator,
        IResultExplainer? resultExplainer) {
        _options = options ?? new AsonClientOptions();
        _operatorsLibrary = operators ?? throw new ArgumentNullException(nameof(operators));
        _logger = _options.Logger;
        MaxScriptFixAttempts = _options.MaxFixAttempts;

        DefaultChatCompletion = defaultChatCompletion;
        ScriptChatCompletion = _options.ScriptChatCompletion ?? defaultChatCompletion;
        ReceptionChatCompletion = _options.ReceptionChatCompletion ?? defaultChatCompletion;
        ExplainerChatCompletion = _options.ExplainerChatCompletion ?? defaultChatCompletion;

        _rootOperator = rootOperator;
        if (_operatorsLibrary.HasExtractor && !_rootOperator.OperatorInstances.Values.Any(o => o is ExtractionOperator)) {
            var extractor = new ExtractionOperator();
            _rootOperator.OperatorInstances.TryAdd(extractor.Handle, extractor);
        }

        _scriptKernel = BuildKernel(ScriptChatCompletion);
        _receptionKernel = BuildKernel(ReceptionChatCompletion);
        _explainerKernel = BuildKernel(ExplainerChatCompletion);

        _runner = new RunnerClient(rootOperator.OperatorInstances, SynchronizationContext.Current) { Mode = _options.ExecutionMode };
        if (!string.IsNullOrWhiteSpace(_options.RunnerExecutablePath)) {
            _runner.RunnerExecutablePath = _options.RunnerExecutablePath;
        }
        SetupCommonLogging();

        _repairExecutor = repairExecutor ?? new ScriptRepairExecutor();
        _validator = validator ?? new KeywordScriptValidator(_options.ForbiddenScriptKeywords);
        _resultExplainer = resultExplainer ?? new ResultExplainer();
        _receptionRouter = new ReceptionRouter();
        _scriptRouteExecutor = new ScriptRouteExecutor(_repairExecutor, _validator, _resultExplainer, MaxScriptFixAttempts);

        _runner.MethodInvoking += (s, e) => {
            e.UserTask ??= _agentThread?.ChatHistory.Where(m => m.Role == AuthorRole.User).LastOrDefault()?.Content;
            MethodInvoking?.Invoke(this, e);
        };

        CurrentInstance = this;

        BuildInitialProxyLayer();

        if (_options.UseRemoteRunner) {
            if (!string.IsNullOrWhiteSpace(_options.RemoteRunnerBaseUrl)) {
                _ = EnableRemoteRunnerAsync(_options.RemoteRunnerBaseUrl, _options.StopLocalRunnerWhenEnablingRemote, _options.RemoteRunnerDockerImage);
            }
            else {
                throw new ArgumentException("When UseRemoteRunner is true, you must provide a Remote runner base URL. Make sure your server is configured by calling RemoteRunnerServiceExtensions.AddRemoteScriptRunner and RemoteRunnerServiceExtensions.MapRemoteScriptRunner, then set the server URL in RemoteRunnerBaseUrl.", nameof(options));
            }
        }
    }

    void BuildInitialProxyLayer() {
        var snapshot = _operatorsLibrary;

        _proxyAugmentationTask = snapshot.BuildTask.ContinueWith(t => {
            if (t.Status == TaskStatus.RanToCompletion) {
                var (rt, sig, cache) = t.Result;
                IOperatorMethodCache effectiveCache = cache;
                if (_options.AdditionalMethodFilter != null)
                    effectiveCache = new FilteringMethodCache(cache, _options.AdditionalMethodFilter);
                _runner.MethodCache = effectiveCache;

                if (snapshot.McpClients is { Count: > 0 }) {
                    foreach (var client in snapshot.McpClients) {
                        try { if (client != null) _runner.RegisterMcpClient(client); }
                        catch (Exception ex) { OnLog(LogLevel.Error, $"Failed registering MCP client '{client?.ServerInfo?.Name}'", ex); }
                    }
                }

                var instanceDecl = BuildExistingOperatorVariableDeclarations();
                _proxies = rt + instanceDecl;
                _signatures = sig + instanceDecl;
                InitAgents();
            }
            else if (t.IsFaulted) {
                OnLog(LogLevel.Error, "Proxy build failed", t.Exception);
            }
        }, TaskContinuationOptions.ExecuteSynchronously);

        if (!_options.UseRemoteRunner)
            _runnerStart = _runner.StartProcessAsync();
    }

    public async Task EnableRemoteRunnerAsync(string baseUrl, bool stopLocalIfRunning = true, string? dockerImage = null, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("Base URL required", nameof(baseUrl));
        if (stopLocalIfRunning && !_runner.UseRemote) {
            try { await _runner.StopAsync().ConfigureAwait(false); } catch { }
        }
        if (dockerImage is not null) _runner.DockerImage = dockerImage;
        _runner.UseRemote = true;
        _runner.RemoteUrl = baseUrl.TrimEnd('/');
        _runnerStart = _runner.StartProcessAsync();
        await _runnerStart.ConfigureAwait(false);
    }

    void SetupCommonLogging() {
        _runner.Log += (s, e) => {
            var msg = string.IsNullOrEmpty(e.Source) ? e.Message : $"[{e.Source}] {e.Message}";
            if (!string.IsNullOrEmpty(e.Exception)) msg += Environment.NewLine + e.Exception;
            OnLog(e.Level, msg);
        };
    }

    string BuildExistingOperatorVariableDeclarations() {
        var sb = new StringBuilder();
        sb.AppendLine();
        var typeInstanceCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var instance in _rootOperator.OperatorInstances.Values) {
            var type = instance.GetType();
            if (type == typeof(RootOperator)) continue;
            var typeName = type.Name;
            if (!typeInstanceCount.TryGetValue(typeName, out var count)) count = 0;
            string baseVar = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
            string varName = count == 0 ? baseVar : baseVar + count.ToString();
            typeInstanceCount[typeName] = count + 1;
            string proxyName = typeName;
            bool isRootDerived = typeof(RootOperator).IsAssignableFrom(type) && type != typeof(RootOperator);
            string ctor = isRootDerived ? $"new {proxyName}()" : $"new {proxyName}(\"{(instance as OperatorBase)?.Handle ?? typeName}\")";
            sb.AppendLine($"{proxyName} {varName} = {ctor};");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    Kernel BuildKernel(IChatCompletionService chat) {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chat);
        return builder.Build();
    }

    string BuildReceptionInstructions() {
        if (!string.IsNullOrWhiteSpace(_options.ReceptionInstructions)) return _options.ReceptionInstructions;
        return AgentPrompts.ReceptionAgentTemplate;
    }

    void InitAgents() {
        _receptionAgent = CreateAgent(
            "Reception",
            BuildReceptionInstructions(),
            _receptionKernel);
        _scriptAgent = CreateAgent(
            "ScriptGenerator",
            _options.ScriptInstructions ?? string.Format(AgentPrompts.ScriptAgentTemplate, _signatures ?? string.Empty),
            _scriptKernel);
        _explainerAgent = CreateAgent(
            "Explainer",
            _options.ExplainerInstructions ?? AgentPrompts.ExplainerAgentTemplate,
            _explainerKernel);
    }

    protected virtual ChatCompletionAgent CreateAgent(string name, string instructions, Kernel kernel) =>
        new ChatCompletionAgent { Name = name, Instructions = instructions, Kernel = kernel };

    async Task EnsureReadyAsync() {
        await _proxyAugmentationTask.ConfigureAwait(false);
        await _runnerStart.ConfigureAwait(false);
        if (_scriptAgent is null || _receptionAgent is null || _explainerAgent is null || string.IsNullOrWhiteSpace(_proxies)) {
            throw new InvalidOperationException("Proxies not initialized.");
        }
    }

    public async Task<string> ExecuteScriptDirectAsync(string script, bool validate = true) {
        if (string.IsNullOrWhiteSpace(script)) return string.Empty;
        // Entire operation offloaded so any networking / remote runner waits do not sit on UI thread
        return await Task.Run(async () => {
            await EnsureReadyAsync().ConfigureAwait(false);
            if (validate) {
                var validationError = _validator.Validate(script);
                if (validationError is not null) return string.Empty;
            }
            string code = _proxies + "\n" + script;
            try {
                var execResult = await _runner.ExecuteAsync(code).ConfigureAwait(false);
                string raw = execResult?.ToString() ?? string.Empty;
                OnLog(LogLevel.Information, $"Direct script execution success. RawResult={raw}");
                return raw;
            }
            catch (Exception ex) {
                OnLog(LogLevel.Error, "Direct script execution error", ex);
                return "Error";
            }
        }).ConfigureAwait(false);
    }

    async Task<OrchestrationResult> SendDetailedAsync(ChatHistoryAgentThread thread, CancellationToken cancellationToken = default) {
        return await Task.Run(async () => {
            string userTask = ExtractLatestUserMessage(thread) ?? string.Empty;
            var context = CreateContext(thread, userTask);
            var logger = new Action<LogLevel, string, Exception?>((lvl, msg, ex) => OnLog(lvl, msg, ex));
            var decision = await _receptionRouter.DecideAsync(context, _receptionAgent, logger, cancellationToken).ConfigureAwait(false);
            if (decision.Route == OrchestrationRoute.Answer) {
                thread.ChatHistory.AddAssistantMessage(decision.Payload);
                return new OrchestrationResult(true, "answer", decision.Payload, null, null, 1);
            }
            return await _scriptRouteExecutor.ExecuteAsync(context, _proxies, _scriptAgent!, _explainerAgent, _runner, logger, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SendAsync(string message, CancellationToken cancellationToken = default) {
        var response = await ((IChatClient)this).GetResponseAsync(new[] { new ChatMessage(ChatRole.User, message) }, options: default, cancellationToken);
        return response.ToString();
    }

    public async Task<string> SendAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) {
        var response = await ((IChatClient)this).GetResponseAsync(messages, options: default, cancellationToken);
        return response.ToString();
    }

    public async IAsyncEnumerable<string> SendStreamingAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        await foreach (var update in ((IChatClient)this).GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, message) }, options: default, cancellationToken)) {
            var text = ExtractText(update);
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    public async IAsyncEnumerable<string> SendStreamingAsync(IEnumerable<ChatMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        await foreach (var update in ((IChatClient)this).GetStreamingResponseAsync(messages, options: default, cancellationToken)) {
            var text = ExtractText(update);
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    static string GetMessageText(ChatMessage msg) {
        if (msg.Contents is null || msg.Contents.Count == 0) return msg.ToString() ?? string.Empty;
        return string.Concat(msg.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
    }

    static string ExtractText(ChatResponseUpdate update) {
        if (update.Contents is { Count: > 0 }) return string.Concat(update.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(c => c.Text));
        return update.ToString() ?? string.Empty;
    }

    string? ExtractLatestUserMessage(ChatHistoryAgentThread thread) => thread.ChatHistory.Where(m => m.Role == AuthorRole.User).LastOrDefault()?.Content;

    (ChatHistoryAgentThread thread, string userTask) PrepareThreadFromMessages(IEnumerable<ChatMessage> messages) {
        var history = new ChatHistory();
        foreach (var msg in messages) {
            if (msg.Role == ChatRole.System)
                continue;
            var text = GetMessageText(msg);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (msg.Role == ChatRole.User) history.AddUserMessage(text);
            else if (msg.Role == ChatRole.Assistant) history.AddAssistantMessage(text);
        }
        var thread = new ChatHistoryAgentThread(history);
        _agentThread = thread;
        string userTask = history.Where(m => m.Role == AuthorRole.User).LastOrDefault()?.Content ?? string.Empty;
        return (thread, userTask);
    }

    async Task<ChatResponse> IChatClient.GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) {
        await EnsureReadyAsync().ConfigureAwait(false);
        var (thread, _) = PrepareThreadFromMessages(messages);
        var result = await SendDetailedAsync(thread, cancellationToken);
        string text = result.ResponseText ?? (result.Success ? "Task completed" : "Task could not be executed");
        return new List<ChatResponseUpdate> { new(ChatRole.Assistant, text) }.ToChatResponse();
    }

    async IAsyncEnumerable<ChatResponseUpdate> IChatClient.GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var update in InternalStreaming(messages, cancellationToken)) yield return update;
    }

    async IAsyncEnumerable<ChatResponseUpdate> InternalStreaming(IEnumerable<ChatMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) {
        // Execute the heavy orchestration logic on a background thread so that the UI thread is never blocked
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = Task.Run(async () => {
            try {
                await ExecuteInternalStreamingCoreAsync(messages, channel.Writer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnLog(LogLevel.Error, "InternalStreaming background error", ex); }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);

        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) {
            while (channel.Reader.TryRead(out var update)) yield return update;
        }
    }

    private async Task ExecuteInternalStreamingCoreAsync(IEnumerable<ChatMessage> messages, ChannelWriter<ChatResponseUpdate> writer, CancellationToken cancellationToken) {
        await EnsureReadyAsync().ConfigureAwait(false);
        var (thread, userTask) = PrepareThreadFromMessages(messages);
        var context = CreateContext(thread, userTask);
        var logger = new Action<LogLevel, string, Exception?>((lvl, msg, ex) => OnLog(lvl, msg, ex));
        var receptionResult = await _receptionRouter.DecideStreamingAsync(context, messages, _receptionAgent, writer, logger, cancellationToken).ConfigureAwait(false);
        if (!receptionResult.ProceedToScript) {
            var payload = receptionResult.AnswerPayload;
            if (!string.IsNullOrWhiteSpace(payload) && !thread.ChatHistory.Any(m => m.Role == AuthorRole.Assistant && m.Content == payload)) {
                thread.ChatHistory.AddAssistantMessage(payload);
            }
            return;
        }
        await foreach (var update in _scriptRouteExecutor.StreamAsync(context, _proxies, _scriptAgent!, _explainerAgent, _runner, logger, cancellationToken).ConfigureAwait(false)) {
            await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
        }
    }

    void OnLog(LogLevel level, string message, Exception? exception = null) {
        _logger?.Log(level, exception, "{Message}", message);
        Log?.Invoke(this, new AsonLogEventArgs(level, message, exception?.ToString(), nameof(AsonClient)));
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    void IDisposable.Dispose() {
        try { _runner.StopAsync().GetAwaiter().GetResult(); }
        catch { }
    }
}
