using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    internal sealed class BoardButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if(Enabled) {base.OnPaint(e); return;}
            var theme=ThemeManager.Current;
            e.Graphics.Clear(theme.Header);
            using(var pen=new Pen(theme.Border)) e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,theme.Muted,
                TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
        }
    }
    internal sealed class BoardTask
    {
        public string Id, Parent, Title, Directory;
        public bool Running;
        public long Updated;
        public string[] Todos;
        public static string Text(IDictionary<string, object> row, string key)
        { object value; return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
        public static IDictionary<string, object> Object(IDictionary<string, object> row, string key)
        { object value; return row.TryGetValue(key, out value) && value is IDictionary<string, object> ? (IDictionary<string, object>)value : new Dictionary<string, object>(); }
        public static List<BoardTask> Parse(string json, string rpcId)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var envelope = serializer.Deserialize<Dictionary<string, object>>(json);
            if(Text(envelope, "rpcId") != rpcId || Text(envelope, "type") != "server-response") throw new InvalidDataException("接口响应不匹配");
            var result = Object(envelope, "result");
            if(Text(result, "ok") != "True") throw new InvalidDataException("DSH 未能提供任务列表");
            var value = Object(result, "value");
            object rows;
            if(!value.TryGetValue("items", out rows) || !(rows is System.Collections.IList)) throw new InvalidDataException("任务列表格式已变化");
            var tasks = new List<BoardTask>(); var seen = new HashSet<string>();
            foreach(var raw in (System.Collections.IList)rows)
            {
                var row = raw as IDictionary<string, object>; if(row == null) continue;
                string id = Text(row,"sessionId"); if(id.Length == 0 || !seen.Add(id)) continue;
                var projections = Object(Object(row,"projections"),"values");
                string title = Text(projections,"title");
                var todos = new List<string>(); object todoRaw;
                if(projections.TryGetValue("todos",out todoRaw) && todoRaw is System.Collections.IList)
                    foreach(var item in (System.Collections.IList)todoRaw)
                    {
                        var todo = item as IDictionary<string, object>; if(todo == null) continue;
                        string status = Text(todo,"status");
                        todos.Add((status == "completed" ? "✓ " : status == "in_progress" ? "进行中 · " : "待办 · ") + Text(todo,"content"));
                    }
                long updated; long.TryParse(Text(row,"updatedAt"),out updated);
                tasks.Add(new BoardTask { Id=id, Parent=Text(row,"parentSessionId"), Title=title.Length == 0 ? "未命名会话" : title,
                    Directory=Text(row,"cwd"), Running=Text(row,"running") == "True", Updated=updated, Todos=todos.ToArray() });
            }
            return tasks;
        }
        public static HashSet<string> Filter(List<BoardTask> tasks, string search, bool running)
        {
            var byId = tasks.ToDictionary(t=>t.Id);
            var selected = new HashSet<string>(tasks.Where(t=>(!running || t.Running) &&
                (search.Length == 0 || (t.Title + " " + t.Directory + " " + t.Id).IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0)).Select(t=>t.Id));
            foreach(string id in selected.ToArray())
            {
                var visited = new HashSet<string>(); BoardTask task=byId[id];
                while(task.Parent.Length > 0 && visited.Add(task.Parent) && byId.TryGetValue(task.Parent,out task)) selected.Add(task.Id);
            }
            return selected;
        }
        public string UpdatedText
        {
            get { try { return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(Updated).ToLocalTime().ToString("MM-dd HH:mm"); } catch { return "未知时间"; } }
        }
    }

    public sealed class TaskBoardForm : Form
    {
        private readonly DshController controller;
        private readonly Label summary, notice;
        private readonly TextBox search, details;
        private readonly CheckBox running, grouping, showHidden;
        private readonly TreeView tree;
        private readonly Button refresh, open, rename, fork, cancel, hide;
        private readonly HiddenSessions hidden;
        private readonly System.Windows.Forms.Timer timer;
        private List<BoardTask> tasks = new List<BoardTask>();
        private bool busy, fresh, allowClose, managing;
        private bool renderedTasks;
        private DateTime lastUpdate;
        public TaskBoardForm(DshController value)
            : this(value,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WSLKeepAliveTray","hidden-sessions.json")) { }
        internal TaskBoardForm(DshController value,string hiddenPath)
        {
            SuspendLayout(); controller=value; hidden=new HiddenSessions(hiddenPath);
            AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96);
            Font=new Font("Microsoft YaHei UI",9f);
            Text="深林印象 · DSH 任务看板"; ClientSize=new Size(1000,680); MinimumSize=new Size(820,560);
            StartPosition=FormStartPosition.CenterScreen;
            Icon=IconFactory.Create(TrayHealthState.Healthy);
            var root=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=5, Padding=new Padding(18) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,56));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            summary=new Label { Dock=DockStyle.Fill, AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft,
                Text="DSH 任务看板", Font=new Font("Microsoft YaHei UI",14f,FontStyle.Bold) };
            var filters=new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, Margin=Padding.Empty };
            search=new TextBox { Width=210, AccessibleName="搜索任务或目录" };
            running=new CheckBox { Text="仅运行中", AutoSize=true, Margin=new Padding(12,5,8,0) };
            grouping=new CheckBox { Text="按目录分组", Checked=true, AutoSize=true, Margin=new Padding(4,5,8,0) };
            refresh=new BoardButton { Text="刷新", Width=70, Height=30, FlatStyle=FlatStyle.Flat };
            open=new BoardButton { Text="打开 DSH", Width=100, Height=30, FlatStyle=FlatStyle.Flat };
            filters.Controls.AddRange(new Control[] {new Label { Text="搜索", AutoSize=true, Margin=new Padding(0,5,6,0) },search,running,grouping,refresh,open});
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, Margin=Padding.Empty };
            rename=new BoardButton { Text="重命名", Width=85, Height=30, FlatStyle=FlatStyle.Flat };
            fork=new BoardButton { Text="复制分支", Width=100, Height=30, FlatStyle=FlatStyle.Flat };
            cancel=new BoardButton { Text="中止本轮", Width=100, Height=30, FlatStyle=FlatStyle.Flat };
            hide=new BoardButton { Text="在看板隐藏", Width=120, Height=30, FlatStyle=FlatStyle.Flat };
            showHidden=new CheckBox {Text="显示隐藏", AutoSize=true, Margin=new Padding(12,5,8,0)};
            actions.Controls.AddRange(new Control[] {rename,fork,cancel,hide,showHidden});
            var panes=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Margin=Padding.Empty };
            panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60)); panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
            panes.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            tree=new TreeView { Dock=DockStyle.Fill, HideSelection=false, FullRowSelect=true, ShowNodeToolTips=true, BorderStyle=BorderStyle.FixedSingle };
            details=new TextBox { Dock=DockStyle.Fill, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, BorderStyle=BorderStyle.FixedSingle,
                Text="选择左侧会话查看详情。\r\n\r\n空闲表示当前没有生成，不代表任务已完成。" };
            notice=new Label { Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleLeft, AutoEllipsis=true, Text="等待 DSH 状态…" };
            panes.Controls.Add(tree,0,0); panes.Controls.Add(details,1,0);
            root.Controls.Add(summary,0,0); root.Controls.Add(filters,0,1); root.Controls.Add(actions,0,2); root.Controls.Add(panes,0,3); root.Controls.Add(notice,0,4);
            Controls.Add(root);
            search.TextChanged+=delegate { Render(); }; running.CheckedChanged+=delegate { Render(); }; grouping.CheckedChanged+=delegate { Render(); };
            tree.AfterSelect+=delegate { ShowDetails(); };
            refresh.Click+=async delegate { await RefreshTasks(); };
            open.Click+=delegate { controller.OpenWeb(); };
            showHidden.CheckedChanged+=delegate { Render(); };
            rename.Click+=async delegate { await Manage("session/rename"); };
            fork.Click+=async delegate { await Manage("session/fork"); };
            cancel.Click+=async delegate { await Manage("session/cancel"); };
            hide.Click+=delegate { ToggleHidden(); };
            timer=new System.Windows.Forms.Timer { Interval=5000 };
            timer.Tick+=async delegate { if(Visible) await RefreshTasks(); };
            VisibleChanged+=async delegate { if(Visible) { timer.Start(); await RefreshTasks(); } else timer.Stop(); };
            FormClosing+=delegate(object sender,FormClosingEventArgs args) { if(!allowClose) { args.Cancel=true; Hide(); } };
            ThemeManager.Changed+=ThemeChanged;
            ResumeLayout(true); ApplyTheme(); UpdateActions();
        }
        private bool Available { get { return controller.Fresh && controller.Snapshot.DshWebReady && !controller.Busy; } }
        private async Task RefreshTasks()
        {
            if(busy || managing || IsDisposed) return;
            if(!Available)
            {
                fresh=false; notice.Text=controller.Status + "；保留上次列表，不自动启动服务。";
                open.Enabled=false; Render(); return;
            }
            busy=true; UpdateActions(); notice.Text="正在读取任务…";
            try
            {
                var loaded=await Task.Run(()=>Fetch());
                if(IsDisposed || !Visible) return;
                if(!Available) { fresh=false; notice.Text="DSH 状态已变化；列表未更新。"; Render(); return; }
                tasks=loaded; lastUpdate=DateTime.Now; fresh=true;
                notice.Text=hidden.Error ?? ("更新于 " + lastUpdate.ToString("HH:mm:ss") + " · 每 5 秒刷新 · 空闲不等于完成");
                Render();
            }
            catch(Exception)
            {
                if(!IsDisposed) { fresh=false; notice.Text="读取失败，保留上次列表；请检查 DSH，稍后自动重试。"; Render(); }
            }
            finally { busy=false; if(!IsDisposed) UpdateActions(); }
        }
        internal static List<BoardTask> Fetch()
        {
            string id=Guid.NewGuid().ToString();
            return BoardTask.Parse(BoardApi.Send("session/list",BoardApi.Request("session/list",new {},id)),id);
        }
        private void Render()
        {
            if(tree==null) return;
            string selected=tree.SelectedNode==null ? "" : tree.SelectedNode.Name;
            string top=tree.TopNode==null ? "" : tree.TopNode.Name;
            var expanded=new HashSet<string>(); CollectExpanded(tree.Nodes,expanded);
            var hiddenIds=hidden.Effective(tasks);
            var eligible=tasks.Where(t=>showHidden.Checked || !hiddenIds.Contains(t.Id)).ToList();
            var visible=BoardTask.Filter(eligible,search.Text.Trim(),running.Checked);
            var candidates=tasks.Where(t=>visible.Contains(t.Id)).OrderByDescending(t=>t.Running).ThenByDescending(t=>t.Updated).ToList();
            var emitted=new HashSet<string>(); var groups=new Dictionary<string,TreeNode>();
            tree.BeginUpdate(); tree.Nodes.Clear();
            foreach(var task in candidates.Where(t=>!visible.Contains(t.Parent))) AddRoot(task,candidates,emitted,groups);
            // Defensive cycle/orphan handling: every matching row stays reachable.
            foreach(var task in candidates) if(!emitted.Contains(task.Id)) AddRoot(task,candidates,emitted,groups);
            Restore(tree.Nodes,expanded,selected);
            var topNodes=tree.Nodes.Find(top,true);
            if(topNodes.Length>0) tree.TopNode=topNodes[0];
            if(!renderedTasks && candidates.Count>0)
            {
                foreach(TreeNode node in tree.Nodes) if(node.Tag==null) node.Expand();
                renderedTasks=true;
            }
            tree.EndUpdate();
            summary.Text="DSH 任务看板  ·  " + tasks.Count + " 个会话  ·  " + (fresh ? tasks.Count(t=>t.Running) + " 个运行中" : "状态未更新") + (hiddenIds.Count>0 ? "  ·  已隐藏 "+tasks.Count(t=>hiddenIds.Contains(t.Id)) : "");
            if(candidates.Count==0) details.Text=tasks.Count==0 ? "暂未读取到会话。" : "没有匹配的任务。";
            else ShowDetails();
            UpdateActions();
        }
        private void AddRoot(BoardTask task,List<BoardTask> candidates,HashSet<string> emitted,Dictionary<string,TreeNode> groups)
        {
            TreeNodeCollection target=tree.Nodes;
            if(grouping.Checked)
            {
                string directory=task.Directory.Length==0 ? "未指定目录" : task.Directory;
                TreeNode group;
                if(!groups.TryGetValue(directory,out group)) { group=new TreeNode(directory) { Name="dir:"+directory, ToolTipText=directory }; groups.Add(directory,group); tree.Nodes.Add(group); }
                target=group.Nodes;
            }
            AddTask(task,target,candidates,emitted);
        }
        private void AddTask(BoardTask task,TreeNodeCollection nodes,List<BoardTask> candidates,HashSet<string> emitted)
        {
            if(!emitted.Add(task.Id)) return;
            var node=new TreeNode((hidden.Effective(tasks).Contains(task.Id) ? "[已隐藏] " : "")+(fresh ? task.Running ? "● 运行中  " : "○ 空闲  " : "○ 未更新  ") + task.Title + "  ·  " + task.UpdatedText)
                { Name=task.Id, Tag=task, ToolTipText=task.Title+"\n"+task.Directory };
            nodes.Add(node);
            foreach(var child in candidates.Where(t=>t.Parent==task.Id)) AddTask(child,node.Nodes,candidates,emitted);
        }
        private static void CollectExpanded(TreeNodeCollection nodes,HashSet<string> expanded)
        { foreach(TreeNode node in nodes) { if(node.IsExpanded) expanded.Add(node.Name); CollectExpanded(node.Nodes,expanded); } }
        private void Restore(TreeNodeCollection nodes,HashSet<string> expanded,string selected)
        { foreach(TreeNode node in nodes) { if(expanded.Contains(node.Name)) node.Expand(); if(node.Name==selected) tree.SelectedNode=node; Restore(node.Nodes,expanded,selected); } }
        private void ShowDetails()
        {
            UpdateActions();
            var task=tree.SelectedNode==null ? null : tree.SelectedNode.Tag as BoardTask;
            if(task==null) { details.Text="选择会话查看详情。\r\n\r\n支持按标题或目录搜索。子任务可通过左侧箭头展开。\r\n\r\n空闲表示当前没有生成，不代表任务已完成。"; return; }
            details.Text=task.Title+"\r\n\r\n状态："+(fresh ? task.Running ? "运行中" : "空闲" : "未更新（上次读取的数据）")+
                "\r\n最近更新："+task.UpdatedText+"\r\n目录："+task.Directory+"\r\n会话 ID："+task.Id+
                (task.Parent.Length>0 ? "\r\n父会话："+task.Parent : "")+"\r\n\r\n待办步骤\r\n"+
                (task.Todos.Length==0 ? "此会话未提供待办步骤。" : string.Join("\r\n",task.Todos));
        }
        private BoardTask Selected {get {return tree.SelectedNode==null ? null : tree.SelectedNode.Tag as BoardTask;}}
        private bool CanManage {get {return !busy && !managing && fresh && Available && (DateTime.Now-lastUpdate).TotalSeconds<20;}}
        private void UpdateActions()
        {
            var task=Selected;
            rename.Enabled=fork.Enabled=CanManage && task!=null;
            cancel.Enabled=CanManage && task!=null && task.Running;
            hide.Enabled=!busy && !managing && task!=null && hidden.Error==null;
            hide.Text=task!=null && hidden.Effective(tasks).Contains(task.Id) ? "恢复到看板" : "在看板隐藏";
            refresh.Enabled=!busy && !managing; open.Enabled=Available;
        }
        private async Task Manage(string method)
        {
            var task=Selected;
            if(!CanManage || task==null || (method=="session/cancel" && !task.Running)) return;
            managing=true; UpdateActions(); string title=null, selectedId=task.Id, message=null;
            try
            {
                if(method=="session/rename")
                {
                    title=AskTitle(task.Title);
                    if(title==null || title==task.Title) return;
                }
                else
                {
                    string question=method=="session/fork" ? "从“"+task.Title+"”已完成的轮次复制新分支？\r\n原会话保留，新分支不会自动发送消息。" :
                        "中止“"+task.Title+"”当前正在执行的轮次？\r\n排队的消息仍保留，DSH 服务继续运行。";
                    if(MessageBox.Show(this,question,method=="session/fork" ? "复制会话分支" : "中止本轮",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK) return;
                }
                if(!Available) throw new InvalidDataException("DSH 当前不可用，请刷新后重试。");
                notice.Text="正在处理所选会话…";
                var result=await Task.Run(()=>BoardApi.Manage(method,task.Id,title,BoardApi.Send));
                if(method=="session/fork") selectedId=BoardTask.Text(result,"sessionId");
                message=method=="session/rename" ? "会话已重命名。" : method=="session/fork" ? "新分支已创建。" : "已提交中止本轮请求；排队消息仍保留。";
            }
            catch(InvalidDataException ex) { message=ex.Message; }
            catch(Exception) { message="操作结果未确认，请先刷新列表或在 DSH 中检查，避免重复操作。"; }
            finally
            {
                managing=false;
                if(!IsDisposed) UpdateActions();
            }
                if(!IsDisposed)
                {
                    if(message!=null)
                    {
                        await RefreshTasks();
                        if(!IsDisposed)
                        {
                            if(method=="session/fork" && selectedId!=task.Id) {search.Clear(); running.Checked=false;}
                            var nodes=tree.Nodes.Find(selectedId,true);
                            if(nodes.Length>0) {tree.SelectedNode=nodes[0]; nodes[0].EnsureVisible();}
                            notice.Text=message+(fresh ? "" : " 列表尚未刷新成功。");
                            if(Visible) MessageBox.Show(this,notice.Text,"会话管理",MessageBoxButtons.OK,MessageBoxIcon.Information);
                        }
                    }
                    if(!IsDisposed) UpdateActions();
                }
        }
        private string AskTitle(string current)
        {
            using(var dialog=new Form())
            {
                dialog.SuspendLayout(); dialog.AutoScaleMode=AutoScaleMode.Dpi; dialog.AutoScaleDimensions=new SizeF(96,96);
                dialog.Font=Font; dialog.Text="重命名会话"; dialog.ClientSize=new Size(480,150);
                dialog.FormBorderStyle=FormBorderStyle.FixedDialog; dialog.StartPosition=FormStartPosition.CenterParent;
                dialog.MinimizeBox=false; dialog.MaximizeBox=false; dialog.ShowInTaskbar=false;
                var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(18),RowCount=3,ColumnCount=1};
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
                layout.Controls.Add(new Label {Text="会话名称",AutoSize=true},0,0);
                var input=new TextBox {Text=current,Dock=DockStyle.Top}; layout.Controls.Add(input,0,1);
                var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
                var ok=new BoardButton {Text="保存",DialogResult=DialogResult.OK,Width=80,Height=30};
                var back=new BoardButton {Text="取消",DialogResult=DialogResult.Cancel,Width=80,Height=30};
                buttons.Controls.Add(back); buttons.Controls.Add(ok); layout.Controls.Add(buttons,0,2); dialog.Controls.Add(layout);
                dialog.AcceptButton=ok; dialog.CancelButton=back; input.TextChanged+=delegate {ok.Enabled=!string.IsNullOrWhiteSpace(input.Text);};
                var theme=ThemeManager.Current; dialog.BackColor=theme.Background; dialog.ForeColor=theme.Ink;
                input.BackColor=theme.Surface; input.ForeColor=theme.Ink;
                foreach(var b in new[]{ok,back}) {b.FlatStyle=FlatStyle.Flat; b.BackColor=theme.Header; b.ForeColor=theme.Ink; b.FlatAppearance.BorderColor=theme.Border;}
                dialog.ResumeLayout(true); dialog.Shown+=delegate {input.Focus(); input.SelectAll();};
                return dialog.ShowDialog(this)==DialogResult.OK ? input.Text.Trim() : null;
            }
        }
        private void ToggleHidden()
        {
            var task=Selected; if(task==null || !hide.Enabled) return;
            managing=true; UpdateActions();
            try
            {
                bool hiding=!hidden.Effective(tasks).Contains(task.Id);
                if(hiding && MessageBox.Show(this,"仅在此看板隐藏“"+task.Title+"”及其子会话？\r\n聊天记录保留，运行中的会话继续执行。可勾选“显示隐藏”后恢复。",
                    "隐藏会话",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK) return;
                hidden.Set(tasks,task.Id,hiding); Render();
                notice.Text=hiding ? "已在本看板隐藏；勾选“显示隐藏”后可恢复。" : "已恢复到看板。";
            }
            catch(Exception) {MessageBox.Show(this,"无法保存隐藏记录，列表未改动。","保存失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            finally {managing=false; UpdateActions();}
        }
        private void ThemeChanged(object sender,EventArgs args) { ApplyTheme(); }
        private void ApplyTheme()
        {
            Theme theme=ThemeManager.Current; BackColor=theme.Background; ForeColor=theme.Ink;
            tree.BackColor=details.BackColor=search.BackColor=theme.Surface;
            tree.ForeColor=details.ForeColor=search.ForeColor=theme.Ink;
            tree.LineColor=theme.Border; notice.ForeColor=theme.Muted;
            foreach(var button in new[] {refresh,open,rename,fork,cancel,hide}) { button.BackColor=theme.Header; button.ForeColor=theme.Ink; button.FlatAppearance.BorderColor=theme.Border; }
        }
        public void ShowBoard() { if(!Visible) Show(); if(WindowState==FormWindowState.Minimized) WindowState=FormWindowState.Normal; Activate(); BringToFront(); }
        public void ExitBoard() { allowClose=true; Close(); }
        protected override void Dispose(bool disposing)
        { if(disposing) { timer.Dispose(); ThemeManager.Changed-=ThemeChanged; if(Icon!=null) Icon.Dispose(); } base.Dispose(disposing); }
    }
}
