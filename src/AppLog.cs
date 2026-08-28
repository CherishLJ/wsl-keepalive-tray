using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WSLKeepAliveTray
{
    internal static class AppLog
    {
        private static readonly object Sync = new object();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSLKeepAliveTray");

        public static readonly string FilePath = Path.Combine(DirectoryPath, "tray.log");

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    RotateIfNeeded();
                    File.AppendAllText(
                        FilePath,
                        string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}", DateTime.Now, message, Environment.NewLine),
                        new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length < 1024 * 1024)
            {
                return;
            }
            string previous = FilePath + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }
            File.Move(FilePath, previous);
        }

        public static void OpenDirectory()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                Process.Start("explorer.exe", "\"" + DirectoryPath + "\"");
            }
            catch (Exception ex)
            {
                Write("打开日志目录失败: " + ex.Message);
            }
        }
    }
}

