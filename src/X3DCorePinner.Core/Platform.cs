// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Runtime.Versioning;

// The whole library is Windows-only (affinity, PDH, registry, schtasks).
[assembly: SupportedOSPlatform("windows")]

namespace X3DCorePinner.Core;

/// <summary>Product-wide constants and well-known paths.</summary>
public static class AppInfo
{
    public const string ProductName = "X3D Core Pinner";
    public const string Author = "PR FRAG";
    public const string RepoUrl = "https://github.com/riversde/X3DCorePinner";
    public const string TaskName = "X3D Core Pinner";
    public const string DataFolderName = "X3DCorePinner";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Folder that holds the running executable.</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>Full path of the running executable (works for single-file publish).</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(ExeDir, "X3DCorePinner.exe");

    /// <summary>
    /// Portable mode: if a file named "portable.flag" sits next to the exe, config and
    /// logs are kept beside the exe. Otherwise %APPDATA% / %LOCALAPPDATA% are used.
    /// </summary>
    public static bool IsPortable => File.Exists(Path.Combine(ExeDir, "portable.flag"));

    public static string ConfigDir => IsPortable
        ? Path.Combine(ExeDir, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);

    public static string LocalDir => IsPortable
        ? Path.Combine(ExeDir, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);

    public static string LogDir => Path.Combine(LocalDir, "Logs");
    public static string ToolsDir => Path.Combine(LocalDir, "Tools");
    public static string ReportsDir => Path.Combine(LocalDir, "Reports");
}
