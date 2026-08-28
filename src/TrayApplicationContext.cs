using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    public sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly Form host;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly WslAgentSupervisor supervisor;
        private readonly DashboardForm dashboard;
        private readonly EventWaitHandle showEvent;
        private readonly EventWaitHandle exitEvent;
        private readonly EventWaitHandle exitAndStopEvent;
        private readonly ToolStripMenuItem stateItem;
        private readonly ToolStripMenuItem cpuItem;
        private readonly ToolStripMenuItem memoryItem;
        private readonly ToolStripMenuItem diskItem;
        private readonly ToolStripMenuItem networkItem;
        private readonly ToolStripMenuItem serviceItem;
        private readonly ToolStripMenuItem startItem;
        private readonly ToolStripMenuItem stopItem;
        private readonly ToolStripMenuItem autostartItem;
        private Icon currentIcon;
        private TrayHealthState currentState;
        private TelemetrySnapshot currentSnapshot;
        private bool exiting;

        public TrayApplicationContext(
            string distroName,
            bool showDashboard,
            EventWaitHandle showDashboardEvent,
            EventWaitHandle exitApplicationEvent,
            EventWaitHandle exitAndStopApplicationEvent)
        {
            currentState = TrayHealthState.Starting;
            showEvent = showDashboardEvent;
            exitEvent = exitApplicationEvent;
            exitAndStopEvent = exitAndStopApplicationEvent;
            host = new Form();
            host.ShowInTaskbar = false;
            host.FormBorderStyle = FormBorderStyle.None;
            host.Opacity = 0;
            host.Size = new Size(1, 1);
            host.Location = new Point(-32000, -32000);
            // Force creation of a UI-thread-owned handle. CreateControl() alone
            // does not create a handle for an invisible top-level Form, which
            // made InvokeRequired return false on the signal worker thread.
            IntPtr hostHandle = host.Handle;

            supervisor = new WslAgentSupervisor(distroName);
            dashboard = new DashboardForm(supervisor);
            // Telemetry can arrive before the dashboard is first shown. Ensure
            // its handle belongs to this UI thread so InvokeRequired remains
            // reliable and no worker thread can accidentally own the window.
            IntPtr dashboardHandle = dashboard.Handle;
            menu = new ContextMenuStrip();
            menu.BackColor = Color.FromArgb(24, 34, 47);
            menu.ForeColor = Color.FromArgb(230, 236, 241);
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.Padding = new Padding(4);
            menu.Opening += OnMenuOpening;

            stateItem = MetricItem(supervisor.Distro + " · 启动中");
            stateItem.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            cpuItem = MetricItem("CPU -- · Load --");
            memoryItem = MetricItem("内存 -- · Swap --");
            diskItem = MetricItem("磁盘 读 -- · 写 --");
            networkItem = MetricItem("网络 ↓ -- · ↑ --");
            serviceItem = MetricItem("Docker -- · SSH -- · Watchdog --");

            menu.Items.Add(stateItem);
            menu.Items.Add(cpuItem);
            menu.Items.Add(memoryItem);
            menu.Items.Add(diskItem);
            menu.Items.Add(networkItem);
            menu.Items.Add(serviceItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem dashboardItem = CommandItem("打开监控面板", delegate { dashboard.ShowDashboard(); });
            ToolStripMenuItem healthItem = CommandItem("立即健康检查", delegate { supervisor.RunHealthCheckAsync(); });
            ToolStripMenuItem terminalItem = CommandItem("打开 WSL 终端", delegate { supervisor.OpenTerminal(); });
            ToolStripMenuItem logsItem = CommandItem("打开日志目录", delegate { AppLog.OpenDirectory(); });
            menu.Items.Add(dashboardItem);
            menu.Items.Add(healthItem);
            menu.Items.Add(terminalItem);
            menu.Items.Add(logsItem);
            menu.Items.Add(new ToolStripSeparator());

            startItem = CommandItem("启动 WSL", delegate { supervisor.StartDistro(); });
            ToolStripMenuItem restartItem = CommandItem("重启 WSL", delegate { supervisor.RestartDistroAsync(); });
            stopItem = CommandItem("停止 WSL", delegate { supervisor.StopDistroAsync(); });
            menu.Items.Add(startItem);
            menu.Items.Add(restartItem);
            menu.Items.Add(stopItem);
            menu.Items.Add(new ToolStripSeparator());

            autostartItem = CommandItem("登录时自动启动", delegate
            {
                try
                {
                    AutostartManager.SetEnabled(!AutostartManager.IsEnabled(), supervisor.Distro);
                    autostartItem.Checked = AutostartManager.IsEnabled();
                }
                catch (Exception ex)
                {
                    ShowBalloon("开机启动设置失败", ex.Message, ToolTipIcon.Error);
                }
            });
            autostartItem.CheckOnClick = false;
            autostartItem.Checked = AutostartManager.IsEnabled();
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CommandItem("退出托盘（WSL 继续运行）", delegate { ExitApplication(false); }));
            menu.Items.Add(CommandItem("退出并停止 WSL", delegate { ExitApplication(true); }));

            tray = new NotifyIcon();
            currentIcon = IconFactory.Create(currentState);
            tray.Icon = currentIcon;
            tray.Text = "WSL 启动中 · 正在连接遥测";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { dashboard.ShowDashboard(); };

            supervisor.TelemetryReceived += OnTelemetryReceived;
            supervisor.StateChanged += OnStateChanged;
            supervisor.OperationCompleted += OnOperationCompleted;
            supervisor.Start();

            Thread signalThread = new Thread(ShowSignalLoop);
            signalThread.IsBackground = true;
            signalThread.Name = "WSL tray show signal";
            signalThread.Start();

            if (showDashboard)
            {
                host.BeginInvoke(new Action(delegate { dashboard.ShowDashboard(); }));
            }
            AppLog.Write("托盘程序已启动");
        }

        private static ToolStripMenuItem MetricItem(string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Enabled = false;
            item.ForeColor = Color.FromArgb(179, 193, 204);
            item.Padding = new Padding(6, 2, 10, 2);
            return item;
        }

        private static ToolStripMenuItem CommandItem(string text, Action action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Padding = new Padding(6, 3, 10, 3);
            item.Click += delegate { action(); };
            return item;
        }

        private void OnTelemetryReceived(object sender, TelemetryEventArgs args)
        {
            Ui(delegate
            {
                currentSnapshot = args.Snapshot;
                TrayHealthState state = args.Snapshot.IsHealthy ? TrayHealthState.Healthy : TrayHealthState.Warning;
                ApplyState(state);
                UpdateMetrics();
                dashboard.UpdateSnapshot(args.Snapshot, state);
            });
        }

        private void OnStateChanged(object sender, AgentStateEventArgs args)
        {
            Ui(delegate
            {
                ApplyState(args.State);
                if (args.State == TrayHealthState.Starting || args.State == TrayHealthState.Stopped || args.State == TrayHealthState.Error)
                {
                    dashboard.SetState(args.State);
                }
            });
        }

        private void OnOperationCompleted(object sender, OperationEventArgs args)
        {
            Ui(delegate
            {
                if (!args.Success || args.Name == "立即健康检查")
                {
                    ShowBalloon(args.Name + (args.Success ? "完成" : "失败"),
                        string.IsNullOrWhiteSpace(args.Message) ? (args.Success ? "操作成功。" : "操作失败。") : args.Message,
                        args.Success ? ToolTipIcon.Info : ToolTipIcon.Error);
                }
            });
        }

        private void ApplyState(TrayHealthState state)
        {
            if (currentState == state && currentIcon != null)
            {
                UpdateTooltip();
                return;
            }
            currentState = state;
            Icon next = IconFactory.Create(state);
            tray.Icon = next;
            Icon old = currentIcon;
            currentIcon = next;
            if (old != null) old.Dispose();
            UpdateTooltip();
        }

        private void UpdateTooltip()
        {
            string text;
            if (currentSnapshot == null)
            {
                text = "WSL " + StateText(currentState) + " · 等待遥测";
            }
            else
            {
                text = string.Format(
                    CultureInfo.InvariantCulture,
                    "WSL {0} | CPU {1:0}% | RAM {2:0.0}G | 网↓{3:0.0} ↑{4:0.0}M",
                    StateText(currentState),
                    currentSnapshot.CpuPercent,
                    currentSnapshot.MemoryUsedBytes / 1073741824.0,
                    currentSnapshot.NetworkReceiveBytesPerSecond / 1048576.0,
                    currentSnapshot.NetworkTransmitBytesPerSecond / 1048576.0);
            }
            if (text.Length > 63) text = text.Substring(0, 63);
            tray.Text = text;
        }

        private void OnMenuOpening(object sender, CancelEventArgs args)
        {
            UpdateMetrics();
            autostartItem.Checked = AutostartManager.IsEnabled();
            startItem.Enabled = !supervisor.DesiredRunning || !supervisor.AgentRunning;
            stopItem.Enabled = supervisor.DesiredRunning || supervisor.AgentRunning;
        }

        private void UpdateMetrics()
        {
            stateItem.Text = supervisor.Distro + " · " + StateText(currentState);
            if (currentSnapshot == null)
            {
                cpuItem.Text = "CPU -- · Load --";
                memoryItem.Text = "内存 -- · Swap --";
                diskItem.Text = "磁盘 读 -- · 写 --";
                networkItem.Text = "网络 ↓ -- · ↑ --";
                serviceItem.Text = "Docker -- · SSH -- · Watchdog --";
                return;
            }

            cpuItem.Text = string.Format(CultureInfo.InvariantCulture,
                "CPU {0:0.0}% · Load {1:0.00} / {2:0.00} / {3:0.00}",
                currentSnapshot.CpuPercent, currentSnapshot.Load1, currentSnapshot.Load5, currentSnapshot.Load15);
            memoryItem.Text = string.Format(CultureInfo.InvariantCulture,
                "内存 {0} / {1} ({2:0.0}%) · Swap {3:0.0}%",
                TelemetrySnapshot.FormatBytes(currentSnapshot.MemoryUsedBytes),
                TelemetrySnapshot.FormatBytes(currentSnapshot.MemoryTotalBytes),
                currentSnapshot.MemoryPercent,
                currentSnapshot.SwapPercent);
            diskItem.Text = "磁盘 读 " + TelemetrySnapshot.FormatRate(currentSnapshot.DiskReadBytesPerSecond) +
                " · 写 " + TelemetrySnapshot.FormatRate(currentSnapshot.DiskWriteBytesPerSecond) +
                " · 根盘 " + currentSnapshot.RootPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            networkItem.Text = "网络 ↓ " + TelemetrySnapshot.FormatRate(currentSnapshot.NetworkReceiveBytesPerSecond) +
                " · ↑ " + TelemetrySnapshot.FormatRate(currentSnapshot.NetworkTransmitBytesPerSecond) +
                " · " + currentSnapshot.NetworkInterface;
            serviceItem.Text = string.Format(CultureInfo.InvariantCulture,
                "Docker {0} ({1}/{2}) · SSH {3} · Watchdog {4}",
                currentSnapshot.DockerActive ? "正常" : "异常",
                currentSnapshot.ContainersRunning,
                currentSnapshot.ContainersTotal,
                currentSnapshot.SshActive ? "正常" : "异常",
                currentSnapshot.WatchdogTimerActive ? "正常" : "未启用");
        }

        private static string StateText(TrayHealthState state)
        {
            switch (state)
            {
                case TrayHealthState.Healthy: return "正常";
                case TrayHealthState.Warning: return "需关注";
                case TrayHealthState.Stopped: return "已停止";
                case TrayHealthState.Error: return "错误";
                default: return "启动中";
            }
        }

        private void ShowSignalLoop()
        {
            WaitHandle[] signals = { showEvent, exitEvent, exitAndStopEvent };
            while (!exiting)
            {
                try
                {
                    int selected = WaitHandle.WaitAny(signals, 500);
                    if (selected == 0)
                    {
                        AppLog.Write("收到显示仪表盘信号");
                        Ui(delegate
                        {
                            dashboard.ShowDashboard();
                            AppLog.Write("仪表盘显示调用完成，visible=" + dashboard.Visible);
                        });
                    }
                    else if (selected == 1)
                    {
                        Ui(delegate { ExitApplication(false); });
                    }
                    else if (selected == 2)
                    {
                        Ui(delegate { ExitApplication(true); });
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            tray.ShowBalloonTip(3500, title, text, icon);
        }

        private void Ui(Action action)
        {
            if (exiting || host.IsDisposed) return;
            try
            {
                if (host.InvokeRequired) host.BeginInvoke(action);
                else action();
            }
            catch (Exception ex)
            {
                AppLog.Write("UI 调度失败: " + ex);
            }
        }

        private void ExitApplication(bool stopWsl)
        {
            if (exiting) return;
            exiting = true;
            AppLog.Write("退出托盘，stopWsl=" + stopWsl);
            if (stopWsl)
            {
                supervisor.StopDistroNow();
            }
            supervisor.Dispose();
            tray.Visible = false;
            dashboard.CloseForExit();
            tray.Dispose();
            menu.Dispose();
            if (currentIcon != null) currentIcon.Dispose();
            host.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting)
            {
                ExitApplication(false);
            }
            base.Dispose(disposing);
        }
    }
}
