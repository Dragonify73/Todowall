using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace TodoWall
{
    /// <summary>
    /// Removing TodoWall again. There is no installer, so there is nothing to hand the
    /// job to: uninstalling means dropping the Run-key entry, optionally deleting the
    /// data folder, and getting rid of the program file.
    ///
    /// A running exe holds a lock on itself and cannot delete itself, so the last step
    /// is handed to a detached shell that waits for this process to exit first.
    /// </summary>
    internal static class Uninstaller
    {
        /// <summary>Set once removal is under way, so shutdown does not helpfully write
        /// board.json (and with it the data folder) straight back out again.</summary>
        public static bool Removing;

        public static string ExePath
        {
            get
            {
                // Assembly.Location is empty in a single-file publish; ProcessPath is not.
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                    exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return exe;
            }
        }

        public static void Prompt(Window owner)
        {
            string exe = ExePath;

            MessageBoxResult go = MessageBox.Show(owner,
                "Remove TodoWall from this PC?\n\n"
                + "•  it stops starting with Windows\n"
                + "•  the program file is deleted:\n"
                + "     " + (string.IsNullOrEmpty(exe) ? "(unknown location)" : exe) + "\n\n"
                + "Your tasks are dealt with separately, on the next screen.",
                "Uninstall TodoWall", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            if (go != MessageBoxResult.OK) return;

            MessageBoxResult data = MessageBox.Show(owner,
                "Delete your tasks and settings as well?\n\n"
                + Paths.Dir + "\n\n"
                + "Choose No to keep them — a future copy of TodoWall picks them up where you left off.",
                "Uninstall TodoWall", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (data == MessageBoxResult.Cancel) return;

            string leftBehind = Run(exe, data == MessageBoxResult.Yes);

            MessageBox.Show(owner,
                "TodoWall has been removed.\n\n" + leftBehind,
                "Uninstall TodoWall", MessageBoxButton.OK, MessageBoxImage.Information);

            Application.Current.Shutdown();
        }

        /// <summary>Does the work. Returns a note about anything deliberately left behind.</summary>
        static string Run(string exe, bool deleteData)
        {
            Removing = true;

            StartupService.SetEnabled(false);

            string note = "";
            if (deleteData)
            {
                // Named files only, then the folder itself but *non*-recursively - so it
                // goes only if nothing else lives there. %APPDATA%\TodoWall may still hold
                // wallpaper.png, which Windows itself points at as the desktop wallpaper:
                // deleting that would leave the user with a black desktop.
                string dir = Paths.Dir;
                Delete(Paths.Board);
                Delete(Paths.Config);
                Delete(Path.Combine(dir, "todowall.log"));
                Delete(Path.Combine(dir, "error.log"));
                try { Directory.Delete(dir); }
                catch
                {
                    note = "Kept: " + dir + "\n(it still holds files TodoWall did not put there — "
                         + "wallpaper.png, if Windows is using it as your wallpaper.)\n\n";
                }
            }
            else
            {
                note = "Kept: " + Paths.Dir + "\n(your tasks and settings.)\n\n";
            }

            if (!string.IsNullOrEmpty(exe) && ScheduleSelfDelete(exe))
                note += "The program file is deleted a moment after TodoWall closes.";
            else
                note += "Delete " + (string.IsNullOrEmpty(exe) ? "the TodoWall program file" : exe)
                      + " yourself to finish.";

            return note;
        }

        static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        /// <summary>Hand the exe to a detached shell that waits for us to exit first.
        /// The folder is removed too, but only by plain rmdir - which refuses a folder
        /// that still has anything else in it.</summary>
        static bool ScheduleSelfDelete(string exe)
        {
            try
            {
                string dir = Path.GetDirectoryName(exe);

                // ping is the delay that works with no console attached; timeout needs one.
                string command = "/c ping 127.0.0.1 -n 3 > nul"
                               + " & del /f /q \"" + exe + "\"";
                if (!string.IsNullOrEmpty(dir))
                    command += " & rmdir \"" + dir + "\"";

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", command);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Program.LogCrash(ex);
                return false;
            }
        }
    }
}
