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
}
