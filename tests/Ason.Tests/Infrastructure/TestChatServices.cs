using System;

namespace Ason.Tests.Infrastructure;

internal static class TestChatServices {
    public static StubChatCompletionService CreateScriptService(params string[] replies) => Create(defaultReply: "// default script\nreturn null;", echoUserInput: false, replies);
    public static StubChatCompletionService CreateReceptionService(params string[] replies) => Create(defaultReply: "script", echoUserInput: false, replies);
    public static StubChatCompletionService CreateExplainerService(bool echoUserInput = false, params string[] replies) => Create(defaultReply: "Explanation", echoUserInput, replies);

    private static StubChatCompletionService Create(string defaultReply, bool echoUserInput, params string[] replies) {
        var svc = new StubChatCompletionService(defaultReply, echoUserInput);
        if (replies is { Length: > 0 }) svc.Enqueue(replies);
        return svc;
    }
}
