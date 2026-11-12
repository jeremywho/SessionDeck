using System.Diagnostics;
using System.IO;
using Xunit;

namespace SessionDeck.Tests;

/// <summary>The chat pane's model is JavaScript; its tests run under Node (<c>node --test Tests/js/chat.test.js</c>) and this runs them with the rest.</summary>
public class ChatPageTests
{
    [Fact]
    public void The_chat_panes_model_passes_its_own_tests()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "wwwroot", "chat.js"))) root = Path.GetDirectoryName(root);
        Assert.True(root != null, "wwwroot/chat.js was not found above the test binaries");

        var psi = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        psi.ArgumentList.Add("--test");
        psi.ArgumentList.Add(Path.Combine(root!, "Tests", "js", "chat.test.js"));
        using var node = Process.Start(psi)!;
        var errors = node.StandardError.ReadToEndAsync();
        string output = node.StandardOutput.ReadToEnd();
        Assert.True(node.WaitForExit(120000), "node did not finish");
        Assert.True(node.ExitCode == 0, output + errors.Result);
        Assert.Contains("# fail 0", output);
        Assert.DoesNotContain("# tests 0", output);
    }
}
