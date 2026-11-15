// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace X3DCorePinner.Core;

/// <summary>
/// "Start with Windows" via a Task Scheduler logon task with highest privileges.
/// A task (not the Run key) is used because the app needs admin rights to change
/// other processes' affinity, and a task starts elevated without a UAC prompt.
/// </summary>
public static class StartupTask
{
    private static string Schtasks => ProcessRunner.System32("schtasks.exe");

    public static bool IsInstalled() =>
        ProcessRunner.Run(Schtasks, $"/Query /TN \"{AppInfo.TaskName}\"").ExitCode == 0;

    /// <summary>Executable path the registered task launches, or null.</summary>
    public static string? RegisteredCommand()
    {
        var r = ProcessRunner.Run(Schtasks, $"/Query /TN \"{AppInfo.TaskName}\" /XML");
        if (r.ExitCode != 0) return null;
        var m = Regex.Match(r.Output, "<Command>\"?(?<c>[^<\"]+)\"?</Command>");
        return m.Success ? m.Groups["c"].Value : null;
    }

    /// <summary>True if the task exists but points at a different exe (app was moved).</summary>
    public static bool NeedsRepair()
    {
        var cmd = RegisteredCommand();
        return cmd != null && !string.Equals(Path.GetFullPath(cmd), Path.GetFullPath(AppInfo.ExePath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool Install(out string message)
    {
        // SECURITY: the task starts this exe elevated at every sign-in. If a non-admin
        // program could replace the exe, that would be a silent admin-rights bypass.
        if (!SecureFolder.AppLocationIsProtected(out string why))
        {
            message = why + "\n\nFor security, Start with Windows only works when X3D Core Pinner is in a folder " +
                      "that only administrators can change. Move the whole X3DCorePinner folder to " +
                      @"""C:\Program Files\X3DCorePinner"", run it from there and enable it again.";
            Log.Warn("Start with Windows refused: app folder is writable by non-admins.");
            return false;
        }

        string user = WindowsIdentity.GetCurrent().Name;
        string xml = BuildXml(user, AppInfo.ExePath);
        string tmp;
        try
        {
            // Admin-only folder: a non-elevated program cannot swap the XML before schtasks reads it.
            tmp = Path.Combine(SecureFolder.EnsureAdminOnly(SecureFolder.ProtectedRoot), $"task_{Guid.NewGuid():N}.xml");
        }
        catch (Exception ex)
        {
            message = $"Could not prepare a protected folder: {ex.Message}";
            Log.Error(message);
            return false;
        }
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var r = ProcessRunner.Run(Schtasks, $"/Create /TN \"{AppInfo.TaskName}\" /XML \"{tmp}\" /F");
            message = r.ExitCode == 0
                ? $"Start with Windows enabled for {user}."
                : $"schtasks failed ({r.ExitCode}): {r.Output.Trim()}";
            if (r.ExitCode == 0) Log.Ok(message); else Log.Error(message);
            return r.ExitCode == 0;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            Log.Error($"Start with Windows failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static bool Uninstall(out string message)
    {
        if (!IsInstalled()) { message = "Not installed."; return true; }
        var r = ProcessRunner.Run(Schtasks, $"/Delete /TN \"{AppInfo.TaskName}\" /F");
        message = r.ExitCode == 0 ? "Start with Windows disabled." : $"schtasks failed: {r.Output.Trim()}";
        if (r.ExitCode == 0) Log.Ok(message); else Log.Error(message);
        return r.ExitCode == 0;
    }

    public static string BuildXml(string user, string exePath)
    {
        string u = SecurityElement.Escape(user);
        string exe = SecurityElement.Escape(exePath);
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>{AppInfo.Author}</Author>
    <Description>{AppInfo.ProductName}: keeps the X3D V-Cache CCD free for games by moving background apps to the frequency CCD while a game runs.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{u}</UserId>
      <Delay>PT20S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{u}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{exe}""</Command>
      <Arguments>--tray</Arguments>
      <WorkingDirectory>{dir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
    }
}
