using System;
using Microsoft.Win32;

namespace TodoWall
{
    /// <summary>Run-at-login registration.</summary>
    internal static class StartupService
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "TodoWall";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled)
                    {
                        // Assembly.Location is empty for single-file publishes; ProcessPath is not.
                        string exe = Environment.ProcessPath;
                        if (string.IsNullOrEmpty(exe))
                            exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        if (string.IsNullOrEmpty(exe)) return;
                        k.SetValue(RunName, "\"" + exe + "\"", RegistryValueKind.String);
                    }
                    else if (k.GetValue(RunName) != null)
                    {
                        k.DeleteValue(RunName, false);
                    }
                }
            }
            catch { }
        }
    }
}
