using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WSLKeepAliveTray
{
    public sealed class OperationEventArgs : EventArgs
    {
        public string Name { get; private set; }
        public bool Success { get; private set; }
        public string Message { get; private set; }

        public OperationEventArgs(string name, bool success, string message)
        {
            Name = name;
            Success = success;
            Message = message ?? string.Empty;
        }
    }

    public sealed class WslAgentSupervisor : IDisposable
    {
        private readonly object sync = new object();
        private readonly string distro;
        private readonly string wslExe;
        private Process agentProcess;
        private Timer restartTimer;
        private bool desiredRunning;
        private bool disposing;
        private int restartAttempt;
        private TelemetrySnapshot latestSnapshot;

        public event EventHandler<TelemetryEventArgs> TelemetryReceived;
        public event EventHandler<AgentStateEventArgs> StateChanged;
        public event EventHandler<OperationEventArgs> OperationCompleted;

        public WslAgentSupervisor(string distroName)
        {
            distro = distroName;
            wslExe = ResolveWslExe();
            desiredRunning = true;
        }

        public string Distro
        {
            get { return distro; }
        }

        public string WslExe
        {
            get { return wslExe; }
        }

        public bool DesiredRunning
        {
            get { lock (sync) { return desiredRunning; } }
        }

        public TelemetrySnapshot LatestSnapshot
        {
            get { lock (sync) { return latestSnapshot; } }
        }

        public bool AgentRunning
        {
            get
            {
                lock (sync)
                {
                    return agentProcess != null && !agentProcess.HasExited;
                }
            }
        }

        public void Start()
        {
            lock (sync)
            {
                if (disposing)
                {
                    return;
                }
                desiredRunning = true;
            }
            StartAgentIfNeeded();
        }

        private void StartAgentIfNeeded()
        {
            lock (sync)
            {
                if (disposing || !desiredRunning || (agentProcess != null && !agentProcess.HasExited))
                {
                    return;
                }

                RaiseState(TrayHealthState.Starting, "正在启动 " + distro);
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = wslExe;
                    info.Arguments = "-d " + distro + " --exec /usr/local/sbin/wsl-tray-agent --interval 2";
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    info.WindowStyle = ProcessWindowStyle.Hidden;
                    info.RedirectStandardInput = true;
                    info.RedirectStandardOutput = true;
                    info.RedirectStandardError = true;
                    info.StandardOutputEncoding = new UTF8Encoding(false);
                    info.StandardErrorEncoding = new UTF8Encoding(false);

                    Process process = new Process();
                    process.StartInfo = info;
                    process.EnableRaisingEvents = true;
                    process.OutputDataReceived += OnOutputDataReceived;
                    process.ErrorDataReceived += OnErrorDataReceived;
                    process.Exited += OnAgentExited;
                    if (!process.Start())
                    {
                        throw new InvalidOperationException("wsl.exe could not be started.");
                    }
                    agentProcess = process;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    AppLog.Write("遥测 agent 已启动，pid=" + process.Id);
                }
                catch (Exception ex)
                {
                    AppLog.Write("启动遥测 agent 失败: " + ex);
                    RaiseState(TrayHealthState.Error, "启动失败: " + ex.Message);
                    ScheduleRestart();
                }
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }
            try
            {
                TelemetrySnapshot snapshot = TelemetrySnapshot.Parse(args.Data);
                lock (sync)
                {
                    latestSnapshot = snapshot;
                    restartAttempt = 0;
                }
                EventHandler<TelemetryEventArgs> handler = TelemetryReceived;
                if (handler != null)
                {
                    handler(this, new TelemetryEventArgs(snapshot));
                }
                RaiseState(snapshot.IsHealthy ? TrayHealthState.Healthy : TrayHealthState.Warning,
                    snapshot.IsHealthy ? "运行正常" : "服务状态需要关注");
            }
            catch (Exception ex)
            {
                AppLog.Write("遥测解析失败: " + ex.Message + " | " + args.Data);
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                AppLog.Write("agent stderr: " + args.Data);
            }
        }

        private void OnAgentExited(object sender, EventArgs args)
        {
            Process exited = sender as Process;
            int exitCode = -1;
            try { exitCode = exited == null ? -1 : exited.ExitCode; } catch { }
            AppLog.Write("遥测 agent 已退出，exit=" + exitCode);

            lock (sync)
            {
                if (ReferenceEquals(agentProcess, exited))
                {
                    agentProcess = null;
                }
                if (disposing || !desiredRunning)
                {
                    RaiseState(TrayHealthState.Stopped, "WSL 已停止");
                    return;
                }
            }
            RaiseState(TrayHealthState.Warning, "连接中断，准备恢复");
            ScheduleRestart();
        }

        private void ScheduleRestart()
        {
            lock (sync)
            {
                if (disposing || !desiredRunning)
                {
                    return;
                }
                restartAttempt++;
                int delaySeconds = Math.Min(30, Math.Max(2, restartAttempt * 2));
                if (restartTimer != null)
                {
                    restartTimer.Dispose();
                }
                restartTimer = new Timer(delegate { StartAgentIfNeeded(); }, null, delaySeconds * 1000, Timeout.Infinite);
            }
        }

        public void StartDistro()
        {
            lock (sync)
            {
                desiredRunning = true;
                restartAttempt = 0;
            }
            StartAgentIfNeeded();
        }

        public void StopDistroAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                StopDistroNow();
            });
        }

        public bool StopDistroNow()
        {
            lock (sync) { desiredRunning = false; }
            StopAgentProcess();
            CommandResult result = RunHidden(wslExe, "--terminate " + distro, 30000);
            RaiseOperation("停止 WSL", result.ExitCode == 0, result.Message);
            RaiseState(TrayHealthState.Stopped, "WSL 已停止");
            return result.ExitCode == 0;
        }

        public void RestartDistroAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                RaiseState(TrayHealthState.Starting, "正在重启 " + distro);
                lock (sync) { desiredRunning = false; }
                StopAgentProcess();
                CommandResult result = RunHidden(wslExe, "--terminate " + distro, 30000);
                Thread.Sleep(1200);
                lock (sync)
                {
                    desiredRunning = true;
                    restartAttempt = 0;
                }
                StartAgentIfNeeded();
                RaiseOperation("重启 WSL", result.ExitCode == 0, result.Message);
            });
        }

        public void RunHealthCheckAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                CommandResult result = RunHidden(
                    wslExe,
                    "-d " + distro + " -u root --exec systemctl start wsl-tray-watchdog.service",
                    120000);
                RaiseOperation("立即健康检查", result.ExitCode == 0, result.Message);
            });
        }

        public void OpenTerminal()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = wslExe;
                info.Arguments = BuildInteractiveWslArguments(distro);
                info.UseShellExecute = false;
                info.CreateNoWindow = false;
                if (Process.Start(info) == null)
                {
                    throw new InvalidOperationException("wsl.exe could not be started.");
                }
            }
            catch (Exception ex)
            {
                RaiseOperation("打开终端", false, ex.Message);
            }
        }

        internal static string BuildInteractiveWslArguments(string distroName)
        {
            return "-d " + distroName + " --cd ~";
        }

        private void StopAgentProcess()
        {
            Process process = null;
            lock (sync)
            {
                process = agentProcess;
                agentProcess = null;
                if (restartTimer != null)
                {
                    restartTimer.Dispose();
                    restartTimer = null;
                }
            }
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
            try { process.Dispose(); } catch { }
        }

        private void RaiseState(TrayHealthState state, string message)
        {
            EventHandler<AgentStateEventArgs> handler = StateChanged;
            if (handler != null)
            {
                handler(this, new AgentStateEventArgs(state, message));
            }
        }

        private void RaiseOperation(string name, bool success, string message)
        {
            AppLog.Write(name + ": " + (success ? "成功" : "失败") + " " + message);
            EventHandler<OperationEventArgs> handler = OperationCompleted;
            if (handler != null)
            {
                handler(this, new OperationEventArgs(name, success, message));
            }
        }

        private static string ResolveWslExe()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string candidate = Path.Combine(system, "wsl.exe");
            return File.Exists(candidate) ? candidate : "wsl.exe";
        }

        private sealed class CommandResult
        {
            public int ExitCode;
            public string Message;
        }

        private static CommandResult RunHidden(string fileName, string arguments, int timeoutMs)
        {
            CommandResult result = new CommandResult();
            result.ExitCode = -1;
            result.Message = string.Empty;
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = fileName;
                info.Arguments = arguments;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = new UTF8Encoding(false);
                info.StandardErrorEncoding = new UTF8Encoding(false);
                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        result.Message = "无法启动进程。";
                        return result;
                    }
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        result.Message = "操作超时。";
                        return result;
                    }
                    result.ExitCode = process.ExitCode;
                    result.Message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                }
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
            }
            return result;
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposing = true;
                desiredRunning = false;
            }
            StopAgentProcess();
        }
    }
}
