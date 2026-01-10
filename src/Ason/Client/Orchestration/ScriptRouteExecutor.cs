using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Ason.Client.Execution;

namespace Ason.Client.Orchestration;

internal interface IScriptRouteExecutor
{
    Task<OrchestrationResult> ExecuteAsync(
        OrchestrationContext context,
        string? proxies,
        ChatCompletionAgent scriptAgent,
        ChatCompletionAgent? explainerAgent,
        RunnerClient runner,
        Action<LogLevel, string, Exception?> log,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        OrchestrationContext context,
        string? proxies,
        ChatCompletionAgent scriptAgent,
        ChatCompletionAgent? explainerAgent,
        RunnerClient runner,
        Action<LogLevel, string, Exception?> log,
        CancellationToken cancellationToken);
}

internal sealed class ScriptRouteExecutor : IScriptRouteExecutor
{
    readonly IScriptRepairExecutor _repairExecutor;
    readonly IScriptValidator _validator;
    readonly IResultExplainer _explainer;
    readonly int _maxAttempts;

    public ScriptRouteExecutor(
        IScriptRepairExecutor repairExecutor,
        IScriptValidator validator,
        IResultExplainer explainer,
        int maxAttempts)
    {
        _repairExecutor = repairExecutor ?? throw new ArgumentNullException(nameof(repairExecutor));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _explainer = explainer ?? throw new ArgumentNullException(nameof(explainer));
        _maxAttempts = maxAttempts;
    }

    public async Task<OrchestrationResult> ExecuteAsync(
        OrchestrationContext context,
        string? proxies,
        ChatCompletionAgent scriptAgent,
        ChatCompletionAgent? explainerAgent,
        RunnerClient runner,
        Action<LogLevel, string, Exception?> log,
        CancellationToken cancellationToken)
    {
        LogDirectRoutingIfNeeded(context, log);
        var outcome = await ExecuteWithRepairsAsync(context.EffectiveTask, proxies, scriptAgent, runner, log, cancellationToken).ConfigureAwait(false);
        if (!outcome.Success && outcome.ErrorMessage?.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Thread.ChatHistory.AddAssistantMessage(outcome.ErrorMessage);
            return new OrchestrationResult(false, "script", outcome.ErrorMessage, null, outcome.ExecutedScript, outcome.Attempts);
        }

        if (!outcome.Success)
        {
            var msg = outcome.ErrorMessage ?? "Task could not be executed.";
            context.Thread.ChatHistory.AddAssistantMessage(msg);
            return new OrchestrationResult(false, "script", msg, null, outcome.ExecutedScript, outcome.Attempts);
        }

        if (string.IsNullOrEmpty(outcome.RawResult))
        {
            return new OrchestrationResult(true, "script", "Task completed", null, outcome.ExecutedScript, outcome.Attempts);
        }

        if (context.SkipExplainer || explainerAgent is null)
        {
            context.Thread.ChatHistory.AddAssistantMessage(outcome.RawResult!);
            return new OrchestrationResult(true, "script", outcome.RawResult, outcome.RawResult, outcome.ExecutedScript, outcome.Attempts);
        }

        string explanation = await _explainer.ExplainAsync(context.EffectiveTask, outcome.RawResult!, explainerAgent, log, cancellationToken).ConfigureAwait(false);
        var trimmed = explanation.Trim();
        context.Thread.ChatHistory.AddAssistantMessage(trimmed);
        return new OrchestrationResult(true, "script", trimmed, outcome.RawResult, outcome.ExecutedScript, outcome.Attempts);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        OrchestrationContext context,
        string? proxies,
        ChatCompletionAgent scriptAgent,
        ChatCompletionAgent? explainerAgent,
        RunnerClient runner,
        Action<LogLevel, string, Exception?> log,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LogDirectRoutingIfNeeded(context, log);
        var outcome = await ExecuteWithRepairsAsync(context.EffectiveTask, proxies, scriptAgent, runner, log, cancellationToken).ConfigureAwait(false);
        if (!outcome.Success && outcome.ErrorMessage?.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) == true)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, outcome.ErrorMessage);
            yield break;
        }

        if (!outcome.Success)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, outcome.ErrorMessage ?? "Task could not be executed.");
            yield break;
        }

        if (string.IsNullOrEmpty(outcome.RawResult))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Task completed");
            yield break;
        }

        if (context.SkipExplainer || explainerAgent is null)
        {
            context.Thread.ChatHistory.AddAssistantMessage(outcome.RawResult!);
            yield return new ChatResponseUpdate(ChatRole.Assistant, outcome.RawResult);
            yield break;
        }

        var explainerInput = $"<task>\n{context.EffectiveTask}\n</task>\n<result>\n{outcome.RawResult}\n</result>";
        var sbExplainer = new StringBuilder();
        var explainerMessages = new[] { new ChatMessageContent(AuthorRole.User, explainerInput) };
        await foreach (var item in explainerAgent.InvokeStreamingAsync(explainerMessages, thread: null, options: null, cancellationToken).ConfigureAwait(false))
        {
            var part = item.Message?.Content;
            if (part is null) continue;
            sbExplainer.Append(part);
            if (part.Length > 0) yield return new ChatResponseUpdate(ChatRole.Assistant, part);
        }

        var full = sbExplainer.ToString();
        if (!string.IsNullOrEmpty(full)) context.Thread.ChatHistory.AddAssistantMessage(full);
    }

    Task<ExecOutcome> ExecuteWithRepairsAsync(
        string userTask,
        string? proxies,
        ChatCompletionAgent scriptAgent,
        RunnerClient runner,
        Action<LogLevel, string, Exception?> log,
        CancellationToken ct)
        => _repairExecutor.ExecuteWithRepairsAsync(
            userTask,
            _maxAttempts,
            proxies,
            scriptAgent,
            runner,
            _validator,
            log,
            ct);

    static void LogDirectRoutingIfNeeded(OrchestrationContext context, Action<LogLevel, string, Exception?> log)
    {
        if (context.HasDirectScriptRoutingReason && context.DirectScriptRoutingReason is { } reason)
        {
            log(LogLevel.Information, reason, null);
        }
    }
}
