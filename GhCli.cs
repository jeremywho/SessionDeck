using System.Diagnostics;
using System.Text;

namespace SessionDeck;

internal sealed record GhResult(int ExitCode, string Stdout, string Stderr, bool NotFound = false, bool TimedOut = false);

internal static class GhCli
{
    public static Task<GhResult> RunAsync(IReadOnlyList<string> args, TimeSpan? timeout)
    {
        var psi = StartInfo();
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Run(psi, timeout);
    }

    public static Task<GhResult> RunAsync(string args, TimeSpan? timeout)
    {
        var psi = StartInfo();
        psi.Arguments = args;
        return Run(psi, timeout);
    }

    static ProcessStartInfo StartInfo() => new("gh")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    static async Task<GhResult> Run(ProcessStartInfo psi, TimeSpan? timeout)
    {
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return new GhResult(-1, "", "", NotFound: true); }
        if (p == null) return new GhResult(-1, "", "", NotFound: true);
        using (p)
        {
            // Both pipes drained concurrently: reading one to the end first deadlocks once gh fills the other's buffer.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new GhResult(-1, "", "", TimedOut: true);
            }
            return new GhResult(p.ExitCode, await stdout, await stderr);
        }
    }
}
