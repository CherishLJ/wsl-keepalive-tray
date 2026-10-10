using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace WSLKeepAliveTray
{
    public sealed class GpuSample
    {
        public bool Available;
        public string Name = "";
        public double UtilizationPercent;
        public double MemoryUtilizationPercent;
        public double MemoryUsedMiB;
        public double MemoryTotalMiB;
        public double TemperatureC;
        public double PowerWatts;
        public double PowerLimitWatts;
        public double SmClockMHz;
        public string Error = "";
        public DateTime SampledAtUtc = DateTime.MinValue;
        public long Sequence;

        public double MemoryPercent
        {
            get { return MemoryTotalMiB <= 0 ? 0 : Math.Max(0, Math.Min(100, MemoryUsedMiB * 100.0 / MemoryTotalMiB)); }
        }
    }

    public sealed class WindowsGpuSampler : IDisposable
    {
        private readonly Func<string, int, string> query;
        private readonly object sync = new object();
        private readonly Timer timer;
        private int sampling;
        private GpuSample latest = new GpuSample();
        private long sequence;

        public WindowsGpuSampler()
            : this(QueryNvidiaSmi) { }

        internal WindowsGpuSampler(Func<string, int, string> runner)
        {
            query = runner;
            timer = new Timer(delegate { Sample(); }, null, 400, 2000);
        }

        public GpuSample Latest
        {
            get { lock (sync) return latest; }
        }

        internal static bool TryParse(string csv, out GpuSample sample, out string error)
        {
            sample = new GpuSample();
            error = "";
            if (string.IsNullOrWhiteSpace(csv))
            {
                error = "nvidia-smi 无输出";
                return false;
            }
            string line = csv.Trim().Split('\n')[0].Trim();
            string[] fields = line.Split(',');
            if (fields.Length < 9)
            {
                error = "nvidia-smi 输出不完整";
                return false;
            }
            double utilization, memoryUtilization, used, total, temperature, power, limit, clock;
            if (!TryNumber(fields[1], out utilization) || !TryNumber(fields[2], out memoryUtilization) ||
                !TryNumber(fields[3], out used) || !TryNumber(fields[4], out total) ||
                !TryNumber(fields[5], out temperature) || !TryNumber(fields[6], out power) ||
                !TryNumber(fields[7], out limit) || !TryNumber(fields[8], out clock))
            {
                error = "nvidia-smi 数值无法解析";
                return false;
            }
            sample.Available = true;
            sample.Name = fields[0].Trim();
            sample.UtilizationPercent = utilization;
            sample.MemoryUtilizationPercent = memoryUtilization;
            sample.MemoryUsedMiB = used;
            sample.MemoryTotalMiB = total;
            sample.TemperatureC = temperature;
            sample.PowerWatts = power;
            sample.PowerLimitWatts = limit;
            sample.SmClockMHz = clock;
            sample.SampledAtUtc = DateTime.UtcNow;
            sample.Sequence = 1;
            return true;
        }

        private static bool TryNumber(string text, out double value)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text == "[N/A]" || text == "N/A")
            {
                value = 0;
                return true;
            }
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void Sample()
        {
            if (Interlocked.CompareExchange(ref sampling, 1, 0) != 0) return;
            GpuSample next = new GpuSample { SampledAtUtc = DateTime.UtcNow };
            try
            {
                string output = query(
                    "--query-gpu=name,utilization.gpu,utilization.memory,memory.used,memory.total,temperature.gpu,power.draw,power.limit,clocks.sm --format=csv,noheader,nounits",
                    2500);
                string error;
                GpuSample parsed;
                if (!TryParse(output, out parsed, out error))
                {
                    next.Error = error;
                }
                else
                {
                    next = parsed;
                    next.Sequence = Interlocked.Increment(ref sequence);
                }
            }
            catch (Exception ex)
            {
                next.Error = ex.Message;
            }
            finally
            {
                lock (sync) latest = next;
                Interlocked.Exchange(ref sampling, 0);
            }
        }

        private static string QueryNvidiaSmi(string arguments, int timeoutMs)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "nvidia-smi.exe";
            info.Arguments = arguments;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.ASCII;
            using (Process process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("无法启动 nvidia-smi");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("nvidia-smi 超时");
                }
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "nvidia-smi 失败" : error.Trim());
                return output;
            }
        }

        public void Dispose()
        {
            timer.Dispose();
        }
    }
}
