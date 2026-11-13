using System.IO;
using SessionDeck.Host;

namespace SessionDeck;

/// <summary>A request to start a session, from the desktop dialog or from a phone.</summary>
internal sealed record StartRequest(string Provider, string Cwd, string Name = "", string Prompt = "", string Model = "", string Effort = "", bool Chat = false);

/// <summary>What gets launched for a start request: the one place both the deck and the gateway build it.</summary>
internal sealed record StartPlan(string SessionId, string Provider, string Command, string Cwd, string? Title, string Prompt, string Kind = "", HostManager.ChatRestore? Restore = null);

internal static class SessionStarter
{
    public static readonly string[] Providers = { "Claude", "Codex", "Agy", "Shell" };

    public static StartPlan? Plan(StartRequest r, Settings settings)
    {
        string cwd = string.IsNullOrWhiteSpace(r.Cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : r.Cwd.Trim();
        if (!Directory.Exists(cwd)) return null;
        string name = r.Name.Trim();
        switch (r.Provider.Trim().ToLowerInvariant())
        {
            case "claude":
            {
                string id = Guid.NewGuid().ToString();
                if (r.Chat)
                    return new StartPlan(id, "Claude", HostManager.NewClaudeChatCommand(id, name.Length > 0 ? name : null, settings.ResumeFlags, r.Model, r.Effort), cwd, name.Length > 0 ? name : null, r.Prompt, ChatStream.Kind);
                return new StartPlan(id, "Claude", HostManager.NewClaudeCommand(id, name.Length > 0 ? name : null, settings.ResumeFlags, r.Model, r.Effort), cwd, name.Length > 0 ? name : null, r.Prompt);
            }
            case "codex":
                // A chat has no thread until its app server starts one; the model and effort picked go with the first turn.
                if (r.Chat)
                    return new StartPlan("", "Codex", HostManager.CodexChatCommand(), cwd, null, r.Prompt, ChatStream.Kind, new HostManager.ChatRestore(HostManager.CodexChatMode(settings.CodexFlags), r.Effort.Trim(), r.Model.Trim()));
                return new StartPlan("", "Codex", HostManager.NewCodexCommand(settings.CodexFlags, r.Model, r.Effort), cwd, null, r.Prompt);
            case "agy":
                return new StartPlan("", "Agy", HostManager.NewAgyCommand(HostManager.AgyDefaultFlags), cwd, "AGY", r.Prompt);
            case "shell":
            case "terminal":
                return new StartPlan("", "Shell", "", cwd, "Terminal", "");
            default:
                return null;
        }
    }

    public static HostRecord Start(StartPlan plan) =>
        HostManager.Spawn(plan.SessionId, plan.Provider, plan.Command, plan.Cwd, plan.Title, plan.Prompt, kind: plan.Kind, restore: plan.Restore);

    /// <summary>Folders a phone can pick from: the deck's recent list, home first.</summary>
    public static List<string> Folders(Settings settings)
    {
        var list = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var f in settings.RecentFolders)
            if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        return list;
    }
}
