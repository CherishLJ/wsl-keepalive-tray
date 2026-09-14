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
        private readonly SparklineControl cpuChart;
        private readonly SparklineControl memoryChart;
        private readonly SparklineControl networkChart;
        private readonly SparklineControl diskChart;
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

        public DashboardForm(WslAgentSupervisor agentSupervisor)
        {
            SuspendLayout();
            supervisor = agentSupervisor;
            Text = "深林印象 · WSL 运行监控";
            ClientSize = new Size(820, 700);
            MinimumSize = new Size(760, 650);
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

            TableLayoutPanel cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top;
            cards.Height = 112;
            cards.Padding = new Padding(16, 14, 16, 8);
            cards.ColumnCount = 4;
            cards.RowCount = 1;
            for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));

            cpuCard = new MetricCard("CPU", "--", "等待数据");
            memoryCard = new MetricCard("内存", "--", "等待数据");
            networkCard = new MetricCard("网络吞吐", "--", "等待数据");
            diskCard = new MetricCard("磁盘吞吐", "--", "等待数据");
            cards.Controls.Add(cpuCard, 0, 0);
            cards.Controls.Add(memoryCard, 1, 0);
            cards.Controls.Add(networkCard, 2, 0);
            cards.Controls.Add(diskCard, 3, 0);

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
            charts.Controls.Add(cpuChart, 0, 0);
            charts.Controls.Add(memoryChart, 1, 0);
            charts.Controls.Add(networkChart, 0, 1);
            charts.Controls.Add(diskChart, 1, 1);

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

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
            header.Dock = DockStyle.Fill;
            cards.Dock = DockStyle.Fill;
            charts.Dock = DockStyle.Fill;
            footer.Dock = DockStyle.Fill;
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(cards, 0, 1);
            dshPanel = new DshPanel(supervisor.Dsh);
            root.Controls.Add(dshPanel, 0, 2);
            root.Controls.Add(charts, 0, 3);
            root.Controls.Add(footer, 0, 4);
            Controls.Add(root);
            FormClosing += OnDashboardClosing;
            ResumeLayout(true);
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
            networkCard.ApplyTheme(theme); diskCard.ApplyTheme(theme);
            foreach (SparklineControl chart in new[] { cpuChart, memoryChart, networkChart, diskChart }) chart.ApplyTheme(theme);
            ApplyButtons(footer, theme);
            dshPanel.ApplyTheme(theme);
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

        public void ShowDashboard()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
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
                valueLabel.Size = new Size(180, 25);
                valueLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

                detailLabel = new Label();
                detailLabel.Text = detail;
                detailLabel.AutoEllipsis = true;
                detailLabel.ForeColor = Color.FromArgb(143, 160, 174);
                detailLabel.Location = new Point(15, 61);
                detailLabel.Size = new Size(178, 20);
                detailLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

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
