using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WSLKeepAliveTray
{
    /// <summary>
    /// Restarts the Windows DeepSeek Harness desktop client.
    ///
    /// The desktop client is not a WSL service: "restart dsh-win" used to only
    /// bounce the cookie proxy, which left the actual client untouched. This
    /// drives the same task-scheduler based worker the restart-desktop skill
    /// uses, so killing the client cannot kill the restart itself.
    /// </summary>
    internal static class DshWinDesktop
    {
        /// <summary>The skill script that owns the verified restart procedure.</summary>
        internal static string ScriptPath
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, @".agents\skills\restart-desktop\scripts\restart-dsh-desktop.ps1");
            }
        }

        internal static bool ScriptAvailable
        {
            get { try { return File.Exists(ScriptPath); } catch { return false; } }
        }

        /// <summary>
        /// Launch the restart worker and return as soon as it has been handed off.
        ///
        /// The script registers a Task Scheduler job and returns immediately; the
        /// client dies a few seconds later and comes back on its own. Waiting for
        /// the new client would block the UI thread for half a minute, so callers
        /// poll telemetry instead.
        /// </summary>
        internal static DshCommandResult Restart(int timeoutMs)
        {
            var result = new DshCommandResult();
            if (!ScriptAvailable)
            {
                result.Code = -1;
                result.Error = "找不到 restart-desktop 技能脚本：" + ScriptPath;
                return result;
            }
            var output = new StringBuilder();
            var error = new StringBuilder();
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
                    {
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + ScriptPath + "\" -Mode Restart -AcknowledgeSessionLoss",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                    { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                    { if (args.Data != null) lock (error) error.AppendLine(args.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        result.Code = -2;
                        result.Error = "重启脚本等待超时";
                        return result;
                    }
                    process.WaitForExit();
                    result.Code = process.ExitCode;
                    lock (output) result.Output = output.ToString();
                    lock (error) result.Error = error.ToString();
                }
            }
            catch (Exception ex)
            {
                result.Code = -1;
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Poll until the desktop client answers again, or the budget runs out.
        /// A restart that "succeeded" while the client never came back is a
        /// failure the user needs to see.
        /// </summary>
        internal static bool WaitUntilClientUp(Func<bool> clientUp, int budgetMs, int intervalMs)
        {
            int waited = 0;
            while (waited < budgetMs)
            {
                Thread.Sleep(intervalMs);
                waited += intervalMs;
                try { if (clientUp()) return true; } catch { }
            }
            return false;
        }
    }
}
