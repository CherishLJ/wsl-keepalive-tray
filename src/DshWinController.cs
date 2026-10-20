using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace WSLKeepAliveTray
{
    public sealed class DshWinController
    {
        private readonly string distro;
        private readonly Func<bool> online;
        private readonly Func<string, int, DshCommandResult> run;
        private readonly object sync = new object();
        private TelemetrySnapshot snapshot;
        private int busy;
        private string message = "";
        public event EventHandler Changed;
        public bool Busy { get { return Interlocked.CompareExchange(ref busy, 0, 0) != 0; } }
        public string Message { get { lock (sync) return message; } }
        public TelemetrySnapshot Snapshot { get { lock (sync) return snapshot; } }
        public bool Fresh
        {
            get
            {
                TelemetrySnapshot value = Snapshot;
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                return online() && value != null && value.DshWinCheckedUnixMs > 0 &&
                    now - value.DshWinCheckedUnixMs >= -5000 && now - value.DshWinCheckedUnixMs < 20000;
            }
        }
        public bool ProxyLoaded { get { return Fresh && Snapshot.DshWinLoadState == "loaded"; } }
        public bool CanStart { get { return !Busy && ProxyLoaded &&
            (Snapshot.DshWinActiveState == "inactive" || Snapshot.DshWinActiveState == "failed"); } }
        public bool CanStop { get { return !Busy && ProxyLoaded &&
            (Snapshot.DshWinActiveState == "active" || Snapshot.DshWinActiveState == "activating"); } }
        public bool CanRestart { get { return !Busy && ProxyLoaded &&
            (Snapshot.DshWinActiveState == "active" || Snapshot.DshWinActiveState == "failed" || Snapshot.DshWinActiveState == "inactive"); } }
        public bool CanRefresh { get { return !Busy && online(); } }
        public string Status
        {
            get
            {
                if (!Fresh) return "dsh-win · 状态未知 / 等待遥测";
                TelemetrySnapshot value = Snapshot;
                string client = value.DshWinClientUp ? "客户端运行" : "客户端未开";
                if (value.DshWinLoadState == "not-found")
                    return "dsh-win · " + client + " · 网页入口未安装";
                if (value.DshWinActiveState == "active")
                    return "dsh-win · " + client + " · " + (value.DshWinWebReady ? "网页可访问" : "网页未响应");
                if (value.DshWinActiveState == "inactive") return "dsh-win · " + client + " · 网页入口已停止";
                if (value.DshWinActiveState == "failed") return "dsh-win · " + client + " · 网页入口启动失败";
                if (value.DshWinActiveState == "activating") return "dsh-win · " + client + " · 网页入口正在启动";
                if (value.DshWinActiveState == "deactivating") return "dsh-win · " + client + " · 网页入口正在停止";
                return value.DshWinClientUp ? "dsh-win · " + client : "dsh-win · 状态未知";
            }
        }
        internal DshWinController(string distroName, Func<bool> isOnline)
            : this(distroName, isOnline, DshController.Execute) { }
        internal DshWinController(string distroName, Func<bool> isOnline, Func<string, int, DshCommandResult> runner)
        { distro = distroName; online = isOnline; run = runner; }
        public void Accept(TelemetrySnapshot value)
        {
            if (value.DshWinCheckedUnixMs <= 0) return;
            lock (sync)
            {
                if (snapshot != null && value.DshWinCheckedUnixMs < snapshot.DshWinCheckedUnixMs) return;
                snapshot = value;
            }
            Notify();
        }
        internal string Arguments(string action)
        {
            if (action == "refresh") return "-d " + distro + " --exec /usr/local/sbin/wsl-tray-agent --dsh-win-status";
            if (action != "start" && action != "stop" && action != "restart") throw new ArgumentException("Unsupported DSH-Win action");
            return "-d " + distro + " --exec systemctl --user --no-ask-password " + action + " dsh-win-cookie-proxy.service";
        }

        /// <summary>
        /// "Restart" has to cover the Windows desktop client too, not just the
        /// WSL cookie proxy. Restarting only the proxy reported success while the
        /// client window never moved.
        /// </summary>
        private void RestartDesktopThenProxy()
        {
            SetMessage("正在重启 dsh-win 桌面客户端…");
            AppLog.Write("dsh-win restart: handing off to restart-desktop worker");
            DshCommandResult desktop = DshWinDesktop.Restart(60000);
            AppLog.Write("dsh-win restart: worker exit=" + desktop.Code + " out=" + FirstLine(desktop.Output, "") + " err=" + FirstLine(desktop.Error, ""));
            if (desktop.Code != 0)
            {
                SetMessage("桌面客户端重启未成功：" + FirstLine(desktop.Error, desktop.Output));
                return;
            }
            SetMessage("桌面客户端已重启，等待新实例上线…");
            // The worker kills the old client about 5s after hand-off and relaunches
            // it a few seconds later. Probing immediately would still see the old
            // process and report a false success, so wait for the port to change
            // away from the pre-restart value (or for a short settle first).
            int before = Snapshot == null ? 0 : Snapshot.DshWinClientPort;
            bool back = DshWinDesktop.WaitUntilClientUp(delegate
            {
                DshCommandResult probe = run(Arguments("refresh"), 8000);
                if (probe.Code != 0) return false;
                TelemetrySnapshot parsed = TelemetrySnapshot.Parse(probe.Output);
                if (parsed == null) return false;
                Accept(parsed);
                if (!parsed.DshWinClientUp) return false;
                // Same port as before the restart means the old process is still up.
                return before <= 0 || parsed.DshWinClientPort != before;
            }, 60000, 3000);

            AppLog.Write("dsh-win restart: client back=" + back);
            SetMessage(back ? "桌面客户端已恢复，正在重启网页入口…" : "桌面客户端未在 45 秒内恢复，仍重启网页入口…");
            DshCommandResult proxy = run(Arguments("restart"), 45000);
            DshCommandResult check = run(Arguments("refresh"), 8000);
            if (check.Code == 0) Accept(TelemetrySnapshot.Parse(check.Output));
            if (proxy.Code != 0)
            {
                SetMessage("网页入口重启失败，请刷新状态或查看服务日志。");
                return;
            }
            SetMessage(back && Fresh && Snapshot.DshWinClientUp
                ? "重启完成：桌面客户端与网页入口都已恢复"
                : "网页入口已重启；桌面客户端状态待确认，请稍后刷新。");
        }

        private static string FirstLine(string primary, string fallback)
        {
            string text = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
            if (string.IsNullOrWhiteSpace(text)) return "未知错误";
            string[] lines = text.Replace("\r", "").Split('\n');
            foreach (string line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }
            return "未知错误";
        }
        public void Act(string action)
        {
            Arguments(action);
            bool allowed = action == "refresh" ? CanRefresh : action == "start" ? CanStart : action == "stop" ? CanStop : CanRestart;
            if (!allowed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            SetMessage(action == "refresh" ? "正在刷新…" : "正在执行 " + ActionName(action) + "…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (action == "restart")
                    {
                        RestartDesktopThenProxy();
                        return;
                    }
                    DshCommandResult result = run(Arguments(action), action == "refresh" ? 8000 : 45000);
                    if (action == "refresh")
                    {
                        if (result.Code != 0) throw new InvalidOperationException("状态查询失败，稍后重试。");
                        Accept(TelemetrySnapshot.Parse(result.Output));
                        SetMessage("状态已刷新");
                    }
                    else
                    {
                        DshCommandResult check = run(Arguments("refresh"), 8000);
                        if (check.Code == 0) Accept(TelemetrySnapshot.Parse(check.Output));
                        if (result.Code != 0)
                            SetMessage(ActionName(action) + "未确认成功：" + (result.Code == -2 ? "等待超时，请刷新状态。" : "systemd 返回错误，请检查服务日志。"));
                        else if (Fresh && ((action == "stop" && Snapshot.DshWinActiveState == "inactive") ||
                            (action != "stop" && Snapshot.DshWinActiveState == "active")))
                            SetMessage(ActionName(action) + "完成");
                        else SetMessage("指令已完成，网页入口尚未响应；状态会继续更新。");
                    }
                }
                catch (Exception ex) { SetMessage("操作失败：" + ex.Message); }
                finally { Interlocked.Exchange(ref busy, 0); Notify(); }
            });
        }
        private static string ActionName(string action) { return action == "start" ? "启动" : action == "stop" ? "停止" : "重启"; }
        private void SetMessage(string value) { lock (sync) message = value; Notify(); }
        private void Notify() { EventHandler handler = Changed; if (handler != null) handler(this, EventArgs.Empty); }
        public void OpenWeb()
        {
            if (!Fresh || !Snapshot.DshWinWebReady) return;
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:19388/") { UseShellExecute = true }); }
            catch (Exception ex) { SetMessage("打开网页失败：" + ex.Message); }
        }
    }
}
