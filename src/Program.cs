using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    internal static class Program
    {
        private const string MutexName = @"Local\WSLKeepAliveTray.SingleInstance";
        private const string ShowEventName = @"Local\WSLKeepAliveTray.ShowDashboard";
        private const string ExitEventName = @"Local\WSLKeepAliveTray.Exit";
        private const string ExitAndStopEventName = @"Local\WSLKeepAliveTray.ExitAndStop";

        [STAThread]
        private static void Main(string[] args)
        {
            if (HasArgument(args, "--self-test"))
            {
                Environment.ExitCode = RunSelfTest(GetValueAfter(args, "--self-test"));
                return;
            }
            if (HasArgument(args, "--quit"))
            {
                SignalEvent(ExitEventName);
                return;
            }
            if (HasArgument(args, "--quit-and-stop"))
            {
                SignalEvent(ExitAndStopEventName);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                AppLog.Write("UI 未处理异常: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                AppLog.Write("未处理异常: " + e.ExceptionObject);
            };

            bool created;
            string distro = NormalizeDistro(GetOptionalValueAfter(args, "--distro"));
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    SignalExistingInstance();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (EventWaitHandle exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName))
                using (EventWaitHandle exitAndStopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitAndStopEventName))
                using (TrayApplicationContext context = new TrayApplicationContext(distro, HasArgument(args, "--show"), showEvent, exitEvent, exitAndStopEvent))
                {
                    Application.Run(context);
                }
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            foreach (string value in args)
            {
                if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetValueAfter(string[] args, string expected)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return Path.Combine(Path.GetTempPath(), "WSLKeepAliveTray-self-test.txt");
        }

        private static string GetOptionalValueAfter(string[] args, string expected)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return args[i + 1].Trim();
                }
            }
            return null;
        }

        private static string NormalizeDistro(string value)
        {
            string distro = string.IsNullOrWhiteSpace(value) ? "Ubuntu-24.04" : value.Trim();
            foreach (char character in distro)
            {
                if (!char.IsLetterOrDigit(character) && character != '.' && character != '-' &&
                    character != '_' && character != '+')
                {
                    throw new ArgumentException(
                        "The WSL distro name may contain only letters, digits, dot, dash, underscore, and plus.",
                        "value");
                }
            }
            return distro;
        }

        private static void SignalExistingInstance()
        {
            SignalEvent(ShowEventName);
        }

        private static void SignalEvent(string eventName)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (EventWaitHandle signal = EventWaitHandle.OpenExisting(eventName))
                    {
                        signal.Set();
                        return;
                    }
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
        }

        private static int RunSelfTest(string reportPath)
        {
            StringBuilder report = new StringBuilder();
            int failures = 0;
            try
            {
                string json = "{\"schemaVersion\":1,\"timestampUnixMs\":1,\"cpuPercent\":12.5," +
                    "\"memoryUsedBytes\":1073741824,\"memoryTotalBytes\":2147483648," +
                    "\"swapUsedBytes\":0,\"swapTotalBytes\":8589934592," +
                    "\"rootUsedBytes\":100,\"rootTotalBytes\":1000," +
                    "\"dockerActive\":true,\"sshActive\":true,\"watchdogTimerActive\":true," +
                    "\"containersRunning\":2,\"containersTotal\":2,\"containerNames\":[\"a\",\"b\"]}";
                TelemetrySnapshot snapshot = TelemetrySnapshot.Parse(json);
                failures += Check(report, snapshot.SchemaVersion == 1, "schema parsing");
                failures += Check(report, Math.Abs(snapshot.CpuPercent - 12.5) < 0.001, "numeric parsing");
                failures += Check(report, Math.Abs(snapshot.MemoryPercent - 50) < 0.001, "memory percentage");
                failures += Check(report, snapshot.IsHealthy, "healthy state");
                failures += Check(report, TelemetrySnapshot.FormatBytes(1073741824) == "1.00 GB", "byte formatting");
                failures += Check(report, TelemetrySnapshot.FormatDuration(90061).StartsWith("1天"), "duration formatting");
                using (System.Drawing.Icon icon = IconFactory.Create(TrayHealthState.Healthy))
                {
                    failures += Check(report, icon.Width > 0 && icon.Height > 0, "icon generation");
                }
                string terminalArguments = WslAgentSupervisor.BuildInteractiveWslArguments("Ubuntu-24.04");
                failures += Check(report,
                    terminalArguments == "-d Ubuntu-24.04 --cd ~",
                    "terminal preserves distro and home directory");
                failures += Check(report,
                    terminalArguments.IndexOf('"') < 0,
                    "terminal avoids quoted distro name");
                failures += Check(report,
                    terminalArguments.IndexOf("wt.exe", StringComparison.OrdinalIgnoreCase) < 0,
                    "terminal bypasses Windows Terminal forwarding");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine("FAIL unhandled: " + ex);
            }

            report.AppendLine(failures == 0 ? "SELF_TEST_PASS" : "SELF_TEST_FAIL count=" + failures);
            try
            {
                string directory = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
            return failures == 0 ? 0 : 1;
        }

        private static int Check(StringBuilder report, bool passed, string name)
        {
            report.AppendLine((passed ? "PASS " : "FAIL ") + name);
            return passed ? 0 : 1;
        }
    }
}
