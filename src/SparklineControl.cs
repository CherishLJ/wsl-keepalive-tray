using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    public sealed class SparklineControl : Control
    {
        private readonly List<float> primary = new List<float>();
        private readonly List<float> secondary = new List<float>();
        private const int Capacity = 120;

        public string ChartTitle { get; set; }
        public string Unit { get; set; }
        public Color PrimaryColor { get; set; }
        public Color SecondaryColor { get; set; }
        public float FixedMaximum { get; set; }
        private Color gridColor = Color.FromArgb(38, 55, 70);
        private Color borderColor = Color.FromArgb(49, 63, 79);
        public void ApplyTheme(Theme theme)
        {
            BackColor = theme.ChartBackground; ForeColor = theme.ChartInk;
            gridColor = theme.Grid; borderColor = theme.Border;
            PrimaryColor = theme.Primary; SecondaryColor = theme.Secondary;
            Invalidate();
        }

        public SparklineControl()
        {
            ChartTitle = string.Empty;
            Unit = string.Empty;
            PrimaryColor = Color.FromArgb(47, 211, 129);
            SecondaryColor = Color.FromArgb(74, 163, 255);
            FixedMaximum = 0;
            BackColor = Color.FromArgb(24, 34, 47);
            ForeColor = Color.FromArgb(228, 235, 241);
            DoubleBuffered = true;
            MinimumSize = new Size(220, 118);
            Margin = new Padding(6);
        }

        public void AddPoint(float first, float second)
        {
            primary.Add(Math.Max(0, first));
            secondary.Add(Math.Max(0, second));
            while (primary.Count > Capacity) primary.RemoveAt(0);
            while (secondary.Count > Capacity) secondary.RemoveAt(0);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = ClientRectangle;
            float dpiScale = graphics.DpiY / 96f;

            using (SolidBrush background = new SolidBrush(BackColor))
            using (Pen border = new Pen(borderColor))
            {
                graphics.FillRectangle(background, bounds);
                graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
            }

            using (Font titleFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (SolidBrush titleBrush = new SolidBrush(ForeColor))
            {
                graphics.DrawString(ChartTitle, titleFont, titleBrush, 12 * dpiScale, 9 * dpiScale);
            }

            string value = primary.Count == 0 ? "--" : primary[primary.Count - 1].ToString(primary[primary.Count - 1] >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + Unit;
            using (Font valueFont = new Font("Segoe UI", 9f, FontStyle.Regular))
            using (SolidBrush valueBrush = new SolidBrush(ForeColor))
            {
                SizeF size = graphics.MeasureString(value, valueFont);
                graphics.DrawString(value, valueFont, valueBrush, bounds.Width - size.Width - 10 * dpiScale, 9 * dpiScale);
            }

            RectangleF plot = new RectangleF(10 * dpiScale, 36 * dpiScale,
                Math.Max(10, bounds.Width - 20 * dpiScale), Math.Max(10, bounds.Height - 47 * dpiScale));
            using (Pen grid = new Pen(gridColor))
            {
                for (int i = 1; i <= 3; i++)
                {
                    float y = plot.Top + plot.Height * i / 4f;
                    graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
            }

            float maximum = FixedMaximum;
            if (maximum <= 0)
            {
                maximum = 1;
                for (int i = 0; i < primary.Count; i++) maximum = Math.Max(maximum, primary[i]);
                for (int i = 0; i < secondary.Count; i++) maximum = Math.Max(maximum, secondary[i]);
                maximum *= 1.12f;
            }
            DrawSeries(graphics, plot, primary, maximum, PrimaryColor);
            DrawSeries(graphics, plot, secondary, maximum, SecondaryColor);
        }

        private static void DrawSeries(Graphics graphics, RectangleF plot, List<float> values, float maximum, Color color)
        {
            if (values.Count < 2) return;
            PointF[] points = new PointF[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                float x = plot.Left + plot.Width * i / Math.Max(1, Capacity - 1);
                float y = plot.Bottom - plot.Height * Math.Min(1f, values[i] / Math.Max(0.001f, maximum));
                points[i] = new PointF(x, y);
            }
            using (Pen pen = new Pen(color, 1.8f))
            {
                pen.LineJoin = LineJoin.Round;
                graphics.DrawLines(pen, points);
            }
        }
    }
}
