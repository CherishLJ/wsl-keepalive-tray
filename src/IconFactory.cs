using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WSLKeepAliveTray
{
    internal static class IconFactory
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(TrayHealthState state)
        {
            Color status = StateColor(state);
            using (Bitmap bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);

                RectangleF tile = new RectangleF(2, 2, 28, 28);
                using (GraphicsPath path = RoundedRectangle(tile, 7))
                using (LinearGradientBrush fill = new LinearGradientBrush(tile, Color.FromArgb(30, 42, 58), Color.FromArgb(12, 20, 31), 90f))
                using (Pen border = new Pen(Color.FromArgb(95, 125, 150), 1f))
                {
                    graphics.FillPath(fill, path);
                    graphics.DrawPath(border, path);
                }

                PointF[] pulse =
                {
                    new PointF(6, 17), new PointF(10, 17), new PointF(12.5f, 11),
                    new PointF(16, 22), new PointF(19, 15), new PointF(23, 15)
                };
                using (Pen pulsePen = new Pen(Color.FromArgb(238, 244, 248), 2.2f))
                {
                    pulsePen.StartCap = LineCap.Round;
                    pulsePen.EndCap = LineCap.Round;
                    pulsePen.LineJoin = LineJoin.Round;
                    graphics.DrawLines(pulsePen, pulse);
                }

                using (Brush glow = new SolidBrush(Color.FromArgb(75, status)))
                using (Brush dot = new SolidBrush(status))
                using (Pen ring = new Pen(Color.FromArgb(235, 250, 252), 1f))
                {
                    graphics.FillEllipse(glow, 20, 20, 11, 11);
                    graphics.FillEllipse(dot, 22, 22, 7, 7);
                    graphics.DrawEllipse(ring, 22, 22, 7, 7);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon icon = Icon.FromHandle(handle))
                    {
                        return (Icon)icon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        public static Color StateColor(TrayHealthState state)
        {
            switch (state)
            {
                case TrayHealthState.Healthy: return Color.FromArgb(47, 211, 129);
                case TrayHealthState.Warning: return Color.FromArgb(255, 184, 77);
                case TrayHealthState.Error: return Color.FromArgb(255, 91, 105);
                case TrayHealthState.Stopped: return Color.FromArgb(145, 158, 171);
                default: return Color.FromArgb(74, 163, 255);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

