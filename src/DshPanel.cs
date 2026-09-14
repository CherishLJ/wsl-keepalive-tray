using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    public sealed class DshPanel : TableLayoutPanel
    {
        private readonly DshController controller;
        private readonly Label status;
        private readonly Label detail;
        private readonly Button start, stop, restart, refresh, web, board;
        private readonly Timer timer;
        private readonly ToolTip tip = new ToolTip();
        public DshPanel(DshController value)
        {
            controller = value;
            Dock = DockStyle.Fill; Margin = new Padding(22, 2, 22, 4);
            Padding = new Padding(12, 5, 12, 5);
            ColumnCount = 1; RowCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            start = Button("启动", delegate { controller.Act("start"); });
            stop = Button("停止", delegate { controller.Act("stop"); });
            restart = Button("重启 DSH", delegate { controller.Act("restart"); });
            refresh = Button("刷新", delegate { controller.Act("refresh"); });
            web = Button("打开网页", delegate { controller.OpenWeb(); });
            board = Button("任务看板", delegate { controller.OpenBoard(); });
            actions.Controls.AddRange(new Control[] { start, stop, restart, refresh, web, board });
            detail = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(10, 0, 0, 0) };
            var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 516));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            actionRow.Controls.Add(actions, 0, 0); actionRow.Controls.Add(detail, 1, 0);
            Controls.Add(status, 0, 0); Controls.Add(actionRow, 0, 1);
            controller.Changed += Changed;
            timer = new Timer { Interval = 2000 };
            timer.Tick += delegate { UpdateState(); };
            timer.Start();
            UpdateState();
        }
        private static Button Button(string text, Action action)
        {
            var button = new ActionButton { Text = text, Size = new Size(78, 30), FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 6, 0), Cursor = Cursors.Hand };
            button.Click += delegate { action(); };
            return button;
        }
        private void Changed(object sender, EventArgs args)
        {
            if(IsDisposed || !IsHandleCreated) return;
            if(InvokeRequired) { try { BeginInvoke(new Action(UpdateState)); } catch(InvalidOperationException) { } }
            else UpdateState();
        }
        private void UpdateState()
        {
            if(IsDisposed) return;
            status.Text = controller.Status + "  ·  自动更新";
            start.Enabled = controller.CanStart; stop.Enabled = controller.CanStop;
            restart.Enabled = controller.CanRestart; refresh.Enabled = controller.CanRefresh;
            web.Enabled = controller.Fresh && controller.Snapshot.DshWebReady && !controller.Busy;
            detail.Text = controller.Message;
            tip.SetToolTip(detail, controller.Message);
            TelemetrySnapshot snapshot = controller.Snapshot;
            tip.SetToolTip(status, controller.Fresh ? "systemd: " + snapshot.DshActiveState + "/" + snapshot.DshSubState +
                " · PID " + snapshot.DshMainPid + "\n" + snapshot.DshError : "尚无新鲜状态；WSL 连接中断时不会沿用旧状态。");
        }
        public void ApplyTheme(Theme theme)
        {
            BackColor = theme.Surface; status.ForeColor = theme.Ink; detail.ForeColor = theme.Muted;
            foreach(Button button in new[] { start, stop, restart, refresh, web, board })
            {
                button.BackColor = theme.Header; button.ForeColor = theme.Ink;
                button.FlatAppearance.BorderColor = theme.Border;
                button.FlatAppearance.MouseOverBackColor = theme.Surface;
            }
        }
        private sealed class ActionButton : Button
        {
            protected override void OnPaint(PaintEventArgs args)
            {
                if(Enabled) { base.OnPaint(args); return; }
                using(var brush = new SolidBrush(BackColor)) args.Graphics.FillRectangle(brush, ClientRectangle);
                using(var pen = new Pen(ThemeManager.Current.Border))
                    args.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width-1), Math.Max(0, Height-1));
                TextRenderer.DrawText(args.Graphics, Text, Font, ClientRectangle, ThemeManager.Current.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { controller.Changed -= Changed; timer.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
