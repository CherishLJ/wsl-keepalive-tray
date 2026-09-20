using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WSLKeepAliveTray
{
    public sealed class McpHubController
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
        public string Message { get { lock(sync) return message; } }
        public TelemetrySnapshot Snapshot { get { lock(sync) return snapshot; } }
        public bool Fresh
        {
            get
            {
                TelemetrySnapshot value = Snapshot;
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                return online() && value != null && value.McpHubCheckedUnixMs > 0 &&
                    now - value.McpHubCheckedUnixMs >= -5000 && now - value.McpHubCheckedUnixMs < 20000;
            }
        }
        public bool CanStart { get { return !Busy && Fresh && Snapshot.McpHubLoadState == "loaded" &&
            (Snapshot.McpHubActiveState == "inactive" || Snapshot.McpHubActiveState == "failed"); } }
        public bool CanStop { get { return !Busy && Fresh && Snapshot.McpHubLoadState == "loaded" &&
            (Snapshot.McpHubActiveState == "active" || Snapshot.McpHubActiveState == "activating"); } }
        public bool CanRestart { get { return !Busy && Fresh && Snapshot.McpHubLoadState == "loaded" &&
            (Snapshot.McpHubActiveState == "active" || Snapshot.McpHubActiveState == "failed" || Snapshot.McpHubActiveState == "inactive"); } }
        public bool CanRefresh { get { return !Busy && online(); } }
        public string Status
        {
            get
            {
                if (!Fresh) return "MCP Hub · 状态未知 / 等待遥测";
                TelemetrySnapshot value = Snapshot;
                if (value.McpHubLoadState == "not-found") return "MCP Hub · 未安装服务";
                if (value.McpHubActiveState == "active") return value.McpHubReady ? "MCP Hub · 运行中 · 网关正常" : "MCP Hub · 进程运行 · 网关未就绪";
                if (value.McpHubActiveState == "inactive") return "MCP Hub · 已停止";
                if (value.McpHubActiveState == "failed") return "MCP Hub · 启动失败";
                if (value.McpHubActiveState == "activating") return "MCP Hub · 正在启动";
                if (value.McpHubActiveState == "deactivating") return "MCP Hub · 正在停止";
                return "MCP Hub · 状态未知";
            }
        }
        internal McpHubController(string distroName, Func<bool> isOnline)
            : this(distroName, isOnline, DshController.Execute) { }
        internal McpHubController(string distroName, Func<bool> isOnline, Func<string, int, DshCommandResult> runner)
        { distro = distroName; online = isOnline; run = runner; }
        public void Accept(TelemetrySnapshot value)
        {
            if (value.McpHubCheckedUnixMs <= 0) return;
            lock(sync)
            {
                if(snapshot != null && value.McpHubCheckedUnixMs < snapshot.McpHubCheckedUnixMs) return;
                snapshot = value;
            }
            Notify();
        }
        internal string Arguments(string action)
        {
            // mcp-hub.service is a user unit of the distro's login user (jie);
            // control goes through machinectl shell to reach the user bus.
            if(action == "refresh") return "-d " + distro + " --exec /usr/local/sbin/wsl-tray-agent --mcp-hub-status";
            if(action != "start" && action != "stop" && action != "restart") throw new ArgumentException("Unsupported MCP Hub action");
            return "-d " + distro + " -u root --exec machinectl shell -q jie@ /usr/bin/systemctl --user --no-ask-password " + action + " mcp-hub.service";
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
                        else if(Fresh && ((action == "stop" && Snapshot.McpHubActiveState == "inactive") ||
                            (action != "stop" && Snapshot.McpHubActiveState == "active" && Snapshot.McpHubReady)))
                            SetMessage(ActionName(action) + "完成");
                        else SetMessage("指令已完成，服务或网关尚未就绪；状态会继续更新。");
                    }
                }
                catch(Exception ex) { SetMessage("操作失败：" + ex.Message); }
                finally { Interlocked.Exchange(ref busy, 0); Notify(); }
            });
        }
        private static string ActionName(string action) { return action == "start" ? "启动" : action == "stop" ? "停止" : "重启"; }
        private void SetMessage(string value) { lock(sync) message = value; Notify(); }
        private void Notify() { EventHandler handler = Changed; if(handler != null) handler(this, EventArgs.Empty); }
    }
}
