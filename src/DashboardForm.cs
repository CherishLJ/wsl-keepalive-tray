using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    public sealed class DashboardForm : Form
    {
        private readonly WslAgentSupervisor supervisor;
        private readonly Label statusBadge;
        private readonly Label subtitle;
        private readonly Label serviceLine;
        private readonly MetricCard cpuCard;
        private readonly MetricCard memoryCard;
        private readonly MetricCard networkCard;
        private readonly MetricCard diskCard;
        private readonly MetricCard gpuCard;
        private readonly SparklineControl cpuChart;
        private readonly SparklineControl memoryChart;
        private readonly SparklineControl networkChart;
        private readonly SparklineControl diskChart;
        private readonly SparklineControl gpuChart;
        private readonly Timer gpuTimer;
        private long paintedGpuSequence;
        private bool allowClose;
        private Icon currentIcon;
        private readonly Panel header;
        private readonly Panel footer;
        private readonly ComboBox themePicker;
        private readonly Label title;
        private bool updatingTheme;
        private TrayHealthState lastState;
        private readonly ToolTip detailsTip = new ToolTip();
        private readonly DshPanel dshPanel;
        private readonly DshWinPanel dshWinPanel;
        private readonly McpHubPanel mcpHubPanel;

        public DashboardForm(WslAgentSupervisor agentSupervisor)
        {
            SuspendLayout();
            supervisor = agentSupervisor;
            Text = "深林印象 · WSL 运行监控";
            // Provisional size only; Shown re-applies it once the window DPI is known.
            Size open = OpenSize(Screen.PrimaryScreen.WorkingArea, 1f);
            ClientSize = open;
            MinimumSize = open;
            Shown += delegate { ApplyOpenSize(); };
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(13, 21, 31);
            ForeColor = Color.FromArgb(233, 239, 244);
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            // All bounds below are authored in 96-DPI logical pixels. Without
            // this baseline fonts scale on high-DPI screens but controls do not.
            AutoScaleDimensions = new SizeF(96f, 96f);
            ShowInTaskbar = true;
            DoubleBuffered = true;

            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Size = new Size(820, 92);
            header.Padding = new Padding(22, 14, 22, 10);
            header.BackColor = Color.FromArgb(18, 28, 40);

            PictureBox brand = new PictureBox();
            brand.Image = BrandAssets.Logo;
            brand.SizeMode = PictureBoxSizeMode.Zoom;
            brand.BackColor = Color.FromArgb(255, 254, 251);
            brand.Padding = new Padding(4);
            brand.Location = new Point(22, 17);
            brand.Size = new Size(54, 58);
            brand.AccessibleName = "深林印象 Logo";
            header.Controls.Add(brand);
            title = new Label();
            title.Text = "深林印象 · WSL 运行监控";
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.AutoEllipsis = true;
            title.Size = new Size(470, 32);
            title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            title.Location = new Point(90, 15);

            subtitle = new Label();
            subtitle.Text = "正在连接遥测 agent…";
            subtitle.ForeColor = Color.FromArgb(151, 169, 184);
            subtitle.AutoEllipsis = true;
            subtitle.Size = new Size(470, 26);
            subtitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            subtitle.Location = new Point(91, 52);

            statusBadge = new Label();
            statusBadge.Text = "启动中";
            statusBadge.TextAlign = ContentAlignment.MiddleCenter;
            statusBadge.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            statusBadge.Size = new Size(92, 30);
            statusBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            statusBadge.Location = new Point(Math.Max(8, header.ClientSize.Width - statusBadge.Width - 24), 22);
            statusBadge.BackColor = Color.FromArgb(39, 91, 143);

            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(statusBadge);
            themePicker = new ComboBox();
            themePicker.DropDownStyle = ComboBoxStyle.DropDownList;
            themePicker.FlatStyle = FlatStyle.Flat;
            themePicker.DrawMode = DrawMode.OwnerDrawFixed;
            themePicker.DrawItem += delegate(object sender, DrawItemEventArgs args)
            {
                Theme theme = ThemeManager.Current;
                using (Brush brush = new SolidBrush(theme.Surface)) args.Graphics.FillRectangle(brush, args.Bounds);
                string text = args.Index >= 0 ? themePicker.Items[args.Index].ToString() : theme.Name;
                TextRenderer.DrawText(args.Graphics, text, themePicker.Font, args.Bounds, theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                args.DrawFocusRectangle();
            };
            themePicker.Width = 160;
            themePicker.Location = new Point(638, 53);
            themePicker.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            themePicker.AccessibleName = "切换主题";
            foreach (Theme theme in ThemeManager.All) themePicker.Items.Add(theme);
            themePicker.SelectedItem = ThemeManager.Current;
            themePicker.SelectedIndexChanged += delegate
            {
                if (!updatingTheme && themePicker.SelectedItem != null)
                    ThemeManager.Select(((Theme)themePicker.SelectedItem).Id, true);
            };
            header.Controls.Add(themePicker);

            Panel cards = new Panel();
            cards.Dock = DockStyle.Top;
            cards.Height = 104;
            cards.Padding = new Padding(16, 14, 16, 8);

            cpuCard = new MetricCard("CPU", "--", "等待数据");
            memoryCard = new MetricCard("内存", "--", "等待数据");
            networkCard = new MetricCard("网络吞吐", "--", "等待数据");
            diskCard = new MetricCard("磁盘吞吐", "--", "等待数据");
            gpuCard = new MetricCard("RTX 3060", "--", "等待数据");
            cards.Controls.Add(cpuCard);
            cards.Controls.Add(memoryCard);
            cards.Controls.Add(networkCard);
            cards.Controls.Add(diskCard);
            cards.Controls.Add(gpuCard);
            cards.Resize += delegate { FitMetricCards(cards); };

            TableLayoutPanel charts = new TableLayoutPanel();
            charts.Dock = DockStyle.Fill;
            charts.Padding = new Padding(16, 4, 16, 4);
            charts.ColumnCount = 2;
            charts.RowCount = 2;
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            cpuChart = CreateChart("CPU / 归一化负载", "%", Color.FromArgb(47, 211, 129), Color.FromArgb(255, 184, 77), 100);
            memoryChart = CreateChart("内存 / Swap", "%", Color.FromArgb(74, 163, 255), Color.FromArgb(167, 126, 255), 100);
            networkChart = CreateChart("网络 ↓ / ↑", " MB/s", Color.FromArgb(47, 211, 211), Color.FromArgb(74, 163, 255), 0);
            diskChart = CreateChart("磁盘读 / 写", " MB/s", Color.FromArgb(92, 224, 126), Color.FromArgb(255, 184, 77), 0);
            gpuChart = CreateChart("RTX 3060 占用 / 显存", "%", Color.FromArgb(118, 224, 92), Color.FromArgb(255, 138, 92), 100);
            charts.Controls.Add(cpuChart, 0, 0);
            charts.Controls.Add(memoryChart, 1, 0);
            charts.Controls.Add(networkChart, 0, 1);
            charts.Controls.Add(diskChart, 1, 1);
            gpuTimer = new Timer { Interval = 1000 };
            gpuTimer.Tick += delegate { RefreshGpu(); };
            gpuTimer.Start();

            footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 64;
            footer.Padding = new Padding(18, 11, 18, 10);
            footer.BackColor = Color.FromArgb(18, 28, 40);

            serviceLine = new Label();
            serviceLine.Text = "systemd -- · Docker -- · SSH -- · Watchdog --";
            serviceLine.ForeColor = Color.FromArgb(169, 185, 197);
            serviceLine.AutoSize = false;
            serviceLine.AutoEllipsis = true;
            serviceLine.Dock = DockStyle.Fill;
            serviceLine.TextAlign = ContentAlignment.MiddleLeft;

            Button health = CreateButton("立即检查", 94);
            health.Click += delegate { supervisor.RunHealthCheckAsync(); };
            Button terminal = CreateButton("打开终端", 94);
            terminal.Click += delegate { supervisor.OpenTerminal(); };
            Button restart = CreateButton("重启 WSL", 94);
            restart.Click += delegate { supervisor.RestartDistroAsync(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Dock = DockStyle.Right;
            buttons.Controls.Add(health);
            buttons.Controls.Add(terminal);
            buttons.Controls.Add(restart);

            footer.Controls.Add(serviceLine);
            footer.Controls.Add(buttons);

            header.Dock = DockStyle.Fill;
            cards.Dock = DockStyle.Fill;
            charts.Dock = DockStyle.Fill;
            footer.Dock = DockStyle.Fill;
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60f));
            dshPanel = new DshPanel(supervisor.Dsh);
            dshWinPanel = new DshWinPanel(supervisor.DshWin);
            mcpHubPanel = new McpHubPanel(supervisor.McpHub);
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(cards, 0, 1);
            root.Controls.Add(dshPanel, 0, 2);
            root.Controls.Add(dshWinPanel, 0, 3);
            root.Controls.Add(mcpHubPanel, 0, 4);
            root.Controls.Add(charts, 0, 5);
            root.Controls.Add(footer, 0, 6);
            Controls.Add(root);
            FormClosing += OnDashboardClosing;
            ResumeLayout(true);
            FitMetricCards(cards);
            header.Layout += delegate { LayoutHeader(); };
            LayoutHeader();
            ThemeManager.Changed += OnThemeChanged;
            ApplyTheme();
        }

        private void OnThemeChanged(object sender, EventArgs args) { ApplyTheme(); }

        private void LayoutHeader()
        {
            float scale = CurrentAutoScaleDimensions.Width / 96f;
            int right = header.ClientSize.Width - (int)(22 * scale);
            int pickerWidth = (int)(160 * scale);
            themePicker.SetBounds(right - pickerWidth, (int)(53 * scale), pickerWidth, themePicker.Height);
            statusBadge.SetBounds(right - (int)(92 * scale), (int)(14 * scale), (int)(92 * scale), (int)(30 * scale));
            int textWidth = Math.Max(20, themePicker.Left - (int)(110 * scale));
            title.SetBounds((int)(90 * scale), (int)(15 * scale), textWidth, (int)(32 * scale));
            subtitle.SetBounds((int)(91 * scale), (int)(52 * scale), textWidth, (int)(26 * scale));
        }
        private void ApplyTheme()
        {
            Theme theme = ThemeManager.Current;
            SuspendLayout();
            BackColor = theme.Background; ForeColor = theme.Ink;
            header.BackColor = footer.BackColor = theme.Header;
            title.ForeColor = theme.Ink;
            subtitle.ForeColor = serviceLine.ForeColor = theme.Muted;
            themePicker.BackColor = theme.Surface; themePicker.ForeColor = theme.Ink;
            themePicker.ItemHeight = themePicker.Font.Height + 4;
            updatingTheme = true;
            themePicker.SelectedItem = theme;
            updatingTheme = false;
            cpuCard.ApplyTheme(theme); memoryCard.ApplyTheme(theme);
            networkCard.ApplyTheme(theme); diskCard.ApplyTheme(theme); gpuCard.ApplyTheme(theme);
            foreach (SparklineControl chart in new[] { cpuChart, memoryChart, networkChart, diskChart, gpuChart }) chart.ApplyTheme(theme);
            ApplyButtons(footer, theme);
            dshPanel.ApplyTheme(theme);
            dshWinPanel.ApplyTheme(theme);
            mcpHubPanel.ApplyTheme(theme);
            ApplyBadge(lastState);
            ResumeLayout(true);
            Invalidate(true);
        }
        private static void ApplyButtons(Control parent, Theme theme)
        {
            foreach (Control control in parent.Controls)
            {
                Button button = control as Button;
                if (button != null)
                {
                    button.BackColor = theme.Surface; button.ForeColor = theme.Ink;
                    button.FlatAppearance.BorderColor = theme.Border;
                    button.FlatAppearance.MouseOverBackColor = theme.Header;
                }
                ApplyButtons(control, theme);
            }
        }

        private static void FitMetricCards(Panel cards)
        {
            int count = cards.Controls.Count;
            if (count == 0) return;
            int inner = Math.Max(0, cards.ClientSize.Width - cards.Padding.Horizontal);
            int each = Math.Max(48, inner / count);
            int x = cards.Padding.Left;
            int y = cards.Padding.Top;
            int height = Math.Max(40, cards.ClientSize.Height - cards.Padding.Vertical);
            foreach (Control card in cards.Controls)
            {
                card.Dock = DockStyle.None;
                card.SetBounds(x, y, each - 8, height);
                x += each;
            }
        }

        private static SparklineControl CreateChart(string title, string unit, Color first, Color second, float maximum)
        {
            SparklineControl chart = new SparklineControl();
            chart.Dock = DockStyle.Fill;
            chart.ChartTitle = title;
            chart.Unit = unit;
            chart.PrimaryColor = first;
            chart.SecondaryColor = second;
            chart.FixedMaximum = maximum;
            return chart;
        }

        private static Button CreateButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 34;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(65, 82, 99);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 59, 75);
            button.BackColor = Color.FromArgb(27, 41, 56);
            button.ForeColor = Color.FromArgb(232, 239, 244);
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(6, 0, 0, 0);
            return button;
        }

        public void UpdateSnapshot(TelemetrySnapshot snapshot, TrayHealthState state)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<TelemetrySnapshot, TrayHealthState>(UpdateSnapshot), snapshot, state);
                return;
            }

            cpuCard.SetValue(snapshot.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                string.Format(CultureInfo.InvariantCulture, "Load {0:0.00} / {1:0.00} / {2:0.00}", snapshot.Load1, snapshot.Load5, snapshot.Load15));
            memoryCard.SetValue(
                string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", snapshot.MemoryUsedBytes / 1073741824.0),
                string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB · Swap {1:0.0}%",
                    snapshot.MemoryTotalBytes / 1073741824.0,
                    snapshot.SwapPercent));
            networkCard.SetValue(
                "↓ " + TelemetrySnapshot.FormatRate(snapshot.NetworkReceiveBytesPerSecond),
                "↑ " + TelemetrySnapshot.FormatRate(snapshot.NetworkTransmitBytesPerSecond) + " · " + snapshot.NetworkInterface);
            diskCard.SetValue(
                "读 " + TelemetrySnapshot.FormatRate(snapshot.DiskReadBytesPerSecond),
                "写 " + TelemetrySnapshot.FormatRate(snapshot.DiskWriteBytesPerSecond) + " · 根盘 " + snapshot.RootPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%");

            float normalizedLoad = snapshot.ProcessorCount <= 0 ? 0 : (float)(snapshot.Load1 * 100.0 / snapshot.ProcessorCount);
            cpuChart.AddPoint((float)snapshot.CpuPercent, normalizedLoad);
            memoryChart.AddPoint((float)snapshot.MemoryPercent, (float)snapshot.SwapPercent);
            networkChart.AddPoint((float)(snapshot.NetworkReceiveBytesPerSecond / 1048576.0), (float)(snapshot.NetworkTransmitBytesPerSecond / 1048576.0));
            diskChart.AddPoint((float)(snapshot.DiskReadBytesPerSecond / 1048576.0), (float)(snapshot.DiskWriteBytesPerSecond / 1048576.0));
            RefreshGpu();

            subtitle.Text = supervisor.Distro + " · 已运行 " + TelemetrySnapshot.FormatDuration(snapshot.UptimeSeconds);
            subtitle.AccessibleDescription = snapshot.Kernel;
            detailsTip.SetToolTip(subtitle, supervisor.Distro + "\n" + snapshot.Kernel);
            serviceLine.Text = string.Format(
                CultureInfo.InvariantCulture,
                "systemd {0}  ·  Docker {2}/{3}  ·  SSH {4}  ·  自愈 {5}",
                string.IsNullOrEmpty(snapshot.SystemdState) ? "--" : snapshot.SystemdState,
                snapshot.DockerActive ? "正常" : "异常",
                snapshot.ContainersRunning,
                snapshot.ContainersTotal,
                snapshot.SshActive ? "✓" : "×",
                snapshot.WatchdogTimerActive ? "✓" : "×");
            SetState(state);
        }

        private void RefreshGpu()
        {
            if (IsDisposed || gpuCard == null) return;
            GpuSample gpu = supervisor.Gpu == null ? null : supervisor.Gpu.Latest;
            if (gpu == null || (!gpu.Available && string.IsNullOrEmpty(gpu.Error)))
            {
                gpuCard.SetValue("--", "等待数据");
                return;
            }
            if (!gpu.Available)
            {
                gpuCard.SetValue("不可用", string.IsNullOrEmpty(gpu.Error) ? "nvidia-smi 无数据" : gpu.Error);
                detailsTip.SetToolTip(gpuCard, gpu.Error);
                return;
            }
            gpuCard.SetValue(
                gpu.UtilizationPercent.ToString("0", CultureInfo.InvariantCulture) + "% · " +
                gpu.TemperatureC.ToString("0", CultureInfo.InvariantCulture) + "°C",
                string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0.0} GB · {2:0} W",
                    gpu.MemoryUsedMiB / 1024.0, gpu.MemoryTotalMiB / 1024.0, gpu.PowerWatts));
            if (gpu.Sequence != paintedGpuSequence)
            {
                gpuChart.AddPoint((float)gpu.UtilizationPercent, (float)gpu.MemoryPercent);
                paintedGpuSequence = gpu.Sequence;
            }
            detailsTip.SetToolTip(gpuCard, string.Format(CultureInfo.InvariantCulture,
                "{0}\n占用 {1:0}% · 显存带宽 {2:0}%\n显存 {3:0}/{4:0} MiB\n温度 {5:0}°C · 功耗 {6:0.0}/{7:0} W\n核心频率 {8:0} MHz",
                gpu.Name, gpu.UtilizationPercent, gpu.MemoryUtilizationPercent,
                gpu.MemoryUsedMiB, gpu.MemoryTotalMiB, gpu.TemperatureC, gpu.PowerWatts, gpu.PowerLimitWatts, gpu.SmClockMHz));
        }

        public void SetState(TrayHealthState state)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<TrayHealthState>(SetState), state);
                return;
            }
            lastState = state;
            ApplyBadge(state);
            if (currentIcon != null) currentIcon.Dispose();
            currentIcon = IconFactory.Create(state);
            Icon = currentIcon;
        }

        private void ApplyBadge(TrayHealthState state)
        {
            statusBadge.Text = StateText(state);
            statusBadge.ForeColor = ThemeManager.Current.Light ? Color.FromArgb(24, 59, 52) : Color.FromArgb(244, 247, 240);
            Color color = IconFactory.StateColor(state);
            Color background = ThemeManager.Current.Header;
            statusBadge.BackColor = Color.FromArgb((background.R * 3 + color.R) / 4,
                (background.G * 3 + color.G) / 4, (background.B * 3 + color.B) / 4);
        }

        private static string StateText(TrayHealthState state)
        {
            switch (state)
            {
                case TrayHealthState.Healthy: return "运行正常";
                case TrayHealthState.Warning: return "需要关注";
                case TrayHealthState.Stopped: return "已停止";
                case TrayHealthState.Error: return "发生错误";
                default: return "启动中";
            }
        }

        private static Size OpenSize(Rectangle work, float scale)
        {
            // Width: half the screen width, then 80 percent of that, but never
            // narrower than the metric row needs, or the card text elides.
            int width = (int)Math.Round(work.Width * 0.5 * 0.8);
            int floor = (int)Math.Round(560 * scale);
            width = Math.Max(floor, Math.Min(width, work.Width - 40));
            // Height: the layout needs about 1420 px once 200 percent scaling is
            // applied, which is close to the desktop height itself, so take the
            // working area. That keeps both chart rows visible.
            int height = Math.Max((int)Math.Round(360 * scale), work.Height - 48);
            return new Size(width, height);
        }

        private float WindowScale()
        {
            uint dpi = Handle == IntPtr.Zero ? 0 : GetDpiForWindow(Handle);
            if (dpi < 96) dpi = (uint)DeviceDpi;
            return dpi / 96f;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private void ApplyOpenSize()
        {
            // DeviceDpi can still report the pre-scaling 96 here, so ask the
            // window itself for the real scale.
            float scale = Math.Max(1f, WindowScale());
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size open = OpenSize(work, scale);
            MinimumSize = open;
            ClientSize = open;
            CenterToScreen();
        }

        public void ShowDashboard()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ApplyOpenSize();
            if (!Visible) Show();
            // An always-hidden ApplicationContext can leave the managed Visible
            // flag set before USER32 applies WS_VISIBLE. Make the native state
            // explicit so tray/menu activation is reliable on Windows 11.
            ShowWindow(Handle, 5);
            Activate();
            BringToFront();
            SetForegroundWindow(Handle);
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public void CloseForExit()
        {
            allowClose = true;
            Close();
        }

        private void OnDashboardClosing(object sender, FormClosingEventArgs args)
        {
            if (!allowClose)
            {
                args.Cancel = true;
                Hide();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeManager.Changed -= OnThemeChanged;
            if (disposing && gpuTimer != null) gpuTimer.Dispose();
            if (disposing) detailsTip.Dispose();
            if (disposing && currentIcon != null)
            {
                currentIcon.Dispose();
                currentIcon = null;
            }
            base.Dispose(disposing);
        }

        private sealed class MetricCard : Panel
        {
            private readonly Label titleLabel;
            private readonly Label valueLabel;
            private readonly Label detailLabel;

            public MetricCard(string title, string value, string detail)
            {
                Dock = DockStyle.Fill;
                MinimumSize = new Size(0, 0);
                Margin = new Padding(6, 0, 6, 0);
                Padding = new Padding(14, 10, 14, 8);
                BackColor = Color.FromArgb(24, 34, 47);

                titleLabel = new Label();
                titleLabel.Text = title;
                titleLabel.AutoSize = true;
                titleLabel.ForeColor = Color.FromArgb(148, 167, 181);
                titleLabel.Location = new Point(14, 9);

                valueLabel = new Label();
                valueLabel.Text = value;
                valueLabel.AutoEllipsis = true;
                valueLabel.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
                valueLabel.ForeColor = Color.FromArgb(237, 242, 246);
                valueLabel.Location = new Point(13, 31);
                valueLabel.Size = new Size(96, 25);
                valueLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top;

                detailLabel = new Label();
                detailLabel.Text = detail;
                detailLabel.AutoEllipsis = true;
                detailLabel.ForeColor = Color.FromArgb(143, 160, 174);
                detailLabel.Location = new Point(15, 61);
                detailLabel.Size = new Size(96, 20);
                detailLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top;
                Resize += delegate
                {
                    int width = Math.Max(48, ClientSize.Width - 28);
                    valueLabel.Width = width;
                    detailLabel.Width = width;
                };

                Controls.Add(titleLabel);
                Controls.Add(valueLabel);
                Controls.Add(detailLabel);
            }

            public void SetValue(string value, string detail)
            {
                valueLabel.Text = value;
                detailLabel.Text = detail;
            }
            public void ApplyTheme(Theme theme)
            {
                BackColor = theme.Surface;
                titleLabel.ForeColor = detailLabel.ForeColor = theme.Muted;
                valueLabel.ForeColor = theme.Accent;
            }
        }
    }
}
