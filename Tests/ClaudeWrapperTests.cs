using System.IO;
using SessionDeck.Host;
using Xunit;

namespace SessionDeck.Tests;

[Collection("HostManager exe locators")]
public class ClaudeWrapperTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-wrapper-" + Guid.NewGuid().ToString("N"));
    readonly Func<string?> _locator = ClaudeProfiles.SwiftScriptLocator;
    readonly Func<IReadOnlyList<string>> _others = SessionScanner.OtherConfigDirs;

    public ClaudeWrapperTests()
    {
        Directory.CreateDirectory(_dir);
        ClaudeProfiles.ForgetWrappers();
    }

    public void Dispose()
    {
        ClaudeProfiles.SwiftScriptLocator = _locator;
        SessionScanner.OtherConfigDirs = _others;
        ClaudeProfiles.ForgetWrappers();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    const string Script = "C:\\Tools\\bin\\claude-swift.cmd";
    const string Id = "0b6f1c1e-5a53-4e0b-9a0a-3f2f8a6f1d11";

    const string Wrapper = """
        @echo off
        setlocal
        set "CLAUDE_CONFIG_DIR=%USERPROFILE%\.claude-swift"
        set "ANTHROPIC_BASE_URL=http://model.example:11234"
        set /p ANTHROPIC_AUTH_TOKEN=<"%USERPROFILE%\.claude-swift\key"
        set "ANTHROPIC_DEFAULT_OPUS_MODEL=swift-1.5"
        claude --model swift-1.5 %*
        """;

    [Fact]
    public void The_config_folder_is_the_one_the_script_sets_whatever_its_line_endings()
    {
        string expected = Path.Combine(UserHome, ".claude-swift");
        foreach (string text in new[] { Wrapper.Replace("\r\n", "\n"), Wrapper.Replace("\r\n", "\n").Replace("\n", "\r\n") })
        {
            var w = ClaudeProfiles.Read("swift", Script, text)!;
            Assert.Equal(("swift", Script, expected), (w.Profile, w.ScriptPath, w.ConfigDir));
            Assert.Equal((Path.Combine(expected, "projects"), Path.Combine(expected, "sessions")), (w.ProjectsDir, w.SessionsDir));
        }
        Assert.Equal(@"D:\cfg dir", ClaudeProfiles.Read("swift", Script, "SET CLAUDE_CONFIG_DIR=D:\\cfg dir\r\nclaude %*\r\n")!.ConfigDir);
        Assert.Equal(@"D:\cfg", ClaudeProfiles.Read("swift", Script, "  @set \"claude_config_dir=D:\\cfg\"  \nclaude %*")!.ConfigDir);
    }

    [Fact]
    public void A_script_that_sets_no_config_folder_uses_the_clis_own()
    {
        var w = ClaudeProfiles.Read("swift", Script, "@echo off\r\nset \"ANTHROPIC_BASE_URL=http://model.example\"\r\nclaude %*\r\n")!;
        Assert.Equal(ClaudeProfiles.DefaultConfigDir, w.ConfigDir);
        Assert.Equal(Path.Combine(UserHome, ".claude"), ClaudeProfiles.DefaultConfigDir);
    }

    [Theory]
    [InlineData("set \"CLAUDE_CONFIG_DIR=%~dp0cfg\"\r\nclaude %*")]
    [InlineData("set \"CLAUDE_CONFIG_DIR=%NO_SUCH_VARIABLE_HERE%\\cfg\"\r\nclaude %*")]
    [InlineData("set \"CLAUDE_CONFIG_DIR=C:\\cfg\\%NO_SUCH_VARIABLE_HERE%\"\r\nclaude %*")]
    [InlineData("set \"CLAUDE_CONFIG_DIR=relative\\cfg\"\r\nclaude %*")]
    [InlineData("set \"CLAUDE_CONFIG_DIR=\"\r\nclaude %*")]
    [InlineData("set \"CLAUDE_CONFIG_DIR=C:\\a\"\r\nset \"CLAUDE_CONFIG_DIR=C:\\b\"\r\nclaude %*")]
    [InlineData("if defined X (set \"CLAUDE_CONFIG_DIR=C:\\a\")\r\nclaude %*")]
    [InlineData("rem CLAUDE_CONFIG_DIR is set elsewhere\r\nclaude %*")]
    public void A_config_folder_named_in_a_way_that_cannot_be_followed_is_no_wrapper_at_all(string script) =>
        Assert.Null(ClaudeProfiles.Read("swift", Script, script));

    [Fact]
    public void The_swift_profile_is_its_script_on_the_path_and_nothing_stands_in_when_it_is_gone()
    {
        Assert.True(ClaudeProfiles.IsWrapper("swift") && ClaudeProfiles.IsWrapper("Swift"));
        Assert.False(ClaudeProfiles.IsWrapper("") || ClaudeProfiles.IsWrapper("gmail"));
        Assert.Null(ClaudeProfiles.Wrapper("gmail"));

        string script = Path.Combine(_dir, "claude-swift.cmd"), config = Path.Combine(_dir, "cfg");
        File.WriteAllText(script, $"@echo off\r\nset \"CLAUDE_CONFIG_DIR={config}\"\r\nclaude --model swift-1.5 %*\r\n");
        ClaudeProfiles.SwiftScriptLocator = () => script;
        ClaudeProfiles.ForgetWrappers();
        var found = ClaudeProfiles.Require("swift");
        Assert.Equal((script, config), (found.ScriptPath, found.ConfigDir));
        Assert.Equal(new[] { config }, ClaudeProfiles.WrapperConfigDirs());
        Assert.Equal((config, ClaudeProfiles.DefaultConfigDir), (ClaudeProfiles.ConfigDirFor("swift"), ClaudeProfiles.ConfigDirFor("")));

        ClaudeProfiles.SwiftScriptLocator = () => null;
        ClaudeProfiles.ForgetWrappers();
        Assert.Null(ClaudeProfiles.Wrapper("swift"));
        Assert.Empty(ClaudeProfiles.WrapperConfigDirs());
        Assert.Equal(ClaudeProfiles.DefaultConfigDir, ClaudeProfiles.ConfigDirFor("swift"));
        var refused = Assert.Throws<InvalidOperationException>(() => ClaudeProfiles.Require("swift"));
        Assert.Contains("claude-swift", refused.Message);

        // A wrapper that shares the CLI's own folder adds no folder to read.
        File.WriteAllText(script, "@echo off\r\nclaude --model swift-1.5 %*\r\n");
        ClaudeProfiles.SwiftScriptLocator = () => script;
        ClaudeProfiles.ForgetWrappers();
        Assert.NotNull(ClaudeProfiles.Wrapper("swift"));
        Assert.Empty(ClaudeProfiles.WrapperConfigDirs());
    }

    [Fact]
    public void A_session_found_in_the_wrappers_folder_is_brought_in_on_the_wrapper()
    {
        string script = Path.Combine(_dir, "claude-swift.cmd"), config = Path.Combine(_dir, "cfg");
        File.WriteAllText(script, $"set \"CLAUDE_CONFIG_DIR={config}\"\r\nclaude %*\r\n");
        ClaudeProfiles.SwiftScriptLocator = () => script;
        ClaudeProfiles.ForgetWrappers();

        Assert.Equal("swift", ClaudeProfiles.ProfileOfConfigDir(config));
        Assert.Equal("swift", ClaudeProfiles.ProfileOfConfigDir(config.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.Equal("", ClaudeProfiles.ProfileOfConfigDir(""));
        Assert.Equal("", ClaudeProfiles.ProfileOfConfigDir(Path.Combine(_dir, "elsewhere")));

        var outside = new SessionInfo { SessionId = Id, Cwd = @"C:\Work", Name = "running in a terminal", Provider = SessionProvider.Claude, ConfigDir = config };
        var adopted = SavedSession.Adopting(outside);
        Assert.Equal((Id, @"C:\Work", "running in a terminal", SessionProvider.Claude, "swift"), (adopted.Id, adopted.Cwd, adopted.Name, adopted.Provider, adopted.Profile));
        Assert.Equal("", SavedSession.Adopting(new SessionInfo { SessionId = Id, Cwd = @"C:\Work", Provider = SessionProvider.Claude }).Profile);

        ClaudeProfiles.SwiftScriptLocator = () => null;
        ClaudeProfiles.ForgetWrappers();
        Assert.Equal("", ClaudeProfiles.ProfileOfConfigDir(config));
    }

    static readonly ClaudeWrapper Swift = new("swift", @"C:\Users\Some One\.local\bin\claude-swift.cmd", @"C:\Users\Some One\.claude-swift");

    [Fact]
    public void A_terminal_session_runs_the_script_in_place_of_claude_with_the_skip_flag_and_the_hooks()
    {
        const string Hooks = " --settings \"{SD_CLAUDE_SETTINGS}\"";
        Assert.Equal($"& 'C:\\Users\\Some One\\.local\\bin\\claude-swift.cmd' --session-id {Id} --dangerously-skip-permissions" + Hooks, HostManager.WrapperCommand(Swift, Id, resume: false, chat: false));
        Assert.Equal($"& 'C:\\Users\\Some One\\.local\\bin\\claude-swift.cmd' --resume {Id} --dangerously-skip-permissions" + Hooks, HostManager.WrapperCommand(Swift, Id, resume: true, chat: false));
        Assert.Equal("& 'C:\\it''s here\\claude-swift.cmd'", new ClaudeWrapper("swift", @"C:\it's here\claude-swift.cmd", @"C:\x").ShellCommand);
    }

    [Fact]
    public void A_chat_runs_the_script_through_cmd_on_the_chat_protocol_and_names_no_model()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string fresh = HostManager.WrapperCommand(Swift, Id, resume: false, chat: true);
        Assert.Equal($"\"{cmd}\" /d /s /c \"\"C:\\Users\\Some One\\.local\\bin\\claude-swift.cmd\" {ChatStream.ProtocolFlags} --session-id {Id} --dangerously-skip-permissions\"", fresh);
        string back = HostManager.WrapperCommand(Swift, Id, resume: true, chat: true);
        Assert.Contains($" --resume {Id} --dangerously-skip-permissions\"", back);
        Assert.DoesNotContain("--model", fresh + back);
        Assert.DoesNotContain("--settings", fresh + back);

        // The command a chat moves to another conversation with has the id filled in later, and nothing after its last quote.
        string template = HostManager.WrapperCommand(Swift, ChatSessions.SessionPlaceholder, resume: true, chat: true);
        Assert.Equal(back, ChatSessions.Command(template, Id, ""));
    }

    [Theory]
    [InlineData("ship the thing", true)]
    [InlineData("v2.1 notes, part #3: go+stop @home", true)]
    [InlineData("naïve café", true)]
    [InlineData("a \" & calc & \"", false)]
    [InlineData("100% done", false)]
    [InlineData("a^b", false)]
    [InlineData("wow!", false)]
    [InlineData("a|b", false)]
    [InlineData("a<b>c", false)]
    [InlineData("back\\slash", false)]
    [InlineData("it's", false)]
    [InlineData("(x)", false)]
    [InlineData("", false)]
    public void A_name_goes_on_the_line_only_when_cmd_reads_it_as_written(string name, bool passed)
    {
        Assert.Equal(passed, HostManager.CmdSafe(name));
        foreach (bool chat in new[] { false, true })
        {
            string started = HostManager.WrapperCommand(Swift, Id, resume: false, chat, name);
            Assert.Equal(passed, started.Contains($" --name \"{name}\""));
            Assert.Equal(passed, started.Contains("--name"));
            Assert.DoesNotContain("--name", HostManager.WrapperCommand(Swift, Id, resume: true, chat, name));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an id & calc")]
    [InlineData("../elsewhere")]
    public void What_is_not_a_session_id_never_reaches_the_line(string id) =>
        Assert.Throws<ArgumentException>(() => HostManager.WrapperCommand(Swift, id, resume: true, chat: true));

    [Fact]
    public void A_wrappers_session_is_looked_for_in_the_wrappers_folder()
    {
        string config = Path.Combine(_dir, "cfg"), cwd = @"C:\Work\my repo";
        string project = Path.Combine(config, "projects", "C--Work-my-repo");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, Id + ".jsonl"), """{"type":"user","message":{"role":"user","content":"said"}}""" + "\n");

        Assert.True(SessionScanner.HasTranscript(Id, cwd, chat: false, config));
        Assert.True(SessionScanner.HasTranscript(Id, cwd, chat: true, config));
        // The same id under the CLI's own folder is another matter: there is no such conversation there.
        Assert.False(SessionScanner.HasTranscript(Id, cwd, chat: false));
        Assert.False(SessionScanner.HasTranscript(Id, cwd, chat: true, ClaudeProfiles.DefaultConfigDir));
        Assert.False(SessionScanner.HasTranscript("11111111-2222-4333-8444-555555555555", cwd, chat: true, config));
    }

    [Fact]
    public void A_start_request_for_swift_is_a_claude_session_of_the_swift_profile_or_nothing()
    {
        string script = Path.Combine(_dir, "claude-swift.cmd"), config = Path.Combine(_dir, "cfg");
        File.WriteAllText(script, $"set \"CLAUDE_CONFIG_DIR={config}\"\r\nclaude %*\r\n");
        ClaudeProfiles.SwiftScriptLocator = () => script;
        ClaudeProfiles.ForgetWrappers();
        var settings = new Settings { ResumeFlags = "--model opus --dangerously-skip-permissions" };

        var chat = SessionStarter.Plan(new StartRequest("Swift", _dir, Name: "typed & unsafe", Model: "opus", Chat: true), settings)!;
        Assert.Equal(("Claude", "swift", ChatStream.Kind, _dir), (chat.Provider, chat.Profile, chat.Kind, chat.Cwd));
        Assert.True(Guid.TryParse(chat.SessionId, out _));
        Assert.Equal(HostManager.WrapperCommand(new ClaudeWrapper("swift", script, config), chat.SessionId, resume: false, chat: true), chat.Command);
        Assert.DoesNotContain("opus", chat.Command);
        Assert.DoesNotContain("typed", chat.Command);

        var terminal = SessionStarter.Plan(new StartRequest("swift", _dir), settings)!;
        Assert.Equal(("Claude", "swift", ""), (terminal.Provider, terminal.Profile, terminal.Kind));
        Assert.StartsWith($"& '{script}' --session-id {terminal.SessionId} --dangerously-skip-permissions", terminal.Command);
        Assert.Contains("Swift", SessionStarter.Providers);

        ClaudeProfiles.SwiftScriptLocator = () => null;
        ClaudeProfiles.ForgetWrappers();
        Assert.Throws<InvalidOperationException>(() => SessionStarter.Plan(new StartRequest("Swift", _dir, Chat: true), settings));
    }
}
