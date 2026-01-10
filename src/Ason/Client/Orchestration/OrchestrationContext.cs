using System;
using Microsoft.SemanticKernel.Agents;

namespace Ason.Client.Orchestration;

internal sealed class OrchestrationContext
{
    public OrchestrationContext(ChatHistoryAgentThread thread, string userTask, bool skipReception, bool skipExplainer)
    {
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        UserTask = userTask ?? string.Empty;
        SkipReception = skipReception;
        SkipExplainer = skipExplainer;
    }

    public ChatHistoryAgentThread Thread { get; }
    public string UserTask { get; }
    public bool SkipReception { get; }
    public bool SkipExplainer { get; }
    public string? ConsolidatedUserTask { get; private set; }
    public string EffectiveTask => ConsolidatedUserTask ?? UserTask;
    public string? DirectScriptRoutingReason { get; private set; }
    public bool HasDirectScriptRoutingReason => !string.IsNullOrWhiteSpace(DirectScriptRoutingReason);

    public void SetConsolidatedTask(string? task)
    {
        ConsolidatedUserTask = string.IsNullOrWhiteSpace(task) ? null : task.Trim();
    }

    public void SetDirectScriptRoutingReason(string? reason)
    {
        DirectScriptRoutingReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }
}
