using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Ason.Client.Orchestration;

internal enum OrchestrationRoute
{
    Script,
    Answer
}

internal sealed record ReceptionDecision(OrchestrationRoute Route, string Payload);
internal sealed record ReceptionStreamingResult(bool ProceedToScript, string? AnswerPayload);

internal interface IReceptionRouter
{
    Task<ReceptionDecision> DecideAsync(
        OrchestrationContext context,
        ChatCompletionAgent? receptionAgent,
        Action<LogLevel, string, Exception?> log,
        CancellationToken cancellationToken);

    Task<ReceptionStreamingResult> DecideStreamingAsync(
        OrchestrationContext context,
        IEnumerable<ChatMessage> originalMessages,
        ChatCompletionAgent? receptionAgent,
        ChannelWriter<ChatResponseUpdate> writer,
        Action<LogLevel, string, Exception?> log,
        CancellationToken cancellationToken);
}

internal sealed class ReceptionRouter : IReceptionRouter
{
    public async Task<ReceptionDecision> DecideAsync(
        OrchestrationContext context,
        ChatCompletionAgent? receptionAgent,
        Action<LogLevel, string, Exception?> log,
        CancellationToken cancellationToken)
    {
        if (context.SkipReception || receptionAgent is null)
        {
            const string routingMessage = "Skipping ReceptionAgent; routing directly to ScriptAgent.";
            context.SetDirectScriptRoutingReason(routingMessage);
            context.SetConsolidatedTask(context.UserTask);
            return new ReceptionDecision(OrchestrationRoute.Script, context.UserTask);
        }

        var sb = new StringBuilder();
        try
        {
            var messages = new List<ChatMessageContent> { new ChatMessageContent(AuthorRole.User, context.UserTask) };
            log(LogLevel.Information, $"ReceptionAgent input: {context.UserTask}", null);
            await foreach (var item in receptionAgent.InvokeAsync(messages, context.Thread, null, cancellationToken).ConfigureAwait(false))
            {
                var part = item.Message?.Content;
                if (!string.IsNullOrWhiteSpace(part)) sb.Append(part);
            }

            var decisionRaw = sb.ToString();
            var trimmed = decisionRaw.Trim();
            log(LogLevel.Information, $"ReceptionAgent completed output: {trimmed}", null);
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                context.SetConsolidatedTask(context.UserTask);
                return new ReceptionDecision(OrchestrationRoute.Script, context.UserTask);
            }

            if (trimmed.StartsWith("script", StringComparison.OrdinalIgnoreCase))
            {
                context.SetConsolidatedTask(ExtractTaskBlock(trimmed) ?? context.UserTask);
                return new ReceptionDecision(OrchestrationRoute.Script, context.EffectiveTask);
            }

            context.SetConsolidatedTask(null);
            return new ReceptionDecision(OrchestrationRoute.Answer, trimmed);
        }
        catch (Exception ex)
        {
            log(LogLevel.Error, "ReceptionAgent routing error", ex);
            throw;
        }
    }

    public async Task<ReceptionStreamingResult> DecideStreamingAsync(
        OrchestrationContext context,
        IEnumerable<ChatMessage> originalMessages,
        ChatCompletionAgent? receptionAgent,
        ChannelWriter<ChatResponseUpdate> writer,
        Action<LogLevel, string, Exception?> log,
        CancellationToken cancellationToken)
    {
        if (context.SkipReception || receptionAgent is null)
        {
            const string routingMessage = "Skipping ReceptionAgent; routing directly to ScriptAgent.";
            context.SetDirectScriptRoutingReason(routingMessage);
            context.SetConsolidatedTask(context.UserTask);
            return new ReceptionStreamingResult(true, null);
        }

        var sb = new StringBuilder();
        bool bufferingPossibleScript = true;
        bool collectingTaskBlock = false;
        bool proceedToScript = false;

        log(LogLevel.Information, $"ReceptionAgent input:\n{ConvertMessagesToString(originalMessages)}", null);
        await foreach (var item in receptionAgent.InvokeStreamingAsync(thread: context.Thread, options: null, cancellationToken).ConfigureAwait(false))
        {
            var part = item.Message?.Content;
            if (part is null) continue;

            sb.Append(part);
            var currentFull = sb.ToString();
            var currentTrimmedStart = currentFull.TrimStart();

            if (bufferingPossibleScript)
            {
                if (currentTrimmedStart.StartsWith("script", StringComparison.OrdinalIgnoreCase))
                {
                    proceedToScript = true;
                    bufferingPossibleScript = false;
                    collectingTaskBlock = true;
                    continue;
                }

                if ("script".StartsWith(currentTrimmedStart, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await writer.WriteAsync(new ChatResponseUpdate(ChatRole.Assistant, currentFull), cancellationToken).ConfigureAwait(false);
                bufferingPossibleScript = false;
            }
            else if (collectingTaskBlock)
            {
                if (currentFull.IndexOf("</task>", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    break;
                }
            }
            else
            {
                await writer.WriteAsync(new ChatResponseUpdate(ChatRole.Assistant, part), cancellationToken).ConfigureAwait(false);
            }
        }

        var all = sb.ToString();
        log(LogLevel.Information, $"ReceptionAgent completed output: {all}", null);
        if (proceedToScript)
        {
            context.SetConsolidatedTask(ExtractTaskBlock(all) ?? context.UserTask);
            return new ReceptionStreamingResult(true, null);
        }

        var full = all.Trim();
        if (string.IsNullOrWhiteSpace(full))
        {
            context.SetConsolidatedTask(context.UserTask);
            return new ReceptionStreamingResult(true, null);
        }

        if (full.Equals("script", StringComparison.OrdinalIgnoreCase))
        {
            context.SetConsolidatedTask(context.UserTask);
            return new ReceptionStreamingResult(true, null);
        }

        context.SetConsolidatedTask(null);
        return new ReceptionStreamingResult(false, full);
    }

    static string ConvertMessagesToString(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System) continue;
            var text = GetMessageText(message);
            sb.Append('[').Append(message.Role).Append("] ");
            sb.AppendLine(string.IsNullOrWhiteSpace(text) ? "<empty>" : text);
        }
        return sb.ToString().TrimEnd();
    }

    static string GetMessageText(ChatMessage msg)
    {
        if (msg.Contents is null || msg.Contents.Count == 0)
        {
            return msg.ToString() ?? string.Empty;
        }

        return string.Concat(msg.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
    }

    static string? ExtractTaskBlock(string receptionAgentOutput)
    {
        int start = receptionAgentOutput.IndexOf("<task>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        int end = receptionAgentOutput.IndexOf("</task>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        int innerStart = start + "<task>".Length;
        var inner = receptionAgentOutput.Substring(innerStart, end - innerStart).Trim();
        return string.IsNullOrWhiteSpace(inner) ? null : inner;
    }
}
