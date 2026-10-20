using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    /// <summary>
    /// The "· 自动更新 · (已运行 …)" suffix shared by the three service rows.
    ///
    /// Clicking the parenthesised uptime cycles through several readings of the
    /// same fact so the row can show whichever is most useful at a glance:
    /// elapsed time, time since the service came up, or the raw start moment.
    /// The chosen variant is remembered for the lifetime of the process; each
    /// row keeps its own choice.
    /// </summary>
    internal static class StatusSuffix
    {
        internal const int VariantCount = 4;

        /// <summary>
        /// Build the text after "自动更新". Returns the uptime clause only; the
        /// caller keeps the leading separator so spacing stays consistent.
        /// </summary>
        internal static string Build(int uptimeSeconds, int variant, DateTime now)
        {
            if (uptimeSeconds <= 0) return string.Empty;
            TimeSpan elapsed = TimeSpan.FromSeconds(Math.Max(0, uptimeSeconds));
            DateTime started = now - elapsed;
            switch (((variant % VariantCount) + VariantCount) % VariantCount)
            {
                case 0:
                    return "已运行 " + Compact(elapsed);
                case 1:
                    return "已持续 " + Compact(elapsed);
                case 2:
                    return "启动于 " + started.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                default:
                    return "已运行 " + Compact(elapsed) + "（" + started.ToString("HH:mm", CultureInfo.InvariantCulture) + " 启动）";
            }
        }

        /// <summary>
        /// "3天 04:12" / "4小时 12分" / "12分 05秒" — short enough for one status line.
        /// </summary>
        internal static string Compact(TimeSpan elapsed)
        {
            if (elapsed.TotalDays >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}天 {1:00}:{2:00}", (int)elapsed.TotalDays, elapsed.Hours, elapsed.Minutes);
            if (elapsed.TotalHours >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}小时 {1:00}分", (int)elapsed.TotalHours, elapsed.Minutes);
            if (elapsed.TotalMinutes >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}分 {1:00}秒", (int)elapsed.TotalMinutes, elapsed.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}秒", Math.Max(0, (int)elapsed.TotalSeconds));
        }

        /// <summary>
        /// Compose the full status line. The uptime clause is omitted entirely
        /// when the service is not running, so a stopped unit never shows a
        /// stale duration.
        /// </summary>
        internal static string Compose(string status, int uptimeSeconds, int variant, DateTime now)
        {
            string suffix = Build(uptimeSeconds, variant, now);
            string line = status + "  ·  自动更新";
            if (suffix.Length == 0) return line;
            return line + " · (" + suffix + ")";
        }
    }
}
