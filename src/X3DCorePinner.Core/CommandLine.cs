// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Diagnostics;

namespace X3DCorePinner.Core;

/// <summary>
/// Headless command-line switches used by the installer / uninstaller and scripts.
/// They run without UI and without taking the single-instance lock.
///
///   --shutdown         ask a running instance to exit cleanly (it restores all pinned apps) and wait
///   --enable-startup   register the "Start with Windows" logon task
///   --disable-startup  remove the logon task
///   --restore-all      set every listed background app back to all CPU cores
///   --cleanup          uninstall: --shutdown, --disable-startup, --restore-all, delete %ProgramData% data
///
/// Exit codes: 0 = success, 1 = running instance did not exit in time, 2 = startup task failed, 3 = error.
/// </summary>
public static class CommandLine
{
    public const string MutexName = @"Global\PRFRAG.X3DCorePinner.SingleInstance";
    public const string ShowEventName = @"Global\PRFRAG.X3DCorePinner.Show";
    public const string ExitEventName = @"Global\PRFRAG.X3DCorePinner.Exit";

    public static readonly string[] Commands =
        { "--shutdown", "--enable-startup", "--disable-startup", "--restore-all", "--cleanup" };

    /// <summary>The headless command in <paramref name="args"/>, or null for a normal (UI) start.</summary>
    public static string? FindCommand(IEnumerable<string> args) =>
        args.Select(a => a.Trim().ToLowerInvariant()).FirstOrDefault(a => Commands.Contains(a));

    /// <summary>Run a headless command. Returns null if the args contain none (start the UI normally).</summary>
    public static int? TryRun(string[] args)
    {
        string? cmd = FindCommand(args);
        if (cmd == null) return null;

        Log.Directory = AppInfo.LogDir;
        Log.Info($"Command line: {cmd}");
        try
        {
            switch (cmd)
            {
                case "--shutdown":
                    return ShutdownRunningInstance(TimeSpan.FromSeconds(20)) ? 0 : 1;

                case "--enable-startup":
                    return StartupTask.Install(out _) ? 0 : 2;

                case "--disable-startup":
                    return StartupTask.Uninstall(out _) ? 0 : 2;

                case "--restore-all":
                    RestoreAll();
                    return 0;

                case "--cleanup":
                    bool stopped = ShutdownRunningInstance(TimeSpan.FromSeconds(20));
                    StartupTask.Uninstall(out _);
                    RestoreAll();
                    try
                    {
                        if (Directory.Exists(SecureFolder.ProtectedRoot))
                            Directory.Delete(SecureFolder.ProtectedRoot, recursive: true);
                    }
                    catch (Exception ex) { Log.Warn($"Could not delete {SecureFolder.ProtectedRoot}: {ex.Message}"); }
                    Log.Ok("Uninstall cleanup finished.");
                    return stopped ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{cmd} failed: {ex.Message}");
            return 3;
        }
        return 3;
    }

    private static void RestoreAll()
    {
        var topo = CpuTopology.Detect();
        if (!topo.IsDualCcdX3D) return;
        using var engine = new PinningEngine(topo, AppConfig.Load());
        engine.RestoreAllByName();
    }

    /// <summary>
    /// Signal the running instance to exit (it restores every pinned app on the way out)
    /// and wait until it has released its single-instance mutex.
    /// Returns true if no instance is running any more.
    /// </summary>
    public static bool ShutdownRunningInstance(TimeSpan timeout)
    {
        if (!IsRunning()) return true;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ExitEventName);
            ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Older build without the exit event - nothing to signal.
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsRunning())
            {
                Log.Ok("Running instance exited.");
                return true;
            }
            Thread.Sleep(250);
        }
        Log.Warn("Running instance did not exit in time.");
        return false;
    }

    public static bool IsRunning()
    {
        try
        {
            using var m = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // exists, owned by another security context
        }
    }
}
