// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Text;

namespace X3DCorePinner.Core;

public enum LogLevel { Info, Ok, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"[{Time:yyyy-MM-dd HH:mm:ss}] [{Level.ToString().ToUpperInvariant(),-5}] {Message}";
}

/// <summary>
/// Thread-safe daily file logger with retention and a live event for the UI.
/// Never throws: logging failures are swallowed so they cannot break the engine.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<LogEntry> Recent = new();
    private const int RecentCapacity = 2000;

    /// <summary>Raised on the calling thread for every entry (UI must marshal).</summary>
    public static event Action<LogEntry>? Written;

    public static string Directory { get; set; } = AppInfo.LogDir;

    public static string CurrentFile =>
        Path.Combine(Directory, $"X3DCorePinner_{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Ok(string msg) => Write(LogLevel.Ok, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    public static void Write(LogLevel level, string msg)
    {
        var entry = new LogEntry(DateTime.Now, level, msg);
        lock (Gate)
        {
            Recent.Add(entry);
            if (Recent.Count > RecentCapacity) Recent.RemoveRange(0, Recent.Count - RecentCapacity);
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentFile, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the caller.
            }
        }
        try { Written?.Invoke(entry); } catch { /* subscriber errors ignored */ }
    }

    /// <summary>Snapshot of the most recent entries (oldest first).</summary>
    public static IReadOnlyList<LogEntry> GetRecent()
    {
        lock (Gate) return Recent.ToArray();
    }

    /// <summary>Delete log files older than <paramref name="days"/> days.</summary>
    public static void Prune(int days)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, days));
            foreach (var f in System.IO.Directory.GetFiles(Directory, "X3DCorePinner_*.log"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
