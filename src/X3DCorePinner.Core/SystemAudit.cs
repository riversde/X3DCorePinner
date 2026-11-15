// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace X3DCorePinner.Core;

public enum AuditStatus { Ok, Info, Warn, Fail }

public sealed record AuditItem(string Name, AuditStatus Status, string Detail, string Fix = "");

/// <summary>
/// Checks everything that decides whether an X3D CPU and a borderless game perform well:
/// V-Cache driver, power plan, Game Mode, background capture, HAGS, MPO, windowed-game
/// optimizations and known overlay processes.
/// </summary>
public static class SystemAudit
{
    private static readonly string[] OverlayProcesses =
    {
        "RTSS", "RTSSHooksLoader64", "MSIAfterburner", "nexus-overlay", "gameoverlayui", "gameoverlayui64",
        "NVIDIA Overlay", "XboxGameBarWidgets", "Overwolf", "obs64", "Medal", "outplayed"
    };

    public static List<AuditItem> Run(CpuTopology topo)
    {
        var items = new List<AuditItem>();
        void Add(AuditItem i) => items.Add(i);

        // 1. Elevation
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Add(admin
            ? new AuditItem("Administrator rights", AuditStatus.Ok, "Running elevated - can change background app affinity.")
            : new AuditItem("Administrator rights", AuditStatus.Fail, "Not elevated - pinning will fail for most apps.", "Restart the app (it requests admin rights)."));

        // 1b. App location (security): an elevated app should not run from a user-writable folder
        Add(SecureFolder.AppLocationIsProtected(out string locWhy)
            ? new AuditItem("App location", AuditStatus.Ok, "Only administrators can modify the app folder.")
            : new AuditItem("App location", AuditStatus.Warn, locWhy,
                @"Move the X3DCorePinner folder to C:\Program Files\X3DCorePinner. Programs running without admin rights could otherwise replace files this elevated app loads. Start with Windows requires this."));

        // 2. CPU
        if (topo.DetectionError != null)
            Add(new AuditItem("CPU topology", AuditStatus.Fail, $"Could not read cache topology: {topo.DetectionError}"));
        else if (topo.IsDualCcdX3D)
            Add(new AuditItem("CPU topology", AuditStatus.Ok, topo.Summary));
        else if (topo.IsSingleCcdX3D)
            Add(new AuditItem("CPU topology", AuditStatus.Info, topo.Summary));
        else
            Add(new AuditItem("CPU topology", AuditStatus.Warn, topo.Summary, "Pinning only helps dual-CCD X3D CPUs (7950X3D, 7900X3D, 9950X3D, 9900X3D). Diagnostics still work."));

        // 3. V-Cache driver service
        var sc = ProcessRunner.Run(ProcessRunner.System32("sc.exe"), "query amd3dvcacheSvc");
        if (sc.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            Add(new AuditItem("AMD 3D V-Cache driver", AuditStatus.Ok, "amd3dvcacheSvc is running."));
        else if (sc.ExitCode == 1060 || sc.Output.Contains("1060"))
            Add(new AuditItem("AMD 3D V-Cache driver", topo.IsDualCcdX3D ? AuditStatus.Fail : AuditStatus.Info,
                "amd3dvcacheSvc is not installed.",
                topo.IsDualCcdX3D ? "Install the latest AMD chipset driver (full install) so games are steered to the V-Cache CCD." : ""));
        else
            Add(new AuditItem("AMD 3D V-Cache driver", AuditStatus.Warn, "amd3dvcacheSvc is installed but not running.", "Set the service to Automatic and start it, or reinstall the AMD chipset driver."));

        // 4. V-Cache driver preference
        object? defType = RegValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\amd3dvcache\Preferences", "DefaultType");
        if (defType is int dt)
            Add(new AuditItem("V-Cache driver default", AuditStatus.Info,
                $"DefaultType = {dt} (community-documented: 0 = prefer frequency CCD for non-games, 1 = prefer cache CCD). Detected games are still steered to the cache CCD."));

        // 5. Power plan
        var pc = ProcessRunner.Run(ProcessRunner.System32("powercfg.exe"), "/getactivescheme");
        string plan = pc.Output.Trim();
        if (plan.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase))
            Add(new AuditItem("Power plan", AuditStatus.Ok, "Balanced - required for the V-Cache driver to park the frequency CCD."));
        else
            Add(new AuditItem("Power plan", topo.IsDualCcdX3D ? AuditStatus.Warn : AuditStatus.Info, plan.Length > 0 ? plan : "Unknown",
                topo.IsDualCcdX3D ? "Switch to Balanced. High/Ultimate Performance stop AMD's driver from parking cores." : ""));

        // 6. Game Mode
        object? gm = RegValue(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        Add(gm is int g && g == 0
            ? new AuditItem("Game Mode", AuditStatus.Warn, "Off.", "Settings > Gaming > Game Mode > On (helps the V-Cache driver detect games).")
            : new AuditItem("Game Mode", AuditStatus.Ok, "On."));

        // 7. Background capture
        object? cap = RegValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        Add(cap is int c && c == 1
            ? new AuditItem("Game Bar background capture", AuditStatus.Warn, "Enabled - records while you play.", "Turn off background recording (or set AppCaptureEnabled = 0).")
            : new AuditItem("Game Bar background capture", AuditStatus.Ok, "Off."));

        // 8. HAGS
        object? hags = RegValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        Add(new AuditItem("Hardware-accelerated GPU scheduling", AuditStatus.Info,
            hags is int h ? (h == 2 ? "On." : "Off.") : "Driver default."));

        // 9. MPO
        object? mpo = RegValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");
        Add(mpo is int m && m == 5
            ? new AuditItem("Multiplane overlay (MPO)", AuditStatus.Warn, "Disabled (OverlayTestMode = 5).", "Borderless games cannot use hardware Independent Flip without MPO. Delete the OverlayTestMode value and reboot.")
            : new AuditItem("Multiplane overlay (MPO)", AuditStatus.Ok, "Enabled."));

        // 10. Windowed game optimizations
        string? dx = RegValue(Registry.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings") as string;
        if (dx != null && dx.Contains("SwapEffectUpgradeEnable=1"))
            Add(new AuditItem("Optimizations for windowed games", AuditStatus.Ok, "On."));
        else if (dx != null && dx.Contains("SwapEffectUpgradeEnable=0"))
            Add(new AuditItem("Optimizations for windowed games", AuditStatus.Warn, "Off.", "Settings > System > Display > Graphics > Advanced graphics settings > On."));
        else
            Add(new AuditItem("Optimizations for windowed games", AuditStatus.Info, "Windows default."));

        // 11. Overlays
        var running = OverlayProcesses
            .Where(n => { var p = Process.GetProcessesByName(n); bool any = p.Length > 0; foreach (var x in p) x.Dispose(); return any; })
            .ToList();
        Add(running.Count > 0
            ? new AuditItem("Overlay / capture apps", AuditStatus.Warn, "Running: " + string.Join(", ", running),
                "Overlays drawn on a borderless game can force DWM composition. Disable the in-game overlays you do not need.")
            : new AuditItem("Overlay / capture apps", AuditStatus.Ok, "None of the known overlays are running."));

        return items;
    }

    private static object? RegValue(RegistryKey root, string path, string name)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            return k?.GetValue(name);
        }
        catch { return null; }
    }
}
