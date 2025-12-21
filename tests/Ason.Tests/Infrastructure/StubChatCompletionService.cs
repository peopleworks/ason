using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.AI;

namespace Ason.Tests.Infrastructure;

// Deterministic stub implementing just enough of IChatCompletionService for tests.
internal sealed class StubChatCompletionService : IChatCompletionService {
    private readonly ConcurrentQueue<string> _replies = new();
    private readonly string _defaultReply;
    private readonly bool _echoLastUser;
    private readonly Func<string, IEnumerable<string>> _chunker;
    private readonly Func<int, Exception?>? _exceptionFactory;
    private int _invocationCount;

    public StubChatCompletionService(
        string defaultReply = "script",
        bool echoLastUserMessage = false,
        Func<string, IEnumerable<string>>? chunker = null,
        Func<int, Exception?>? exceptionFactory = null) {
        _defaultReply = defaultReply;
        _echoLastUser = echoLastUserMessage;
        _chunker = chunker ?? (text => new[] { text });
        _exceptionFactory = exceptionFactory;
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public void Enqueue(params string[] replies) {
        foreach (var reply in replies ?? Array.Empty<string>()) {
            if (reply is null) continue;
            _replies.Enqueue(reply);
        }
    }

    private void MaybeThrow() {
        if (_exceptionFactory is null) return;
        int invocation = Interlocked.Increment(ref _invocationCount);
        var exception = _exceptionFactory(invocation);
        if (exception is not null) throw exception;
    }

    private string Next(ChatHistory? chatHistory = null, IEnumerable<ChatMessageContent>? messageList = null) {
        if (_replies.TryDequeue(out var reply)) return reply;
        if (_echoLastUser) {
            var fallback = ExtractLastUserMessage(chatHistory, messageList);
            if (!string.IsNullOrWhiteSpace(fallback)) return fallback!;
        }
        return _defaultReply;
    }

    private static string? ExtractLastUserMessage(ChatHistory? chatHistory, IEnumerable<ChatMessageContent>? messageList) {
        if (chatHistory is not null) {
            for (int i = chatHistory.Count - 1; i >= 0; i--) {
                var msg = chatHistory[i];
                if (msg.Role == AuthorRole.User && !string.IsNullOrWhiteSpace(msg.Content)) {
                    return msg.Content;
                }
            }
        }
        if (messageList is not null) {
            foreach (var msg in messageList.Reverse()) {
                if (msg.Role == AuthorRole.User && !string.IsNullOrWhiteSpace(msg.Content)) {
                    return msg.Content;
                }
            }
        }
        return null;
    }

    public async IAsyncEnumerable<ChatMessageContent> GetChatMessageContentsAsync(IEnumerable<ChatMessageContent> messages, ChatOptions? options = null, Kernel? kernel = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        MaybeThrow();
        var reply = Next(messageList: messages);
        yield return new ChatMessageContent(AuthorRole.Assistant, reply);
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(string prompt, ChatOptions? options = null, Kernel? kernel = null, CancellationToken cancellationToken = default) {
        MaybeThrow();
        var reply = Next();
        IReadOnlyList<ChatMessageContent> list = new List<ChatMessageContent> { new ChatMessageContent(AuthorRole.Assistant, reply) };
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) {
        MaybeThrow();
        var reply = Next(chatHistory: chatHistory);
        IReadOnlyList<ChatMessageContent> list = new List<ChatMessageContent> { new ChatMessageContent(AuthorRole.Assistant, reply) };
        return Task.FromResult(list);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        MaybeThrow();
        var reply = Next(chatHistory: chatHistory);
        foreach (var chunk in _chunker(reply)) {
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, chunk);
        }
        await Task.CompletedTask;
    }
}
