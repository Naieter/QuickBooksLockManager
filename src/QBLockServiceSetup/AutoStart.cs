using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace QBLockServiceSetup;

// Starts this program in the system tray when the user logs on.
//
// Why a scheduled task and not a Run-key entry: the program requires administrator rights, and
// Windows will not silently start an elevated program from a Run key at logon. A logon task with
// "highest privileges" does. It is built from XML because the quick command-line form can't set the
// options that matter here: schtasks' defaults kill the program after 72 hours, and refuse to start
// it on battery.
//
// (The lock SERVICE itself starts at boot regardless; this is only the management/tray icon.)
internal static class AutoStart
{
    public const string TaskName = "QBLockService Tray";
    public const string TrayArgument = "--tray";

    private const string PrefKey   = @"SOFTWARE\QBLockService";
    private const string PrefValue = "TrayAutoStart";

    // The copy of this program kept next to the service, so autostart keeps working even after the
    // downloaded file is deleted.
    public static string InstalledExe(string installDir) => Path.Combine(installDir, "QBLockService-Setup.exe");

    public static bool TaskExists(string? taskName = null) =>
        Run($"/query /tn \"{taskName ?? TaskName}\"").ExitCode == 0;

    public static (bool Ok, string Message) Enable(
        string exePath, string runLevel = "HighestAvailable", string? taskName = null)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"qbl-tray-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks wants the XML as UTF-16.
            File.WriteAllText(xmlPath, BuildTaskXml(exePath, WindowsIdentity.GetCurrent().Name, runLevel), Encoding.Unicode);
            var r = Run($"/create /tn \"{taskName ?? TaskName}\" /xml \"{xmlPath}\" /f");
            return (r.ExitCode == 0, r.Output);
        }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    public static (bool Ok, string Message) Disable(string? taskName = null)
    {
        if (!TaskExists(taskName)) return (true, "No autostart task to remove.");
        var r = Run($"/delete /tn \"{taskName ?? TaskName}\" /f");
        return (r.ExitCode == 0, r.Output);
    }

    // The admin's choice, remembered machine-wide so it survives reinstalls. On unless turned off.
    public static bool PreferenceOn()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(PrefKey);
            return k?.GetValue(PrefValue) is not int v || v != 0;
        }
        catch { return true; }
    }

    public static void SetPreference(bool on)
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(PrefKey);
            k.SetValue(PrefValue, on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { /* not elevated (tests): the choice just isn't remembered */ }
    }

    public static string BuildTaskXml(string exePath, string userId, string runLevel = "HighestAvailable")
    {
        var user = SecurityElement.Escape(userId);
        var exe  = SecurityElement.Escape(exePath);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Shows the QuickBooks Lock Service icon in the system tray when you log on. The lock service itself runs regardless.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>{runLevel}</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>{TrayArgument}</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static (int ExitCode, string Output) Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (p.ExitCode, output.Trim());
    }
}
