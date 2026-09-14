using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using WSLKeepAliveTray;
class LayoutProbe
{
    static StringBuilder report = new StringBuilder();
    static int failures;
    [STAThread] static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var supervisor = new WslAgentSupervisor("Ubuntu-24.04"))
        using (var form = new DashboardForm(supervisor))
        {
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            form.UpdateSnapshot(new TelemetrySnapshot { Kernel = "6.18.40.1-microsoft-standard-WSL2", CpuPercent = 23.6,
                MemoryUsedBytes = 3000000000, MemoryTotalBytes = 34000000000,
                NetworkReceiveBytesPerSecond = 640000, NetworkTransmitBytesPerSecond = 120000,
                DiskWriteBytesPerSecond = 256000, NetworkInterface = "eth1",
                SystemdState = "running", DockerActive = true, SshActive = true,
                WatchdogTimerActive = true, ContainersRunning = 5, ContainersTotal = 5 }, TrayHealthState.Healthy);
            Application.DoEvents();
            SeedCharts(form);
            Size normal = form.Size;
            foreach (Theme theme in ThemeManager.All)
            {
                ThemeManager.Select(theme.Id, false);
                form.Size = normal;
                Application.DoEvents();
                report.AppendLine(theme.Id + " DPI=" + form.CurrentAutoScaleDimensions + " client=" + form.ClientSize);
                Inspect(form);
                if (args.Length > 1)
                {
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(args[1], theme.Id + ".png"));
                    }
                }
                form.Size = form.MinimumSize;
                Application.DoEvents();
                report.AppendLine("MINIMUM client=" + form.ClientSize);
                Inspect(form);
            }
            string settings = Path.Combine(Path.GetDirectoryName(args[0]), "theme-probe", "theme.txt");
            foreach(Theme theme in ThemeManager.All)
            {
                ThemeManager.Save(settings, theme.Id);
                if(ThemeManager.Read(settings).Id != theme.Id) failures++;
            }
            File.WriteAllText(settings, "invalid-theme");
            if(ThemeManager.Read(settings).Id != "zijin") failures++;
            report.AppendLine("Theme persistence and invalid-setting fallback verified.");
            form.CloseForExit();
        }
        report.AppendLine("FAILURES=" + failures);
        File.WriteAllText(args[0], report.ToString());
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
    static void Inspect(Control parent)
    {
        foreach(Control c in parent.Controls)
        {
            if(c is ComboBox)
            {
                foreach(Control sibling in parent.Controls)
                    if(sibling is Label && sibling.Bounds.IntersectsWith(c.Bounds))
                    { report.AppendLine("FAIL overlapping theme picker: " + sibling.Text); failures++; }
            }
            if(c is Label || c is Button)
            {
                Size text = TextRenderer.MeasureText(c.Text, c.Font, Size.Empty, TextFormatFlags.SingleLine);
                bool fits = c.Top >= 0 && c.Bottom <= parent.ClientSize.Height && c.Height >= text.Height;
                if(c is Button) fits &= c.Width >= text.Width + 8;
                if(c is Label && !((Label)c).AutoEllipsis) fits &= c.Width >= text.Width;
                fits &= c.Right <= parent.ClientSize.Width;
                report.AppendLine((fits ? "PASS " : "FAIL ") + c.Text + " bounds=" + c.Bounds + " parent=" + parent.ClientSize + " text=" + text);
                if(!fits) failures++;
            }
            Inspect(c);
        }
    }
    static void SeedCharts(Control parent)
    {
        foreach(Control child in parent.Controls)
        {
            SparklineControl chart = child as SparklineControl;
            if(chart != null)
                for(int i=0; i<119; i++) chart.AddPoint(
                    chart.FixedMaximum > 0 ? (float)(23 + 10 * Math.Sin(i / 12.0)) : (float)(0.4 + 0.2 * Math.Sin(i / 7.0)),
                    chart.FixedMaximum > 0 ? (float)(12 + 6 * Math.Cos(i / 18.0)) : (float)(0.2 + 0.15 * Math.Cos(i / 5.0)));
            SeedCharts(child);
        }
    }
}
