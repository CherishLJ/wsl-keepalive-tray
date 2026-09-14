using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using WSLKeepAliveTray;
class BoardTests
{
    static StringBuilder report=new StringBuilder(); static int failures;
    static void Check(bool ok,string name) { report.AppendLine((ok ? "PASS " : "FAIL ")+name); if(!ok)failures++; }
    static IEnumerable<Control> Controls(Control root) { foreach(Control c in root.Controls) { yield return c; foreach(var child in Controls(c)) yield return child; } }
    [STAThread] static void Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            string json="{\"type\":\"server-response\",\"rpcId\":\"test\",\"result\":{\"ok\":true,\"value\":{\"items\":[{\"sessionId\":\"parent\",\"running\":false},{\"sessionId\":\"child\",\"parentSessionId\":\"parent\",\"running\":true,\"projections\":{\"values\":{\"title\":\"needle\",\"todos\":[{\"content\":\"one\",\"status\":\"completed\"}]}}}]}}}";
            var parsed=BoardTask.Parse(json,"test");
            Check(parsed.Count==2 && parsed[1].Todos.Length==1,"parse optional metadata and todos");
            Check(BoardTask.Filter(parsed,"needle",true).SetEquals(new[]{"parent","child"}),"search/running filter retains child ancestors");
            Check(BoardTask.Filter(parsed,"missing",false).Count==0,"empty search result");
            parsed[0].Parent="child";
            Check(BoardTask.Filter(parsed,"needle",true).Count==2,"cyclic ancestry terminates");
            bool rejected=false; try {BoardTask.Parse(json,"wrong");}catch(InvalidDataException){rejected=true;}
            Check(rejected,"reject mismatched RPC response");
            var serializer=new JavaScriptSerializer();
            foreach(string method in new[]{"session/rename","session/fork","session/cancel"})
            {
                int calls=0;
                var result=BoardApi.Manage(method,"fake-session"," 新名称 ",(endpoint,body)=>{
                    calls++; var envelope=serializer.Deserialize<Dictionary<string,object>>(body);
                    var wireArgs=BoardTask.Object(BoardTask.Object(envelope,"payload"),"args");
                    var request=BoardTask.Object(wireArgs,"request");
                    Check(endpoint==method && BoardTask.Text(envelope,"method")==method && BoardTask.Text(envelope,"type")=="client-request","management endpoint "+method);
                    Check(!wireArgs.ContainsKey("_request") && BoardTask.Text(request,"sessionId")=="fake-session","mutation uses request argument "+method);
                    Check(method=="session/rename" ? BoardTask.Text(request,"title")=="新名称" : request.Count==1,"minimal mutation payload "+method);
                    return serializer.Serialize(new {type="server-response",rpcId=BoardTask.Text(envelope,"rpcId"),result=new {ok=true,value=new {sessionId="new-branch"}}});
                });
                Check(calls==1 && BoardTask.Text(result,"sessionId")=="new-branch","single call and result "+method);
            }
            int attempts=0; rejected=false;
            try { BoardApi.Manage("session/fork","fake",null,(method,body)=>{attempts++;throw new System.Net.WebException("timeout");}); }
            catch(System.Net.WebException) {rejected=true;}
            Check(rejected && attempts==1,"uncertain fork never retried");
            rejected=false; try {BoardApi.Request("session/delete",new {},"x");} catch(ArgumentException){rejected=true;}
            Check(rejected,"unsupported deletion rejected");
            rejected=false; try {BoardApi.Manage("session/rename","fake","  ",(m,b)=>"unused");} catch(ArgumentException){rejected=true;}
            Check(rejected,"empty rename rejected before request");
            foreach(string response in new[]{"{\"type\":\"server-response\",\"rpcId\":\"wrong\",\"result\":{\"ok\":true}}","error","missingFork"})
            {
                rejected=false;
                try {BoardApi.Manage("session/fork","fake",null,(m,b)=>{
                    var e=serializer.Deserialize<Dictionary<string,object>>(b);
                    return response=="error" || response=="missingFork" ? serializer.Serialize(new {type="server-response",rpcId=BoardTask.Text(e,"rpcId"),result=new {ok=response!="error",value=new {}}}) : response;
                });}catch(InvalidDataException){rejected=true;}
                Check(rejected,"reject unconfirmed management response "+response);
            }
            string hiddenPath=Path.Combine(Path.GetDirectoryName(args[0]),"test-hidden-"+Guid.NewGuid().ToString("N")+".json");
            var hidden=new HiddenSessions(hiddenPath);
            Check(HiddenSessions.Branch(parsed,new[]{"parent"}).Count==2,"hidden branch handles cycle");
            parsed[0].Parent="";
            hidden.Set(parsed,"parent",true);
            Check(new HiddenSessions(hiddenPath).Ids.SetEquals(new[]{"parent","child"}),"hidden state persists without session content");
            parsed.Add(new BoardTask {Id="later-child",Parent="child"});
            Check(hidden.Effective(parsed).Contains("later-child"),"future descendants inherit local hiding");
            hidden.Set(parsed,"child",false);
            Check(hidden.Effective(parsed).Count==0,"restoring child also reveals ancestors");
            hidden.Set(parsed,"child",true); hidden.Set(parsed,"child",false);
            Check(new HiddenSessions(hiddenPath).Ids.Count==0,"atomic replacement and restore persist");
            File.WriteAllText(hiddenPath,"invalid json");
            hidden=new HiddenSessions(hiddenPath); rejected=false;
            try {hidden.Set(parsed,"parent",true);}catch(IOException){rejected=true;}
            Check(rejected && File.ReadAllText(hiddenPath)=="invalid json","corrupt hidden state preserved");
            File.Delete(hiddenPath);
            var live=TaskBoardForm.Fetch();
            Check(live.Count>0,"live read-only session API count="+live.Count);
            var controller=new DshController("Ubuntu-24.04",()=>true);
            controller.Accept(new TelemetrySnapshot {DshActiveState="active",DshLoadState="loaded",DshWebReady=true,
                DshCheckedUnixMs=(long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalMilliseconds});
            using(var form=new TaskBoardForm(controller,hiddenPath))
            {
                form.ShowInTaskbar=false; form.Opacity=0; form.Show();
                var tree=Controls(form).OfType<TreeView>().Single();
                DateTime deadline=DateTime.UtcNow.AddSeconds(12);
                while(tree.Nodes.Count==0 && DateTime.UtcNow<deadline) {Application.DoEvents(); Thread.Sleep(30);}
                Check(tree.Nodes.Count>0,"board renders live directory groups");
                if(tree.Nodes.Count>0 && tree.Nodes[0].Nodes.Count>0) tree.SelectedNode=tree.Nodes[0].Nodes[0];
                Check(Controls(form).OfType<TextBox>().Any(t=>t.Multiline && t.Text.Contains("会话 ID")),"selection displays task details");
                Check(Controls(form).OfType<Button>().Single(b=>b.Text=="重命名").Enabled,"selected online session enables rename");
                if(args.Length>1) using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size)); bitmap.Save(args[1]); }
                foreach(Theme theme in ThemeManager.All)
                {
                    ThemeManager.Select(theme.Id,false); form.Size=form.MinimumSize; Application.DoEvents();
                    foreach(var c in Controls(form).Where(c=>c is Button || c is TextBox || c is CheckBox || c is TreeView))
                        Check(c.Bounds.Left>=0 && c.Bounds.Top>=0 && c.Right<=c.Parent.ClientSize.Width && c.Bottom<=c.Parent.ClientSize.Height,
                            theme.Id+" "+c.GetType().Name+" containment");
                }
                controller.Accept(new TelemetrySnapshot {DshActiveState="inactive",DshLoadState="loaded",DshWebReady=false,
                    DshCheckedUnixMs=(long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalMilliseconds});
                Controls(form).OfType<Button>().Single(b=>b.Text=="刷新").PerformClick(); Application.DoEvents();
                Check(!Controls(form).OfType<Button>().Single(b=>b.Text=="重命名").Enabled && !Controls(form).OfType<Button>().Single(b=>b.Text=="复制分支").Enabled,"offline disables native mutations");
                form.Hide(); Check(!form.Visible,"close-to-hide supported"); form.ExitBoard();
            }
        }
        catch(Exception ex){Check(false,ex.ToString());}
        report.AppendLine("FAILURES="+failures); File.WriteAllText(args[0],report.ToString()); Environment.ExitCode=failures==0 ? 0 : 1;
    }
}
