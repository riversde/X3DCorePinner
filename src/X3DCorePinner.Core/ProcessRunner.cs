// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Diagnostics;
using System.Text;

namespace X3DCorePinner.Core;

/// <summary>Runs a console tool hidden and captures its output.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, bool TimedOut);

    public static Result Run(string fileName, string arguments, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new Result(-1, "Process could not be started.", false);
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new Result(-1, sb.ToString(), true);
            }
            p.WaitForExit();
            return new Result(p.ExitCode, sb.ToString(), false);
        }
        catch (Exception ex)
        {
            return new Result(-1, ex.Message, false);
        }
    }

    /// <summary>Absolute path of a tool in System32 (avoids PATH hijacking).</summary>
    public static string System32(string exe) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
}
