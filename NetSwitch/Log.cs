using System;
using System.IO;
using System.Text;

namespace NetSwitch
{
    /// <summary>Tiny append-only file logger with size-based rotation.</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 1024 * 1024; // 1 MB, then rotate to .1

        public static string LogPath => Path.Combine(AppPaths.DataDir, "netswitch.log");

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + " :: " + (ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message));
            if (ex != null) Write("ERROR", ex.ToString());
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var path = LogPath;
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        var rotated = path + ".1";
                        if (File.Exists(rotated)) File.Delete(rotated);
                        File.Move(path, rotated);
                    }
                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    internal static class AppPaths
    {
        public static string DataDir
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                return Path.Combine(local, "NetSwitch");
            }
        }

        public static string ExePath => System.Reflection.Assembly.GetExecutingAssembly().Location;
    }
}
