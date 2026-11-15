// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Text.Json;
using System.Text.Json.Serialization;

namespace X3DCorePinner.Core;

/// <summary>User configuration persisted as JSON (config.json).</summary>
public sealed class AppConfig
{
    /// <summary>Game executables. While any of these runs, protection is active.</summary>
    public List<string> Games { get; set; } = new();

    /// <summary>Background app executables moved to the Frequency CCD during a game.</summary>
    public List<string> BackgroundApps { get; set; } = new();

    public int IntervalSeconds { get; set; } = 5;
    public bool EngineEnabled { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool LowerPriority { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public int LogRetentionDays { get; set; } = 14;
    public int ConfigVersion { get; set; } = 1;

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppInfo.ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppConfig CreateDefault() => new()
    {
        Games = DefaultGames.ToList(),
        BackgroundApps = DefaultBackgroundApps.ToList()
    };

    /// <summary>Load config; creates defaults on first run or if the file is corrupt.</summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Config could not be read ({ex.Message}); using defaults. Old file kept as config.bad.json.");
            try { File.Copy(FilePath, Path.Combine(AppInfo.ConfigDir, "config.bad.json"), true); } catch { }
        }
        var def = CreateDefault();
        def.Save();
        return def;
    }

    /// <summary>Deep copy, so the engine thread never shares lists the UI is editing.</summary>
    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts) ?? CreateDefault();

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(AppInfo.ConfigDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Config save failed: {ex.Message}");
        }
    }

    /// <summary>Trim, lower-case, add ".exe", de-duplicate, clamp numbers.</summary>
    public void Normalize()
    {
        Games = NormalizeList(Games);
        BackgroundApps = NormalizeList(BackgroundApps)
            .Where(a => !Games.Contains(a, StringComparer.OrdinalIgnoreCase))
            .ToList();
        IntervalSeconds = Math.Clamp(IntervalSeconds, 2, 60);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);
    }

    public static string NormalizeExe(string name)
    {
        string n = (name ?? string.Empty).Trim().Trim('"');
        // Accept full paths ("C:\Games\cod.exe") - keep only the file name.
        n = n[(n.LastIndexOfAny(new[] { '\\', '/' }) + 1)..].Trim();
        if (n.Length == 0) return n;
        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
        return n.ToLowerInvariant();
    }

    private static List<string> NormalizeList(IEnumerable<string>? items) =>
        (items ?? Enumerable.Empty<string>())
            .Select(NormalizeExe)
            .Where(s => s.Length > 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ defaults

    public static readonly string[] DefaultGames =
    {
        "cod.exe",                              // Call of Duty / Warzone
        "cs2.exe",                              // Counter-Strike 2
        "valorant-win64-shipping.exe",          // VALORANT
        "fortniteclient-win64-shipping.exe",    // Fortnite
        "r5apex.exe", "r5apex_dx12.exe",        // Apex Legends
        "rainbowsix.exe", "rainbowsix_vulkan.exe",
        "overwatch.exe",
        "marvel-win64-shipping.exe",            // Marvel Rivals
        "tslgame.exe",                          // PUBG
        "destiny2.exe",
        "bf6.exe", "bf2042.exe",                // Battlefield
        "eldenring.exe",
        "cyberpunk2077.exe",
        "witcher3.exe",
        "eft.exe", "escapefromtarkov.exe",
        "dota2.exe",
        "league of legends.exe",
        "rocketleague.exe",
        "factorio.exe",
        "msfs2024.exe", "flightsimulator.exe"
    };

    public static readonly string[] DefaultBackgroundApps =
    {
        // Browsers / web runtimes
        "chrome.exe", "msedge.exe", "msedgewebview2.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
        // Chat / social / media
        "discord.exe", "spotify.exe", "teams.exe", "ms-teams.exe", "slack.exe", "whatsapp.exe", "telegram.exe",
        // Launchers and helpers
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "epicwebhelper.exe", "battle.net.exe",
        "upc.exe", "ubisoftextension.exe", "eadesktop.exe", "galaxyclient.exe", "galaxyclient helper.exe",
        // Monitoring / RGB / peripherals
        "hwinfo64.exe", "msiafterburner.exe", "rtss.exe", "nzxt cam.exe", "icue.exe", "lghub.exe", "lghub_agent.exe",
        "signalrgb.exe", "openrgb.exe", "openrgb-headless.exe", "l-connect-service.exe", "armourycrate.service.exe",
        // Streaming / capture / audio
        "streamdeck.exe", "elgato.wavelink.exe", "wavelink.exe", "camerahub.exe", "obs64.exe", "voicemeeter8x64.exe",
        // Cloud sync / misc
        "onedrive.exe", "googledrivefs.exe", "dropbox.exe", "icloudhome.exe", "icloudservices.exe",
        "nexus.exe", "nexus-overlay.exe", "qtwebengineprocess.exe", "wallpaper32.exe", "wallpaper64.exe",
        "claude.exe", "chatgpt.exe"
    };
}
