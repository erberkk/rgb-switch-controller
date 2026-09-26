using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace PcControl.Core
{
    // The vendor apps run elevated, so PcControl must too. Like KANALI, it registers
    // "run with highest privileges" scheduled tasks once (one UAC prompt) and afterwards
    // an ordinary launch just starts the task.
    public static class Installer
    {
        public const string GuiTask = "PcControl";
        public const string ToggleTask = "PcControl Toggle";

        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PcControl");

        public static string InstalledExe => Path.Combine(InstallDir, "PcControl.exe");

        public static bool IsElevated
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static bool TaskExists(string name) => Schtasks($"/query /tn \"{name}\"") == 0;

        public static bool RunTask(string name) => Schtasks($"/run /tn \"{name}\"") == 0;

        public static void RequestInstall()
        {
            var self = Process.GetCurrentProcess().MainModule.FileName;
            var start = new ProcessStartInfo(self, "--install") { UseShellExecute = true, Verb = "runas" };
            using (var p = Process.Start(start)) p.WaitForExit();
        }

        // Runs elevated.
        public static void Install()
        {
            var self = Process.GetCurrentProcess().MainModule.FileName;
            Directory.CreateDirectory(InstallDir);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, InstalledExe, overwrite: true);

            RemoveLegacyInstall();
            RegisterTask(GuiTask, "");
            RegisterTask(ToggleTask, "--toggle");
            CreateShortcut("PC Control", InstalledExe, "", "Işıkları tek tıkla aç/kapat");
        }

        // The app was first installed as "RGB Switch".
        static void RemoveLegacyInstall()
        {
            foreach (var p in Process.GetProcessesByName("RgbSwitch"))
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { }
            Schtasks("/delete /f /tn \"RgbSwitch\"");
            Schtasks("/delete /f /tn \"RgbSwitch Toggle\"");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var link = Path.Combine(desktop, "RGB Switch.lnk");
            if (File.Exists(link)) File.Delete(link);
            var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RgbSwitch");
            try { if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true); } catch (IOException) { }
        }

        static void RegisterTask(string name, string arguments)
        {
            var user = WindowsIdentity.GetCurrent().User.Value;
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.3"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>PC Control (yönetici olarak)</Description></RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{InstalledExe}</Command>
      <Arguments>{arguments}</Arguments>
      <WorkingDirectory>{InstallDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
            var file = Path.Combine(Path.GetTempPath(), $"pccontrol-{Guid.NewGuid():N}.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                if (Schtasks($"/create /f /tn \"{name}\" /xml \"{file}\"") != 0)
                    throw new InvalidOperationException($"'{name}' görevi oluşturulamadı");
            }
            finally { File.Delete(file); }
        }

        static void CreateShortcut(string name, string target, string arguments, string description)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            var link = shell.CreateShortcut(Path.Combine(desktop, name + ".lnk"));
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = description;
            link.IconLocation = target + ",0";
            link.Save();
        }

        static int Schtasks(string arguments)
        {
            var start = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(start))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}
