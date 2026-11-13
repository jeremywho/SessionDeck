using System.IO;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

public class ChatNamesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-chatnames-" + Guid.NewGuid().ToString("N"));

    public ChatNamesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("  my   name  ", "my name")]
    [InlineData("line one\r\nline two\ttabbed", "line one line two tabbed")]
    [InlineData("bell\u0007rung", "bell rung")]
    [InlineData("", "")]
    [InlineData(" \r\n\t ", "")]
    public void A_name_is_one_line_with_single_spaces(string typed, string kept) =>
        Assert.Equal(kept, ChatNames.Clean(typed));

    [Fact]
    public void A_name_is_cut_at_the_limit_and_never_through_a_pair()
    {
        Assert.Equal(new string('x', ChatNames.Longest), ChatNames.Clean(new string('x', ChatNames.Longest + 60)));
        Assert.Equal(new string('x', ChatNames.Longest - 2) + "😀", ChatNames.Clean(new string('x', ChatNames.Longest - 2) + "😀 and more"));
        Assert.Equal(new string('x', ChatNames.Longest - 1), ChatNames.Clean(new string('x', ChatNames.Longest - 1) + "😀"));
        Assert.Equal(new string('x', ChatNames.Longest - 1), ChatNames.Clean(new string('x', ChatNames.Longest - 1) + "   y"));
    }

    [Fact]
    public void A_name_is_kept_by_conversation_and_by_agent_and_the_last_one_given_stands()
    {
        var agy = new ChatNames(_dir, "Agy");
        Assert.Equal("", agy.Of("c81acbc4-0d63-4f5f-884a-83b14e885742"));
        Assert.True(agy.Set("c81acbc4-0d63-4f5f-884a-83b14e885742", "first name"));
        Assert.True(agy.Set("c81acbc4-0d63-4f5f-884a-83b14e885742", "second name"));
        Assert.True(agy.Set("50ed58d2-7396-408d-90df-76d5fbc8a101", "another one"));

        var again = new ChatNames(_dir, "Agy");
        Assert.Equal(("second name", "another one", ""), (again.Of("c81acbc4-0d63-4f5f-884a-83b14e885742"), again.Of("50ed58d2-7396-408d-90df-76d5fbc8a101"), again.Of("never-named")));
        Assert.Equal("", new ChatNames(_dir, "OpenCode").Of("c81acbc4-0d63-4f5f-884a-83b14e885742"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "chat-names"), "*.tmp"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\..\outside")]
    [InlineData("a/b:c*d?")]
    [InlineData("CON")]
    public void An_id_the_agent_chose_never_names_the_file(string session)
    {
        var names = new ChatNames(_dir, "OpenCode");
        Assert.True(names.Set(session, "named"));
        Assert.Equal("named", names.Of(session));
        string file = Directory.GetFiles(Path.Combine(_dir, "chat-names")).Single();
        Assert.Equal(names.PathOf(session), file);
        Assert.Matches("^[0-9a-f]{32}\\.txt$", Path.GetFileName(file));
    }

    [Fact]
    public void A_name_that_cannot_be_kept_is_said_to_be_so()
    {
        string blocked = Path.Combine(_dir, "a-file-where-the-folder-would-go");
        File.WriteAllText(blocked, "");
        var names = new ChatNames(blocked, "Agy");
        Assert.False(names.Set("c81acbc4-0d63-4f5f-884a-83b14e885742", "named"));
        Assert.Equal("", names.Of("c81acbc4-0d63-4f5f-884a-83b14e885742"));
        Assert.False(new ChatNames(_dir, "Agy").Set("", "nobody"));
    }

    [Fact]
    public void Names_given_nowhere_to_be_kept_last_as_long_as_the_translator()
    {
        var names = new MemoryChatNames();
        Assert.Equal("", names.Of("ses_1"));
        Assert.True(names.Set("ses_1", "kept in memory"));
        Assert.Equal(("kept in memory", ""), (names.Of("ses_1"), names.Of("ses_2")));
    }
}
