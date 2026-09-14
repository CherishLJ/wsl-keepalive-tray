using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using WSLKeepAliveTray;
class DshTests
{
    static StringBuilder report = new StringBuilder();
    static int failures;
    static long Now { get { return (long)(DateTime.UtcNow - new DateTime(1970,1,1)).TotalMilliseconds; } }
    static TelemetrySnapshot State(string active)
    {
        return new TelemetrySnapshot { SchemaVersion = 1, DshLoadState = "loaded", DshActiveState = active,
            DshSubState = active == "active" ? "running" : "dead", DshWebReady = active == "active", DshCheckedUnixMs = Now };
    }
    static void Check(bool value, string name) { report.AppendLine((value ? "PASS " : "FAIL ") + name); if(!value) failures++; }
    static void Wait(DshController value) { if(!SpinWait.SpinUntil(()=>!value.Busy, 4000)) throw new Exception("operation did not finish"); }
    static void Main(string[] args)
    {
        try
        {
            string active = "active";
            int mutations = 0;
            string last = "";
            var controller = new DshController("Ubuntu-24.04", ()=>true, (command, timeout)=> {
                last = command;
                if(command.EndsWith("--dsh-status")) return new DshCommandResult { Code = 0, Output = new JavaScriptSerializer().Serialize(State(active)) };
                mutations++; active = command.Contains(" stop ") ? "inactive" : "active";
                return new DshCommandResult { Code = 0 };
            });
            Check(!controller.CanStop && !controller.CanStart && controller.Status.Contains("未知"), "no telemetry does not claim running");
            controller.Accept(State("active"));
            Check(controller.CanStop && !controller.CanStart && controller.CanRestart, "running-state actions");
            Check(controller.Arguments("restart") == "-d Ubuntu-24.04 -u root --exec systemctl --no-ask-password restart dsh-wsl.service", "exact unit and distro, no shell");
            bool rejected = false;
            try { controller.Arguments("stop; reboot"); } catch(ArgumentException) { rejected = true; }
            Check(rejected, "reject unsupported commands");
            controller.Act("stop"); Wait(controller);
            Check(controller.CanStart && !controller.CanStop && controller.Status.Contains("已停止") && mutations == 1, "stop verifies inactive and enables start");
            controller.Act("start"); Wait(controller);
            Check(controller.CanStop && mutations == 2 && controller.Message.Contains("完成"), "start verifies active and web-ready");
            controller.Act("restart"); Wait(controller);
            Check(mutations == 3 && last.EndsWith("--dsh-status"), "restart followed by fresh status query");
            controller.Act("refresh"); Wait(controller);
            Check(mutations == 3, "refresh is read-only");
            var unavailable = State("active"); unavailable.DshWebReady = false; unavailable.DshCheckedUnixMs = Now+1;
            controller.Accept(unavailable);
            Check(controller.Status.Contains("网页未就绪"), "process-active does not imply web-ready");
            var stale = new DshController("Ubuntu-24.04", ()=>true);
            var old = State("active"); old.DshCheckedUnixMs -= 60000; stale.Accept(old);
            Check(!stale.Fresh && !stale.CanStop && stale.Status.Contains("未知"), "stale telemetry disables actions");
            var missing = new DshController("Ubuntu-24.04", ()=>true);
            var absent = State("inactive"); absent.DshLoadState="not-found"; missing.Accept(absent);
            Check(!missing.CanStart && missing.Status.Contains("未安装"), "missing service handled");
            var offline = new DshController("Ubuntu-24.04", ()=>false); offline.Accept(State("active"));
            Check(!offline.CanRefresh && !offline.CanRestart && !offline.Fresh, "offline WSL never triggers a service command");
            var timeoutController = new DshController("Ubuntu-24.04", ()=>true, (command,timeout)=>
                command.EndsWith("--dsh-status") ? new DshCommandResult { Code=0, Output=new JavaScriptSerializer().Serialize(State("active")) } : new DshCommandResult { Code=-2 });
            timeoutController.Accept(State("active")); timeoutController.Act("restart"); Wait(timeoutController);
            Check(timeoutController.Message.Contains("超时") && !timeoutController.Busy, "timeout releases busy state without claiming success");
            using(var release = new ManualResetEvent(false))
            {
                int calls = 0;
                var blocked = new DshController("Ubuntu-24.04", ()=>true, (command,timeout)=> {
                    Interlocked.Increment(ref calls); release.WaitOne(3000);
                    return new DshCommandResult { Code=0, Output=new JavaScriptSerializer().Serialize(State("active")) };
                });
                blocked.Accept(State("active")); blocked.Act("refresh"); blocked.Act("restart");
                Check(blocked.Busy && !blocked.CanStop, "in-flight action disables competing actions");
                release.Set(); Wait(blocked); Check(calls==1, "double-click does not enqueue duplicate command");
            }
        }
        catch(Exception ex) { Check(false, ex.ToString()); }
        report.AppendLine("FAILURES=" + failures);
        File.WriteAllText(args[0], report.ToString()); Environment.ExitCode=failures==0 ? 0 : 1;
    }
}
