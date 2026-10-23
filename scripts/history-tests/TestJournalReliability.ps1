$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs'))
$start = $source.IndexOf('        private static async Task WorkerLoop(CancellationToken token)')
$end = $source.IndexOf('        private static void FlushAudit()', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Production journal methods were not found.' }
$journal = $source.Substring($start, $end - $start)
$fixtures = @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace HistoryJournalTests {
class ElementHistoryEvent { internal int Id; }
enum Formatting { None }
static class JsonConvert {
    internal static bool FailOnce;
    internal static string SerializeObject(ElementHistoryEvent ev, Formatting _) {
        if (FailOnce) { FailOnce=false; throw new InvalidOperationException("Injected serialization failure"); }
        return "{\"id\":" + ev.Id + "}";
    }
}
static class CollaborativeModelTrackerStore { internal static string ActiveDirectory; }
// Real file IO with an injected short write. Only the filesystem/serializer are
// doubles; queue removal, rollback and logger recovery execute production code.
sealed class FileStream : IDisposable {
    readonly System.IO.FileStream stream;
    internal static bool FailWriteOnce;
    internal static bool FailFlushOnce;
    internal FileStream(string path, FileMode mode, FileAccess access, FileShare share) {
        stream = new System.IO.FileStream(path,mode,access,share);
    }
    internal long Length { get { return stream.Length; } }
    internal long Position { set { stream.Position=value; } }
    internal int ReadByte() { return stream.ReadByte(); }
    internal void WriteByte(byte value) { stream.WriteByte(value); }
    internal void Write(byte[] bytes, int offset, int count) {
        if (FailWriteOnce) {
            FailWriteOnce=false; stream.Write(bytes,offset,Math.Max(1,count/2));
            throw new IOException("Injected partial write");
        }
        stream.Write(bytes,offset,count);
    }
    internal void Flush(bool durable) {
        if (FailFlushOnce) { FailFlushOnce=false; throw new IOException("Injected flush failure"); }
        stream.Flush(durable);
    }
    internal void SetLength(long length) { stream.SetLength(length); }
    public void Dispose() { stream.Dispose(); }
}
public static class JournalChecks {
    static readonly ConcurrentQueue<ElementHistoryEvent> Queue = new ConcurrentQueue<ElementHistoryEvent>();
    static readonly object FileSync = new object();
    static readonly TimeSpan HistoryMaintenanceInitialDelay = TimeSpan.Zero;
    static readonly TimeSpan HistoryMaintenanceInterval = TimeSpan.FromHours(1);
    static int AuditFailures, MaintenanceFailures;
    static void FlushAudit() {
        if (Interlocked.Exchange(ref AuditFailures,0)>0) throw new IOException("Injected audit failure");
    }
    static void CompressOldFiles() {
        if (Interlocked.Exchange(ref MaintenanceFailures,0)>0) throw new IOException("Injected maintenance failure");
    }
'@
$checks = @'
    static void Check(bool ok,string message) { if (!ok) throw new Exception(message); }
    static string PathForToday() {
        return Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"element-history-"+DateTime.UtcNow.ToString("yyyy-MM-dd")+".jsonl");
    }
    static void Enqueue(int count, int start=0) {
        for(int index=0;index<count;index++) Queue.Enqueue(new ElementHistoryEvent { Id=start+index });
    }
    static void CheckIds(int count) {
        var lines=File.ReadAllLines(PathForToday());
        Check(lines.Length==count,"Each event must be written exactly once.");
        for(int index=0;index<count;index++) Check(lines[index]=="{\"id\":"+index+"}","FIFO order was lost at "+index);
    }
    static void ExpectFailure(Action action) {
        bool failed=false; try { action(); } catch(IOException) { failed=true; }
        Check(failed,"Injected IO failure was not exercised.");
    }
    public static void Run(string directory) {
        CollaborativeModelTrackerStore.ActiveDirectory=directory;
        Directory.CreateDirectory(directory);
        Enqueue(2505); Flush();
        Check(Queue.IsEmpty,"A single explicit flush must drain more than the former 200-event limit.");
        CheckIds(2505);

        Enqueue(45,2505);
        using(var held=new System.IO.FileStream(PathForToday(),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            ExpectFailure(Flush);
        Check(Queue.Count==45,"Opening a locked file discarded queued deletions.");
        Flush(); CheckIds(2550);

        Enqueue(43,2550);
        var before=File.ReadAllBytes(PathForToday());
        FileStream.FailWriteOnce=true; ExpectFailure(Flush);
        Check(Queue.Count==43,"A short write discarded queued deletions.");
        Check(before.SequenceEqual(File.ReadAllBytes(PathForToday())),"Partial append was not rolled back.");
        Flush(); CheckIds(2593);

        Enqueue(7,2593); before=File.ReadAllBytes(PathForToday());
        FileStream.FailFlushOnce=true; ExpectFailure(Flush);
        Check(Queue.Count==7 && before.SequenceEqual(File.ReadAllBytes(PathForToday())),"Failed durable flush was committed.");
        Flush(); CheckIds(2600);

        Enqueue(5,2600); JsonConvert.FailOnce=true;
        bool serializationFailed=false; try { Flush(); } catch(InvalidOperationException) { serializationFailed=true; }
        Check(serializationFailed && Queue.Count==5,"Serialization failure lost the queue head.");
        Flush(); CheckIds(2605);

        Enqueue(5005,2605);
        Task.WaitAll(Task.Run((Action)Flush),Task.Run((Action)Flush));
        Check(Queue.IsEmpty,"Concurrent consumers did not drain the backlog."); CheckIds(7610);

        using(var cancellation=new CancellationTokenSource()) {
            AuditFailures=MaintenanceFailures=1;
            var worker=WorkerLoop(cancellation.Token);
            Enqueue(11,7610);
            var timeout=Stopwatch.StartNew();
            while(!Queue.IsEmpty && timeout.ElapsedMilliseconds<3000) Thread.Sleep(20);
            Check(Queue.IsEmpty && !worker.IsCompleted,"Audit/maintenance failure stopped the worker.");
            cancellation.Cancel(); Check(worker.Wait(1000),"Logger did not cancel promptly.");
        }
        CheckIds(7621);
    }
}}
'@
Add-Type -TypeDefinition ($fixtures + $journal + $checks)
$output = Join-Path $repo ('tmp/history-journal-tests/' + [Guid]::NewGuid().ToString('N'))
[HistoryJournalTests.JournalChecks]::Run($output)
Write-Output 'PASS: complete backlog, FIFO/concurrent flush, locked-file retry, short-write rollback, durable-flush rollback, serialization retry, worker recovery and cancellation (7621 events).'
