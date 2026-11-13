using System.IO;
using Xunit;

namespace SessionDeck.Tests;

public class HasConversationTests
{
    static string Temp(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "sd-conv-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Metadata_only_transcripts_do_not_count_as_a_conversation()
    {
        string p = Temp("{\"type\":\"summary\",\"summary\":\"x\"}\n{\"type\":\"file-history-snapshot\"}\n");
        try { Assert.False(SessionScanner.HasConversation(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void A_user_message_makes_it_resumable()
    {
        string p = Temp("{\"type\":\"summary\"}\n{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
        try { Assert.True(SessionScanner.HasConversation(p)); } finally { File.Delete(p); }
    }

    [Fact]
    public void Missing_or_blank_paths_are_not_resumable()
    {
        Assert.False(SessionScanner.HasConversation(""));
        Assert.False(SessionScanner.HasConversation(Path.Combine(Path.GetTempPath(), "sd-conv-missing.jsonl")));
    }

    [Fact]
    public void A_chat_is_resumed_whenever_its_transcript_exists_and_a_terminal_only_once_something_was_said()
    {
        // What a chat's transcript holds after "/resume" was typed into it and nothing else (Claude Code 2.1.323):
        // starting the session again under its id is refused ("already in use"), resuming it works.
        string commandOnly = Temp("{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"/resume\"}\n{\"type\":\"queue-operation\",\"operation\":\"dequeue\"}\n{\"type\":\"attachment\"}\n{\"type\":\"system\",\"subtype\":\"local_command\",\"content\":\"/resume\"}\n");
        string said = Temp("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
        string missing = Path.Combine(Path.GetTempPath(), "sd-conv-missing.jsonl");
        try
        {
            Assert.True(SessionScanner.CanResume(commandOnly, chat: true));
            Assert.False(SessionScanner.CanResume(commandOnly, chat: false));
            Assert.True(SessionScanner.CanResume(said, chat: true));
            Assert.True(SessionScanner.CanResume(said, chat: false));
            // No transcript at all: nothing to resume, and the id is free to start under.
            Assert.False(SessionScanner.CanResume(missing, chat: true));
            Assert.False(SessionScanner.CanResume(missing, chat: false));
            Assert.False(SessionScanner.CanResume("", chat: true));
        }
        finally { File.Delete(commandOnly); File.Delete(said); }
    }
}
