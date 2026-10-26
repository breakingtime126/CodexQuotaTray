using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;

var mode = args.ElementAtOrDefault(0) ?? "idle";
var count = int.Parse(args.ElementAtOrDefault(1) ?? "24");
var mib = int.Parse(args.ElementAtOrDefault(2) ?? "0");
var iterations = int.Parse(args.ElementAtOrDefault(3) ?? "2000");
var home = Path.Combine(Path.GetTempPath(), "cqt-perf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(home, "sessions"));
Directory.CreateDirectory(Path.Combine(home, "archived_sessions"));
var paths = new List<string>();
var epoch = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
string Row(int second, string type, object payload) => JsonSerializer.Serialize(new {timestamp=epoch.AddSeconds(second),type,payload})+"\n";
string Header(string id) => Row(0,"session_meta",new {id});
string Start(string id,string turn,int second) => Row(second,"event_msg",new {type="task_started",thread_id=id,turn_id=turn})+Row(second,"turn_context",new {turn_id=turn,model="gpt-6.1-sol",effort="medium"});
string End(string id,string turn,int second) => Row(second,"token_usage_record",new {thread_id=id,turn_id=turn,response_id=turn,model="gpt-6.1-sol",usage=new {output_tokens=100}})+Row(second+1,"event_msg",new {type="task_complete",thread_id=id,turn_id=turn});
var filler = Encoding.UTF8.GetBytes(Row(1,"response_item",new {type="function_call_output",call_id="tool",output=new string('x',8000)}));
var ids = new List<string>();
try {
  for(int i=0;i<count;i++) {
    var id=Guid.NewGuid().ToString("D"); ids.Add(id);
    var path=Path.Combine(home,"sessions","rollout-2026-10-02-"+id+".jsonl"); paths.Add(path);
    using var fs=new FileStream(path,FileMode.Create,FileAccess.Write);
    var head=Encoding.UTF8.GetBytes(Header(id)+Start(id,"first",0)); fs.Write(head);
    while(fs.Length < (long)mib*1024*1024) fs.Write(filler);
    var end=Encoding.UTF8.GetBytes(End(id,"first",10)); fs.Write(end);
  }
  // Warm JIT on separate tiny fixture so the measured cold pass is not JIT startup.
  var warm=Path.Combine(home,"warm"); Directory.CreateDirectory(Path.Combine(warm,"sessions"));
  var warmid=Guid.NewGuid().ToString("D"); File.WriteAllText(Path.Combine(warm,"sessions","rollout-"+warmid+".jsonl"),Header(warmid)+Start(warmid,"warm",0)+End(warmid,"warm",10));
  using(var wr=new RolloutSpeedReader(warm)) for(var i=0;i<10;i++) await wr.PollAsync();
  var options=new RolloutSpeedReaderOptions { DiscoveryInterval = mode=="discovery" ? TimeSpan.Zero : TimeSpan.FromHours(1) };
  using var reader = new RolloutSpeedReader(home,options);
  RolloutSpeedReadResult? result=null;
  if(mode is "idle" or "append" or "discovery") {
    do { result=await reader.PollAsync(); } while(result.CanCatchUp);
    for(int i=0;i<20;i++) result=await reader.PollAsync();
  }
  GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
  using var process=Process.GetCurrentProcess();
  var cpu=process.TotalProcessorTime; var allocated=GC.GetTotalAllocatedBytes(true); var live=GC.GetTotalMemory(true);
  var ioBefore=ReadIo(); var sw=Stopwatch.StartNew();long bytes=0, entries=0, dirs=0;int polls=0;double maxPoll=0;
  do {
    if(mode=="append") File.AppendAllText(paths[0],Start(ids[0],"new-"+polls,20)+End(ids[0],"new-"+polls,30));
    var ps=Stopwatch.StartNew(); result=await reader.PollAsync(); maxPoll=Math.Max(maxPoll,ps.Elapsed.TotalMilliseconds);
    bytes+=result.BytesRead;entries+=result.DiscoveryEntriesVisited;dirs+=result.DiscoveryDirectoriesVisited;polls++;
  } while(mode=="cold" ? result.CanCatchUp && polls<20000 : polls<iterations);
  sw.Stop();var ioAfter=ReadIo();process.Refresh();
  var resultOutput=new {mode,count,mib,polls,wall_ms=sw.Elapsed.TotalMilliseconds,cpu_ms=(process.TotalProcessorTime-cpu).TotalMilliseconds,allocated_bytes=GC.GetTotalAllocatedBytes(true)-allocated,live_before=live,live_after=GC.GetTotalMemory(true),working_set_bytes=process.WorkingSet64,peak_working_set_bytes=process.PeakWorkingSet64,logical_read_bytes=bytes,discovery_entries=entries,discovery_dirs=dirs,max_poll_ms=maxPoll,kernel_rchar=Delta("rchar"),kernel_read_bytes=Delta("read_bytes"),kernel_write_bytes=Delta("write_bytes"),status=result.Status.ToString(),can_catch_up=result.CanCatchUp,history_limited=result.IsHistoryLimited,model_status=result.ModelMetadataStatus.ToString(),response=result.Sample?.ResponseId,tps=result.Sample?.TokensPerSecond};
  Console.WriteLine(JsonSerializer.Serialize(resultOutput));
  long Delta(string k)=>ioAfter.GetValueOrDefault(k)-ioBefore.GetValueOrDefault(k);
} finally { Directory.Delete(home,true); }
static Dictionary<string,long> ReadIo() => File.ReadAllLines("/proc/self/io").Select(l=>l.Split(':')).ToDictionary(p=>p[0],p=>long.Parse(p[1]));
