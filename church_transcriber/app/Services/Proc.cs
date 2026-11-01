using System.Diagnostics;
using System.Text;

namespace ChurchTranscriber.Services;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErrTail)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs external tools (yt-dlp, ffmpeg, python) with line streaming and cancellation.</summary>
public static class Proc
{
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, CancellationToken ct,
        Action<string>? onLine = null, bool captureStdout = true)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var errTail = new Queue<string>();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (captureStdout) lock (stdout) stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errTail) { errTail.Enqueue(e.Data); while (errTail.Count > 40) errTail.Dequeue(); }
            onLine?.Invoke(e.Data);
        };

        try { p.Start(); }
        catch (Exception ex) { return new ProcResult(-1, "", $"Could not start '{file}': {ex.Message}"); }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        p.WaitForExit(); // flush async readers
        string tail;
        lock (errTail) tail = string.Join('\n', errTail);
        return new ProcResult(p.ExitCode, stdout.ToString(), tail);
    }
}
