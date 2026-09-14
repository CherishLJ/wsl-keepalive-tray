using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WSLKeepAliveTray
{
    internal sealed class DshCommandResult
    {
        public int Code;
        public string Output = "";
        public string Error = "";
    }

    public sealed class DshController
    {
        private readonly string distro;
        private readonly Func<bool> online;
        private readonly Func<string, int, DshCommandResult> run;
        private readonly object sync = new object();
        private TelemetrySnapshot snapshot;
        private int busy;
        private string message = "";
        public event EventHandler Changed;
        public event EventHandler BoardRequested;
        public void OpenBoard() { var handler=BoardRequested; if(handler!=null) handler(this,EventArgs.Empty); }
        public bool Busy { get { return Interlocked.CompareExchange(ref busy, 0, 0) != 0; } }
        public string Message { get { lock(sync) return message; } }
        public TelemetrySnapshot Snapshot { get { lock(sync) return snapshot; } }
        public bool Fresh
        {
            get
            {
                TelemetrySnapshot value = Snapshot;
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                return online() && value != null && value.DshCheckedUnixMs > 0 &&
                    now - value.DshCheckedUnixMs >= -5000 && now - value.DshCheckedUnixMs < 20000;
            }
        }
        public bool CanStart { get { return !Busy && Fresh && Snapshot.DshLoadState == "loaded" &&
            (Snapshot.DshActiveState == "inactive" || Snapshot.DshActiveState == "failed"); } }
        public bool CanStop { get { return !Busy && Fresh && Snapshot.DshLoadState == "loaded" &&
            (Snapshot.DshActiveState == "active" || Snapshot.DshActiveState == "activating"); } }
        public bool CanRestart { get { return !Busy && Fresh && Snapshot.DshLoadState == "loaded" &&
            (Snapshot.DshActiveState == "active" || Snapshot.DshActiveState == "failed" || Snapshot.DshActiveState == "inactive"); } }
        public bool CanRefresh { get { return !Busy && online(); } }
        public string Status
        {
            get
            {
                if (!Fresh) return "DSH · 状态未知 / 等待遥测";
                TelemetrySnapshot value = Snapshot;
                if (value.DshLoadState == "not-found") return "DSH · 未安装服务";
                if (value.DshActiveState == "active") return value.DshWebReady ? "DSH · 运行中 · 网页正常" : "DSH · 进程运行 · 网页未就绪";
                if (value.DshActiveState == "inactive") return "DSH · 已停止";
                if (value.DshActiveState == "failed") return "DSH · 启动失败";
                if (value.DshActiveState == "activating") return "DSH · 正在启动";
                if (value.DshActiveState == "deactivating") return "DSH · 正在停止";
                return "DSH · 状态未知";
            }
        }
        internal DshController(string distroName, Func<bool> isOnline)
            : this(distroName, isOnline, Execute) { }
        internal DshController(string distroName, Func<bool> isOnline, Func<string, int, DshCommandResult> runner)
        { distro = distroName; online = isOnline; run = runner; }
        public void Accept(TelemetrySnapshot value)
        {
            if (value.DshCheckedUnixMs <= 0) return;
            lock(sync)
            {
                if(snapshot != null && value.DshCheckedUnixMs < snapshot.DshCheckedUnixMs) return;
                snapshot = value;
            }
            Notify();
        }
        internal string Arguments(string action)
        {
            if(action == "refresh") return "-d " + distro + " --exec /usr/local/sbin/wsl-tray-agent --dsh-status";
            if(action != "start" && action != "stop" && action != "restart") throw new ArgumentException("Unsupported DSH action");
            return "-d " + distro + " -u root --exec systemctl --no-ask-password " + action + " dsh-wsl.service";
        }
        public void Act(string action)
        {
            Arguments(action); // Validate before queueing; never accept shell text.
            bool allowed = action == "refresh" ? CanRefresh : action == "start" ? CanStart : action == "stop" ? CanStop : CanRestart;
            if(!allowed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            SetMessage(action == "refresh" ? "正在刷新…" : "正在执行 " + ActionName(action) + "…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DshCommandResult result = run(Arguments(action), action == "refresh" ? 8000 : 45000);
                    if(action == "refresh")
                    {
                        if(result.Code != 0) throw new InvalidOperationException("状态查询失败，稍后重试。");
                        Accept(TelemetrySnapshot.Parse(result.Output));
                        SetMessage("状态已刷新");
                    }
                    else
                    {
                        DshCommandResult check = run(Arguments("refresh"), 8000);
                        if(check.Code == 0) Accept(TelemetrySnapshot.Parse(check.Output));
                        if(result.Code != 0)
                            SetMessage(ActionName(action) + "未确认成功：" + (result.Code == -2 ? "等待超时，请刷新状态。" : "systemd 返回错误，请检查服务日志。"));
                        else if(Fresh && ((action == "stop" && Snapshot.DshActiveState == "inactive") ||
                            (action != "stop" && Snapshot.DshActiveState == "active" && Snapshot.DshWebReady)))
                            SetMessage(ActionName(action) + "完成");
                        else SetMessage("指令已完成，服务或网页尚未就绪；状态会继续更新。");
                    }
                }
                catch(Exception ex) { SetMessage("操作失败：" + ex.Message); }
                finally { Interlocked.Exchange(ref busy, 0); Notify(); }
            });
        }
        private static string ActionName(string action) { return action == "start" ? "启动" : action == "stop" ? "停止" : "重启"; }
        private void SetMessage(string value) { lock(sync) message = value; Notify(); }
        private void Notify() { EventHandler handler = Changed; if(handler != null) handler(this, EventArgs.Empty); }
        public void OpenWeb()
        {
            if(!Fresh || !Snapshot.DshWebReady) return;
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:3080/") { UseShellExecute = true }); }
            catch(Exception ex) { SetMessage("打开网页失败：" + ex.Message); }
        }
        private static DshCommandResult Execute(string arguments, int timeout)
        {
            var result = new DshCommandResult();
            var output = new StringBuilder();
            var error = new StringBuilder();
            using(var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"), arguments) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { if(args.Data != null) lock(output) output.AppendLine(args.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if(args.Data != null) lock(error) error.AppendLine(args.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if(!process.WaitForExit(timeout))
                {
                    try { process.Kill(); } catch { }
                    result.Code = -2;
                    return result;
                }
                process.WaitForExit();
                result.Code = process.ExitCode;
                lock(output) result.Output = output.ToString();
                lock(error) result.Error = error.ToString();
                return result;
            }
        }
    }
}
