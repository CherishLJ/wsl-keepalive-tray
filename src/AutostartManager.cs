using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WSLKeepAliveTray
{
    internal static class AutostartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WSLKeepAliveTray";
        internal const string TaskName = "WSLKeepAliveTray-Startup";

        public static bool IsEnabled()
        {
            try
            {
                return IsScheduledTaskEnabled() || IsLegacyRunEnabled();
            }
            catch
            {
                return IsLegacyRunEnabled();
            }
        }

        public static void SetEnabled(bool enabled, string distro)
        {
            if (enabled)
            {
                RegisterScheduledTask(distro);
                RemoveLegacyRunValue();
            }
            else
            {
                UnregisterScheduledTask();
                RemoveLegacyRunValue();
            }
        }

        internal static string BuildRegistrationScript(
            string executablePath,
            string distro,
            string userName)
        {
            string executableDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            string actionArguments = "--distro " + distro.Replace("\"", string.Empty);
            return "$ErrorActionPreference='Stop'; " +
                "$action=New-ScheduledTaskAction -Execute '" + PowerShellLiteral(executablePath) +
                "' -Argument '" + PowerShellLiteral(actionArguments) +
                "' -WorkingDirectory '" + PowerShellLiteral(executableDirectory) + "'; " +
                "$trigger=New-ScheduledTaskTrigger -AtLogOn -User '" + PowerShellLiteral(userName) + "'; " +
                "$principal=New-ScheduledTaskPrincipal -UserId '" + PowerShellLiteral(userName) +
                "' -LogonType Interactive -RunLevel Limited; " +
                "$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries " +
                "-DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew " +
                "-RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1)) " +
                "-ExecutionTimeLimit ([TimeSpan]::Zero); " +
                "Register-ScheduledTask -TaskName '" + TaskName +
                "' -Action $action -Trigger $trigger -Principal $principal -Settings $settings " +
                "-Description 'Starts the console-free WSL KeepAlive Tray at user logon.' " +
                "-Force | Out-Null";
        }

        private static void RegisterScheduledTask(string distro)
        {
            string userName;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                userName = identity.Name;
            }
            RunPowerShell(BuildRegistrationScript(
                System.Windows.Forms.Application.ExecutablePath,
                distro,
                userName));
        }

        private static void UnregisterScheduledTask()
        {
            RunPowerShell(
                "$ErrorActionPreference='Stop'; " +
                "$task=Get-ScheduledTask -TaskName '" + TaskName + "' -ErrorAction SilentlyContinue; " +
                "if ($null -ne $task) { $task | Unregister-ScheduledTask -Confirm:$false }");
        }

        private static bool IsScheduledTaskEnabled()
        {
            string schtasksPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "schtasks.exe");
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = schtasksPath,
                Arguments = "/Query /TN " + TaskName + " /XML",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process process = Process.Start(info))
            {
                if (process == null)
                {
                    return false;
                }
                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000) || process.ExitCode != 0)
                {
                    return false;
                }
                return IsTaskXmlEnabled(output);
            }
        }

        internal static bool IsTaskXmlEnabled(string taskXml)
        {
            // Task Scheduler omits <Enabled> when it uses the default value (true).
            return !string.IsNullOrWhiteSpace(taskXml) &&
                taskXml.IndexOf("<Task", StringComparison.OrdinalIgnoreCase) >= 0 &&
                taskXml.IndexOf("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsLegacyRunEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && !string.IsNullOrWhiteSpace(key.GetValue(ValueName) as string);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveLegacyRunValue()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Cannot open the current-user Run registry key.");
                }
                key.DeleteValue(ValueName, false);
            }
        }

        private static void RunPowerShell(string script)
        {
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process process = Process.Start(info))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start Windows PowerShell.");
                }
                string standardOutput = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    throw new TimeoutException("Timed out while updating the logon task.");
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "Failed to update the logon task. " + standardError + standardOutput);
                }
            }
        }

        private static string PowerShellLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }
}
