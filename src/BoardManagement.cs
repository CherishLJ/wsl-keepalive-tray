using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace WSLKeepAliveTray
{
    internal static class BoardApi
    {
        internal static string Request(string method, object request, string id)
        {
            if(!new[] {"session/list","session/rename","session/fork","session/cancel"}.Contains(method))
                throw new ArgumentException("不支持的会话操作");
            var args=new Dictionary<string,object> {{method=="session/list" ? "_request" : "request",request}};
            return new JavaScriptSerializer().Serialize(new { type="client-request", rpcId=id, method=method, payload=new {args=args} });
        }
        internal static string Send(string method,string data)
        {
            byte[] body=Encoding.UTF8.GetBytes(data);
            var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:3080/api/"+method);
            request.Proxy=null; request.Method="POST"; request.ContentType="application/json";
            request.Headers["Origin"]="http://127.0.0.1:3080";
            request.Timeout=8000; request.ReadWriteTimeout=8000; request.ContentLength=body.Length;
            using(var stream=request.GetRequestStream()) stream.Write(body,0,body.Length);
            using(var response=request.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))
            {
                var result=new StringBuilder(); var buffer=new char[4096]; int count;
                while((count=reader.Read(buffer,0,buffer.Length))>0)
                { if(result.Length+count>8*1024*1024) throw new InvalidDataException("接口响应过大"); result.Append(buffer,0,count); }
                return result.ToString();
            }
        }
        internal static IDictionary<string,object> Manage(string method,string sessionId,string title,Func<string,string,string> transport)
        {
            if(method=="session/list" || string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("请先选择会话");
            var request=new Dictionary<string,object> {{"sessionId",sessionId}};
            if(method=="session/rename")
            {
                if(string.IsNullOrWhiteSpace(title)) throw new ArgumentException("名称不能为空");
                request.Add("title",title.Trim());
            }
            string id=Guid.NewGuid().ToString();
            string data=transport(method,Request(method,request,id)); // Never retry a mutation automatically.
            var envelope=new JavaScriptSerializer {MaxJsonLength=8*1024*1024}.Deserialize<Dictionary<string,object>>(data);
            if(envelope==null || BoardTask.Text(envelope,"rpcId")!=id || BoardTask.Text(envelope,"type")!="server-response")
                throw new InvalidDataException("响应不匹配，操作结果未确认，请刷新检查。");
            var result=BoardTask.Object(envelope,"result");
            if(BoardTask.Text(result,"ok")!="True") throw new InvalidDataException("DSH 未接受此操作，请刷新后检查会话状态。");
            var value=BoardTask.Object(result,"value");
            if(method=="session/fork" && BoardTask.Text(value,"sessionId").Length==0)
                throw new InvalidDataException("复制结果未确认，请刷新列表检查，避免重复复制。");
            return value;
        }
    }
    internal sealed class HiddenSessions
    {
        private readonly string path;
        internal HashSet<string> Ids {get; private set;}
        internal string Error {get; private set;}
        internal HiddenSessions(string file)
        {
            path=file; Ids=new HashSet<string>();
            try { if(File.Exists(path)) Ids=new HashSet<string>(new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(path)) ?? new string[0]); }
            catch { Error="隐藏记录读取失败；为保留原记录，暂时禁用隐藏操作。"; }
        }
        internal static HashSet<string> Branch(List<BoardTask> tasks,IEnumerable<string> roots)
        {
            var result=new HashSet<string>(roots); bool added;
            do { added=false; foreach(var t in tasks) if(result.Contains(t.Parent) && result.Add(t.Id)) added=true; } while(added);
            return result;
        }
        internal HashSet<string> Effective(List<BoardTask> tasks) { return Branch(tasks,Ids); }
        internal void Set(List<BoardTask> tasks,string id,bool hide)
        {
            if(Error!=null) throw new IOException(Error);
            var next=new HashSet<string>(Ids); var branch=Branch(tasks,new[]{id});
            if(hide) next.UnionWith(branch);
            else
            {
                next.ExceptWith(branch);
                // A restored child must also become reachable under a hidden ancestor.
                var byId=tasks.ToDictionary(item=>item.Id); var seen=new HashSet<string>(); BoardTask t;
                while(seen.Add(id) && byId.TryGetValue(id,out t)) { next.Remove(id); id=t.Parent; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(next.OrderBy(x=>x).ToArray()),new UTF8Encoding(false));
                if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
                Ids=next;
            }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
    }
}
