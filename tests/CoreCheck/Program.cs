using System.Xml.Linq;
using X3DCorePinner.Core;

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }

// 1. Config normalization
var cfg = new AppConfig { Games = new() { " COD ", "cod.exe", "\"C:\\Games\\cs2.exe\"" }, BackgroundApps = new() { "Chrome", "cod.exe", "discord.EXE", "" }, IntervalSeconds = 0 };
cfg.Normalize();
Check(cfg.Games.SequenceEqual(new[] { "cod.exe", "cs2.exe" }), "games normalized: " + string.Join(",", cfg.Games));
Check(cfg.BackgroundApps.SequenceEqual(new[] { "chrome.exe", "discord.exe" }), "apps normalized, game removed: " + string.Join(",", cfg.BackgroundApps));
Check(cfg.IntervalSeconds == 2, "interval clamped");
var def = AppConfig.CreateDefault(); def.Normalize();
Check(def.Games.Count > 10 && def.BackgroundApps.Count > 30, $"defaults: {def.Games.Count} games, {def.BackgroundApps.Count} apps");
Check(!def.BackgroundApps.Any(a => PinningEngine.ProtectedNames.Contains(Path.GetFileNameWithoutExtension(a))), "no protected names in default apps");

var clone = def.Clone(); clone.Games.Add("zzz.exe");
Check(!def.Games.Contains("zzz.exe") && clone.BackgroundApps.Count == def.BackgroundApps.Count, "Clone is a deep copy");

// 2. Task XML is well-formed and escapes
var xml = StartupTask.BuildXml("PC\\User & Co", @"C:\Tools\X3D <Pinner>\X3DCorePinner.exe");
try { var x = XDocument.Parse(xml); Check(x.Root!.Name.LocalName == "Task", "task XML parses"); }
catch (Exception ex) { Check(false, "task XML parse: " + ex.Message); }
Check(xml.Contains("&amp;") && xml.Contains("&lt;Pinner&gt;"), "task XML escapes user/path");

// 3. PresentMon v2 CSV analysis
string csv = Path.GetTempFileName();
var lines = new List<string> { "Application,ProcessID,SwapChainAddress,PresentRuntime,PresentMode,FrameTime,CPUBusy,GPUBusy,DisplayLatency" };
for (int i = 0; i < 990; i++) lines.Add($"cod.exe,100,0xAAA,DXGI,Hardware: Independent Flip,4.0,3.5,3.0,3.1");
for (int i = 0; i < 10; i++) lines.Add($"cod.exe,100,0xAAA,DXGI,Composed: Flip,10.0,9.0,4.0,NA");
for (int i = 0; i < 50; i++) lines.Add($"cod.exe,100,0xBBB,DXGI,Composed: Flip,50.0,1,1,1");
File.WriteAllLines(csv, lines);
var s = PresentMonService.Analyze(csv);
Check(s.Frames == 1000, $"main swapchain kept: {s.Frames} frames");
Check(Math.Abs(s.AvgFps - 1000.0 / 4.06) < 0.2, $"avg fps {s.AvgFps}");
Check(Math.Abs(s.OnePercentLowFps - 100.0) < 0.01, $"1% low {s.OnePercentLowFps}");
Check(s.PresentModes[0].Mode == "Hardware: Independent Flip" && s.PresentModes[0].SharePct == 99.0, $"present modes {s.PresentModes[0]}");
Check(s.Bottleneck.StartsWith("CPU"), "bottleneck: " + s.Bottleneck);
Check(s.AvgLatencyMs == 3.1, $"latency ignores NA: {s.AvgLatencyMs}");

// 4. PresentMon v1 CSV column names
lines = new() { "Application,ProcessID,SwapChainAddress,PresentMode,MsBetweenPresents,MsCPUBusy,MsGPUBusy,MsUntilDisplayed" };
for (int i = 0; i < 100; i++) lines.Add("cod.exe,1,0x1,Hardware: Independent Flip,5.0,4.0,4.8,3.0");
File.WriteAllLines(csv, lines);
s = PresentMonService.Analyze(csv);
Check(s.AvgFps == 200 && s.Bottleneck.StartsWith("GPU"), $"v1 columns: {s.AvgFps} fps, {s.Bottleneck}");

// 5. CSV splitter
var f = PresentMonService.SplitCsv("a,\"b,c\",\"d\"\"e\",");
Check(f.Count == 4 && f[1] == "b,c" && f[2] == "d\"e" && f[3] == "", "csv quoted fields");

// 6. Command-line switches
Check(CommandLine.FindCommand(Array.Empty<string>()) == null, "no args -> UI start");
Check(CommandLine.FindCommand(new[] { "--tray" }) == null, "--tray -> UI start");
Check(CommandLine.FindCommand(new[] { "--tray", " --SHUTDOWN " }) == "--shutdown", "--shutdown detected (case/space-insensitive)");
Check(CommandLine.FindCommand(new[] { "--cleanup" }) == "--cleanup", "--cleanup detected");

Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails;
