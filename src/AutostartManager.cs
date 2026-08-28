using System;
using Microsoft.Win32;

namespace WSLKeepAliveTray
{
    internal static class AutostartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WSLKeepAliveTray";

        public static bool IsEnabled()
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

        public static void SetEnabled(bool enabled, string distro)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Cannot open the current-user Run registry key.");
                }
                if (enabled)
                {
                    key.SetValue(ValueName,
                        "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --distro \"" + distro.Replace("\"", string.Empty) + "\"");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
