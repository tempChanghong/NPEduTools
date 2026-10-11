using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunStudyHomeworkPipeChecks(bool replacement)
    {
        string pipe = "NPEduTools.Test.study-homework." + Guid.NewGuid().ToString("N");
        var state = StudyFixture(); string content = string.Concat(Enumerable.Repeat("🙂保留完整作业\n",1200));
        var snapshot = new StudyHomeworkSnapshot(state.Version!,"2026-10-12",[new("pipe-work",1,"物理","",content,[new("one","行政一班")])]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot,Protocol.Json);
        string id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));int count=(bytes.Length+16383)/16384;
        state = state with { Homework=new("READY",id,count,snapshot.Date,snapshot.Version) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var finalEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delivered=0;
        var server=Task.Run(async () =>
        {
            for(int index=0;index<count;index++)
            {
                using var stream=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(timeout.Token);var request=await Protocol.ReadAsync<HostRequest>(stream,timeout.Token);
                Assert(request.Capability=="study.homework" && Protocol.Validate(request) is null,"App homework read changed another capability");
                if(index==count-1 && replacement){finalEntered.SetResult();await release.Task.WaitAsync(timeout.Token);}
                var response=new HostResponse(1,request.RequestId,"Succeeded",null,"fixture",HomeworkChunk:new(id,index,count,
                    Convert.ToBase64String(bytes,index*16384,Math.Min(16384,bytes.Length-index*16384))));
                try { await Protocol.WriteAsync(stream,response,timeout.Token); delivered++; }
                catch(IOException) when(replacement) { return; }
            }
        });
        var window=new StudyWindow(pipe,()=>{},true) { Width=1100,Height=700 };
        try
        {
            Exercise(window,_=>
            {
                window.Apply(state,null);
                var surface=(StudySurface)window.Content;
                var grid=(Grid)surface.FindName("HomeworkGrid");
                if(replacement)
                {
                    PumpUntil(()=>finalEntered.Task.IsCompleted,"final homework chunk did not enter");
                    state=state with { Version=new string('b',64),Homework=new("UNAVAILABLE",null,0,"2026-10-12",new string('b',64)) };
                    window.Apply(state,null);release.SetResult();PumpUntil(()=>server.IsCompleted,"cancelled old homework server did not settle");
                    Assert(!grid.Children.OfType<StackPanel>().Any(),"late old homework crossed a policy change");
                }
                else
                {
                    PumpUntil(()=>grid.Children.OfType<StackPanel>().Any(),"actual Named Pipe homework did not reach WPF");
                    PumpUntil(()=>server.IsCompleted,"homework pipe server did not settle");
                    Assert(delivered==count && count>1,"complete homework did not use bounded IPC chunks");
                    Snapshot(window,"study-homework-pipe.png");
                    using var process=System.Diagnostics.Process.GetCurrentProcess();process.Refresh();
                    File.WriteAllText(Path.Combine(_output,"homework-memory-fixture.json"),JsonSerializer.Serialize(new {
                        workingSetBytes=process.WorkingSet64,privateBytes=process.PrivateMemorySize64,managedHeapBytes=GC.GetTotalMemory(false),
                        textCharacters=content.Length,chunks=count,kind="isolated-WPF-after-large-homework-and-screenshots",fullApp=false,realDevices=false }));
                }
                server.GetAwaiter().GetResult();Checks.Add($"Study homework pipe: actual multi-chunk Named Pipe into actual WPF; replacement={replacement}; no Host/sensor startup");
                window.Shutdown();
            });
        }
        finally { release.TrySetResult();timeout.Cancel();window.Shutdown(); }
    }
}
