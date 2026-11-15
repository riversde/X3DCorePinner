// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace X3DCorePinner.Core;

public sealed record FrameStats(
    int Frames,
    double AvgFps,
    double OnePercentLowFps,
    double AvgFrameMs,
    double? AvgGpuBusyMs,
    double? AvgCpuBusyMs,
    double? AvgLatencyMs,
    IReadOnlyList<(string Mode, double SharePct)> PresentModes,
    string Bottleneck,
    string PresentVerdict);

/// <summary>
/// Frame analysis with Intel PresentMon (MIT, github.com/GameTechDev/PresentMon).
/// The console build is downloaded on first use - it is not bundled.
/// </summary>
public static class PresentMonService
{
    /// <summary>
    /// SECURITY: PresentMon runs elevated, so it is stored in an admin-only folder
    /// (%ProgramData%\X3DCorePinner\Tools\PresentMon) and pinned by SHA-256 at download.
    /// A user-writable location would let any program swap the exe and get admin rights.
    /// </summary>
    public static string ToolDir => Path.Combine(SecureFolder.ProtectedRoot, "Tools", "PresentMon");

    private static string PinFile => Path.Combine(ToolDir, "presentmon.pin");

    /// <summary>Return the pinned exe if it is present and its SHA-256 still matches; otherwise null.</summary>
    public static string? FindExisting()
    {
        try
        {
            if (!File.Exists(PinFile)) return null;
            var parts = File.ReadAllText(PinFile).Trim().Split('|');
            if (parts.Length != 2) return null;
            string exe = Path.Combine(ToolDir, Path.GetFileName(parts[0]));
            if (!File.Exists(exe)) return null;
            if (!string.Equals(SecureFolder.Sha256Of(exe), parts[1], StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("PresentMon hash mismatch - the file changed since download. Deleting it; it will be downloaded again.");
                try { File.Delete(exe); File.Delete(PinFile); } catch { }
                return null;
            }
            return exe;
        }
        catch (Exception ex)
        {
            Log.Warn($"PresentMon check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Return the PresentMon console exe, downloading the latest release if needed.</summary>
    public static async Task<string> EnsureAsync(IProgress<string>? progress, CancellationToken ct)
    {
        // Re-lock the folder every time (fixes permissions if something loosened them).
        SecureFolder.EnsureAdminOnly(SecureFolder.ProtectedRoot);
        SecureFolder.EnsureAdminOnly(ToolDir);

        var existing = FindExisting();
        if (existing != null) return existing;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("X3DCorePinner", AppInfo.Version));

        progress?.Report("Looking up the latest PresentMon release on GitHub...");
        string json = await http.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases?per_page=15", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
            if (!rel.TryGetProperty("assets", out var assets)) continue;
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!Regex.IsMatch(name, @"^PresentMon-[\d\.]+-x64\.exe$", RegexOptions.IgnoreCase)) continue;
                string url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith("https://github.com/GameTechDev/", StringComparison.OrdinalIgnoreCase)) continue;

                progress?.Report($"Downloading {name}...");
                string dest = Path.Combine(ToolDir, name);
                string tmp = dest + ".part";
                await using (var src = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
                await using (var fs = File.Create(tmp))
                {
                    await src.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
                File.Move(tmp, dest, overwrite: true);
                string hash = SecureFolder.Sha256Of(dest);
                File.WriteAllText(PinFile, $"{name}|{hash}");
                Log.Ok($"PresentMon downloaded over HTTPS from github.com/GameTechDev: {dest} (SHA-256 {hash})");
                return dest;
            }
        }
        throw new InvalidOperationException("No PresentMon console (x64) build found in recent GitHub releases.");
    }

    /// <summary>Capture <paramref name="processExe"/> for N seconds into a CSV. Returns the CSV path.</summary>
    public static async Task<string> CaptureAsync(string presentMonExe, string processExe, int seconds, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AppInfo.ReportsDir);
        string csv = Path.Combine(AppInfo.ReportsDir, $"PresentMon_{Path.GetFileNameWithoutExtension(processExe)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        string[] argSets =
        {
            $"--process_name \"{processExe}\" --output_file \"{csv}\" --timed {seconds} --terminate_after_timed --stop_existing_session --no_console_stats",
            $"--process_name \"{processExe}\" --output_file \"{csv}\" --timed {seconds} --terminate_after_timed"
        };

        foreach (var args in argSets)
        {
            progress?.Report($"Capturing {processExe} for {seconds} s - go back to the game and play...");
            var result = await Task.Run(() => ProcessRunner.Run(presentMonExe, args, (seconds + 30) * 1000), ct).ConfigureAwait(false);
            if (File.Exists(csv)) return csv;
            Log.Warn($"PresentMon attempt produced no CSV (exit {result.ExitCode}): {Trim(result.Output, 300)}");
        }
        throw new InvalidOperationException("PresentMon produced no data. Is the game running and rendering? See the log for details.");
    }

    private static string Trim(string s, int max) => s.Length <= max ? s.Trim() : s[..max].Trim() + "...";

    // ---------------------------------------------------------------- analysis

    /// <summary>Parse a PresentMon CSV (v1 or v2 column names) into frame statistics.</summary>
    public static FrameStats Analyze(string csvPath)
    {
        using var reader = new StreamReader(csvPath);
        string? header = reader.ReadLine() ?? throw new InvalidDataException("Empty CSV.");
        var cols = SplitCsv(header);
        int Col(params string[] names)
        {
            foreach (var n in names)
            {
                int i = cols.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        int cFrame = Col("FrameTime", "MsBetweenPresents");
        int cGpu = Col("GPUBusy", "MsGPUBusy", "MsGPUActive");
        int cCpu = Col("CPUBusy", "MsCPUBusy");
        int cLat = Col("DisplayLatency", "MsUntilDisplayed");
        int cMode = Col("PresentMode");
        int cSwap = Col("SwapChainAddress");
        if (cFrame < 0) throw new InvalidDataException("CSV has no frame-time column.");

        var rows = new List<string[]>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            rows.Add(SplitCsv(line).ToArray());
        }

        // Keep the swap chain with the most frames (the game's main window).
        if (cSwap >= 0 && rows.Count > 0)
        {
            string main = rows.Where(r => r.Length > cSwap)
                              .GroupBy(r => r[cSwap])
                              .OrderByDescending(g => g.Count())
                              .First().Key;
            rows = rows.Where(r => r.Length > cSwap && r[cSwap] == main).ToList();
        }

        static double? Num(string[] r, int i) =>
            i >= 0 && i < r.Length && double.TryParse(r[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

        var ft = rows.Select(r => Num(r, cFrame)).Where(v => v is > 0).Select(v => v!.Value).ToList();
        if (ft.Count == 0) throw new InvalidDataException("No frames captured.");
        var gpu = rows.Select(r => Num(r, cGpu)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var cpu = rows.Select(r => Num(r, cCpu)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var lat = rows.Select(r => Num(r, cLat)).Where(v => v is > 0).Select(v => v!.Value).ToList();

        double avgFt = ft.Average();
        var sorted = ft.OrderBy(v => v).ToList();
        double p99 = sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(0.99 * sorted.Count))];

        var modes = cMode >= 0
            ? rows.Where(r => r.Length > cMode)
                  .GroupBy(r => r[cMode])
                  .OrderByDescending(g => g.Count())
                  .Select(g => (g.Key, Math.Round(100.0 * g.Count() / rows.Count, 1)))
                  .ToList()
            : new List<(string, double)>();

        double? avgGpu = gpu.Count > 0 ? gpu.Average() : null;
        string bottleneck = "Unknown (no GPU busy data)";
        if (avgGpu.HasValue)
        {
            double ratio = avgGpu.Value / avgFt;
            int pct = (int)Math.Round(ratio * 100);
            bottleneck = ratio >= 0.90 ? $"GPU-bound - GPU busy {pct}% of each frame. GPU settings / resolution / upscaler set the FPS."
                       : ratio < 0.80 ? $"CPU / engine-bound - GPU busy only {pct}% of each frame. CPU-side tuning sets the FPS."
                       : $"Balanced - GPU busy {pct}% of each frame.";
        }

        string verdict = modes.Count == 0 ? "Unknown"
            : modes[0].Item1.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase)
                ? "Good - frames go straight to the display (Independent Flip)."
                : modes[0].Item1.Contains("Composed", StringComparison.OrdinalIgnoreCase)
                    ? "Composed by DWM - an overlay, HDR/MPO setting or window state is forcing desktop composition (adds latency, costs FPS)."
                    : modes[0].Item1;

        return new FrameStats(
            ft.Count,
            Math.Round(1000.0 / avgFt, 1),
            Math.Round(1000.0 / p99, 1),
            Math.Round(avgFt, 2),
            avgGpu.HasValue ? Math.Round(avgGpu.Value, 2) : null,
            cpu.Count > 0 ? Math.Round(cpu.Average(), 2) : null,
            lat.Count > 0 ? Math.Round(lat.Average(), 2) : null,
            modes,
            bottleneck,
            verdict);
    }

    /// <summary>Minimal RFC-4180 splitter (handles quoted fields with commas).</summary>
    public static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
