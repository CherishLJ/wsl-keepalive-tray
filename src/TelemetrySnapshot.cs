using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace WSLKeepAliveTray
{
    public sealed class TelemetrySnapshot
    {
        public int SchemaVersion { get; set; }
        public long TimestampUnixMs { get; set; }
        public string Hostname { get; set; }
        public string Kernel { get; set; }
        public double UptimeSeconds { get; set; }
        public double CpuPercent { get; set; }
        public int ProcessorCount { get; set; }
        public double Load1 { get; set; }
        public double Load5 { get; set; }
        public double Load15 { get; set; }
        public long MemoryUsedBytes { get; set; }
        public long MemoryTotalBytes { get; set; }
        public long SwapUsedBytes { get; set; }
        public long SwapTotalBytes { get; set; }
        public long RootUsedBytes { get; set; }
        public long RootTotalBytes { get; set; }
        public long DiskReadBytesPerSecond { get; set; }
        public long DiskWriteBytesPerSecond { get; set; }
        public string NetworkInterface { get; set; }
        public long NetworkReceiveBytesPerSecond { get; set; }
        public long NetworkTransmitBytesPerSecond { get; set; }
        public string SystemdState { get; set; }
        public bool DockerActive { get; set; }
        public bool SshActive { get; set; }
        public bool WatchdogTimerActive { get; set; }
        public int ContainersRunning { get; set; }
        public int ContainersTotal { get; set; }
        public string[] ContainerNames { get; set; }
        public string ServiceError { get; set; }
        public string AgentError { get; set; }
        public DateTime ReceivedAtUtc { get; set; }

        public TelemetrySnapshot()
        {
            Hostname = string.Empty;
            Kernel = string.Empty;
            NetworkInterface = string.Empty;
            SystemdState = string.Empty;
            ContainerNames = new string[0];
            ServiceError = string.Empty;
            AgentError = string.Empty;
            ReceivedAtUtc = DateTime.UtcNow;
        }

        public bool IsHealthy
        {
            get
            {
                bool containersHealthy = ContainersTotal == 0 || ContainersRunning == ContainersTotal;
                return string.IsNullOrEmpty(AgentError) && DockerActive && SshActive &&
                    WatchdogTimerActive && containersHealthy;
            }
        }

        public double MemoryPercent
        {
            get { return Percent(MemoryUsedBytes, MemoryTotalBytes); }
        }

        public double SwapPercent
        {
            get { return Percent(SwapUsedBytes, SwapTotalBytes); }
        }

        public double RootPercent
        {
            get { return Percent(RootUsedBytes, RootTotalBytes); }
        }

        private static double Percent(long used, long total)
        {
            return total <= 0 ? 0 : Math.Max(0, Math.Min(100, used * 100.0 / total));
        }

        public static TelemetrySnapshot Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("Telemetry JSON is empty.", "json");
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 1024 * 1024;
            TelemetrySnapshot snapshot = serializer.Deserialize<TelemetrySnapshot>(json);
            if (snapshot == null || snapshot.SchemaVersion != 1)
            {
                throw new InvalidOperationException("Unsupported telemetry schema.");
            }
            snapshot.ReceivedAtUtc = DateTime.UtcNow;
            if (snapshot.ContainerNames == null)
            {
                snapshot.ContainerNames = new string[0];
            }
            return snapshot;
        }

        public static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            string format = value >= 100 || unit == 0 ? "0" : value >= 10 ? "0.0" : "0.00";
            return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
        }

        public static string FormatRate(double bytesPerSecond)
        {
            return FormatBytes(bytesPerSecond) + "/s";
        }

        public static string FormatDuration(double seconds)
        {
            TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (duration.TotalDays >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}天 {1:00}:{2:00}", (int)duration.TotalDays, duration.Hours, duration.Minutes);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", duration.Hours, duration.Minutes, duration.Seconds);
        }
    }

    public enum TrayHealthState
    {
        Starting,
        Healthy,
        Warning,
        Stopped,
        Error
    }

    public sealed class TelemetryEventArgs : EventArgs
    {
        public TelemetrySnapshot Snapshot { get; private set; }

        public TelemetryEventArgs(TelemetrySnapshot snapshot)
        {
            Snapshot = snapshot;
        }
    }

    public sealed class AgentStateEventArgs : EventArgs
    {
        public TrayHealthState State { get; private set; }
        public string Message { get; private set; }

        public AgentStateEventArgs(TrayHealthState state, string message)
        {
            State = state;
            Message = message ?? string.Empty;
        }
    }
}

