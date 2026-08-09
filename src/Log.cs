using System;
using System.IO;

namespace TodoWall
{
    /// <summary>Startup/attach breadcrumbs. Desktop hosting fails in ways that are
    /// invisible by definition, so it gets a log rather than a silent catch.</summary>
    internal static class Log
    {
        static readonly object Gate = new object();

        public static string File { get { return Path.Combine(Paths.Dir, "todowall.log"); } }

        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    string path = File;
                    // Keep it from growing forever.
                    if (System.IO.File.Exists(path) && new FileInfo(path).Length > 200 * 1024)
                        System.IO.File.WriteAllText(path, "");
                    System.IO.File.AppendAllText(path,
                        DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
