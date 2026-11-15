// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Diagnostics;

namespace X3DCorePinner.Core;

public sealed record ProcessCpuUsage(string Name, int Instances, double CpuPct, double Threads, bool IsGame, bool IsBackgroundTarget);

/// <summary>Top CPU consumers grouped by process name, measured over a short window.</summary>
public static class ProcessCpuSampler
{
    public static async Task<List<ProcessCpuUsage>> SampleAsync(AppConfig cfg, TimeSpan window, CancellationToken ct = default)
    {
        var first = Snapshot();
        var sw = Stopwatch.StartNew();
        await Task.Delay(window, ct).ConfigureAwait(false);
        var second = Snapshot();
        double elapsedMs = sw.Elapsed.TotalMilliseconds;
        int logical = Environment.ProcessorCount;

        var games = new HashSet<string>(cfg.Games.Select(g => Path.GetFileNameWithoutExtension(g)), StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(cfg.BackgroundApps.Select(a => Path.GetFileNameWithoutExtension(a)), StringComparer.OrdinalIgnoreCase);

        var perName = new Dictionary<string, (int count, double ms)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, s2) in second)
        {
            if (!first.TryGetValue(key, out var s1)) continue;
            double delta = (s2.Cpu - s1.Cpu).TotalMilliseconds;
            if (delta <= 0) continue;
            perName.TryGetValue(s2.Name, out var acc);
            perName[s2.Name] = (acc.count + 1, acc.ms + delta);
        }

        return perName
            .Where(kv => !kv.Key.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            .Select(kv =>
            {
                double pct = 100.0 * kv.Value.ms / (elapsedMs * logical);
                return new ProcessCpuUsage(kv.Key, kv.Value.count, Math.Round(pct, 2), Math.Round(pct * logical / 100.0, 2),
                    games.Contains(kv.Key), targets.Contains(kv.Key));
            })
            .OrderByDescending(u => u.CpuPct)
            .Take(30)
            .ToList();
    }

    private static Dictionary<string, (string Name, TimeSpan Cpu)> Snapshot()
    {
        var map = new Dictionary<string, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string key = $"{p.Id}:{p.StartTime.Ticks}";
                    map[key] = (p.ProcessName, p.TotalProcessorTime);
                }
                catch
                {
                    // Access denied for protected processes - skip.
                }
            }
        }
        return map;
    }
}
