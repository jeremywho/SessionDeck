using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionDeck;

/// <summary>
/// What the Antigravity CLI says about its own conversations, from the files it writes under
/// <c>~/.gemini/antigravity-cli</c>. Each <c>agy</c> process logs its pid and the conversation it
/// opened to <c>log/cli-&lt;stamp&gt;.log</c>; a <c>/rename</c> lands in <c>history.jsonl</c> as a
/// slash command on that conversation. The SQLite summaries hold the title too, but no SQLite
/// reader ships here and the two text files carry what the deck needs.
/// </summary>
internal static class AgyScanner
{
    static readonly Regex PidLine = new(@"Starting language server process with pid (\d+)", RegexOptions.Compiled);
    static readonly Regex ConversationLine = new(@"(?:Created conversation|found conversation) ([0-9a-f-]{36})", RegexOptions.Compiled);
    static readonly Regex RenameCommand = new(@"^/rename\s+(.+)$", RegexOptions.Compiled);

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    static DateTime _scannedAt = DateTime.MinValue;
    static Dictionary<int, string> _conversationByPid = new();
    static Dictionary<string, string> _nameByConversation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The conversation name for the agy process at <paramref name="pid"/>, or empty when unknown.</summary>
    public static string NameFor(int pid)
    {
        Refresh();
        return _conversationByPid.TryGetValue(pid, out var id) && _nameByConversation.TryGetValue(id, out var name) ? name : "";
    }

    public static string ConversationFor(int pid)
    {
        Refresh();
        return _conversationByPid.TryGetValue(pid, out var id) ? id : "";
    }

    static void Refresh()
    {
        if (DateTime.UtcNow - _scannedAt < TimeSpan.FromSeconds(5)) return;
        _scannedAt = DateTime.UtcNow;
        try
        {
            string root = Root;
            if (!Directory.Exists(root)) return;
            _conversationByPid = ConversationsByPid(Path.Combine(root, "log"), DateTime.UtcNow);
            _nameByConversation = NamesFromHistory(Path.Combine(root, "history.jsonl"));
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>Pid to conversation, from the per-process logs written in the last two days; the last conversation a log names wins.</summary>
    internal static Dictionary<int, string> ConversationsByPid(string logDir, DateTime nowUtc)
    {
        var map = new Dictionary<int, string>();
        if (!Directory.Exists(logDir)) return map;
        foreach (var f in Directory.GetFiles(logDir, "cli-*.log"))
        {
            if (nowUtc - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(2)) continue;
            int pid = 0; string conversation = "";
            foreach (var line in ReadLinesShared(f))
            {
                if (pid == 0 && PidLine.Match(line) is { Success: true } p) pid = int.Parse(p.Groups[1].Value);
                if (ConversationLine.Match(line) is { Success: true } c) conversation = c.Groups[1].Value;
            }
            if (pid > 0 && conversation.Length > 0) map[pid] = conversation;
        }
        return map;
    }

    /// <summary>Conversation to its latest <c>/rename</c>, from the command history.</summary>
    internal static Dictionary<string, string> NamesFromHistory(string historyPath)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(historyPath)) return names;
        foreach (var line in ReadLinesShared(historyPath))
        {
            if (!line.Contains("/rename", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                string display = r.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                string id = r.TryGetProperty("conversationId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                if (id.Length == 0) continue;
                if (RenameCommand.Match(display.Trim()) is { Success: true } m) names[id] = m.Groups[1].Value.Trim();
            }
            catch (JsonException) { }
        }
        return names;
    }

    static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line) yield return line;
    }

    /// <summary>The agy process under the host's shell, if any.</summary>
    public static int AgyPidUnder(int childPid, IReadOnlyDictionary<int, (int Parent, string Name)> table)
    {
        foreach (var (pid, (parent, name)) in table)
        {
            if (!string.Equals(name, "agy.exe", StringComparison.OrdinalIgnoreCase)) continue;
            int p = parent;
            for (int hops = 0; hops < 6 && p > 0; hops++)
            {
                if (p == childPid) return pid;
                p = table.TryGetValue(p, out var up) ? up.Parent : 0;
            }
        }
        return 0;
    }
}
