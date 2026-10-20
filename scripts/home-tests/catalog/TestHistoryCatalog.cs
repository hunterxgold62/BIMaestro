using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using Newtonsoft.Json.Linq;
using BIMaestro.Home;

internal static class TestHistoryCatalog
{
    static int Main(string[] args)
    {
        string dependencyPath = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, evt) => {
            string file = Path.Combine(dependencyPath, new AssemblyName(evt.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        try { return Run(args.Length > 1 ? args[1] : "2026"); } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static string Hash(string path) { using (var sha = SHA256.Create()) using(var file=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)); }
    static int Run(string hostVersion)
    {
        var path = HomeHistoryCatalog.SourcePath;
        string before = Hash(path);
        var elapsed=Stopwatch.StartNew();
        HomeHistoryCatalog.ConfigureVersion(hostVersion);
        HomeHistoryCatalog.RequestRefresh(true);
        JObject payload;
        do { Thread.Sleep(25); payload=JObject.Parse(HomeHistoryCatalog.GetPayloadJson()); }
        while (((string)payload["status"] == "loading" || (bool?)payload["checking"] == true) && elapsed.Elapsed.TotalSeconds < 30);
        if ((string)payload["status"] != "ready") throw new Exception(payload.ToString());
        var entries=(JArray)payload["entries"];int projects=0,families=0,thumbs=0,opened=0;
        int previousVersion=int.MaxValue;
        foreach(JObject e in entries) { if((string)e["extension"]==".rvt") projects++;else if((string)e["extension"]==".rfa")families++; if(e["thumbnailPath"].Type!=JTokenType.Null)thumbs++;if(!string.IsNullOrWhiteSpace((string)e["lastOpened"]))opened++; if(!HomeHistoryCatalog.IsKnownPath((string)e["path"]))throw new Exception("Known-path validation failed");
            if(!File.Exists((string)e["path"]))throw new Exception("Missing entry escaped validation");
            int version=(int)e["recordedVersion"];if(version>int.Parse(hostVersion)||version>previousVersion)throw new Exception("Host/version ordering failed");previousVersion=version; }
        bool unchanged=before==Hash(path);
        var requested=entries.Take(24).Select(e=>(string)e["path"]).ToArray();
        HomeHistoryCatalog.RequestThumbnails(requested);
        var previewTimer=Stopwatch.StartNew();int attempted=0, previews=0;
        do {
            Thread.Sleep(20); var previewEntries=(JArray)JObject.Parse(HomeHistoryCatalog.GetPayloadJson())["entries"];
            attempted=previewEntries.Count(e=>requested.Contains((string)e["path"])&&(bool?)e["thumbnailAttempted"]==true);
            previews=previewEntries.Count(e=>requested.Contains((string)e["path"])&&((string)e["thumbnailDataUrl"]??"").StartsWith("data:image/png;base64,"));
        } while(attempted<requested.Length&&previewTimer.Elapsed.TotalSeconds<10);
        previewTimer.Stop();
        if(attempted!=requested.Length||previews==0)throw new Exception("Preview queue did not finish with real PNG data URLs");
        HomeHistoryCatalog.RequestRefresh(true);
        var recheck=Stopwatch.StartNew();JObject refreshed;
        do { Thread.Sleep(20); refreshed=JObject.Parse(HomeHistoryCatalog.GetPayloadJson()); }
        while(HomeHistoryCatalog.IsRefreshing&&recheck.Elapsed.TotalSeconds<10);
        int retained=((JArray)refreshed["entries"]).Count(e=>requested.Contains((string)e["path"])&&(bool?)e["thumbnailAttempted"]==true);
        if(retained!=requested.Length)throw new Exception("Preview memo was lost during revalidation");
        var cached=Stopwatch.StartNew();for(int n=0;n<1000;n++) HomeHistoryCatalog.GetPayloadJson();cached.Stop();
        Console.WriteLine(new JObject { ["status"]="ready",["totalLogged"]=payload["totalLogged"],["inaccessible"]=payload["inaccessible"],["unsupported"]=payload["unsupported"],["newerVersion"]=payload["newerVersion"],["entries"]=entries.Count,["projects"]=projects,["families"]=families,["withOpenedDate"]=opened,["cachedThumbnails"]=thumbs,["thumbnailRequested"]=requested.Length,["thumbnailAttempted"]=attempted,["thumbnailPngDataUrls"]=previews,["thumbnailQueueMilliseconds"]=previewTimer.Elapsed.TotalMilliseconds,["parseMilliseconds"]=payload["elapsedMs"],["completionMilliseconds"]=elapsed.Elapsed.TotalMilliseconds,["cachedPayload1000ReadsMilliseconds"]=cached.Elapsed.TotalMilliseconds,["workbookUnchanged"]=unchanged,["unknownPathRejected"]=!HomeHistoryCatalog.IsKnownPath("C:\\not-in-log.rvt") }.ToString());
        return unchanged&&entries.Count>0&&opened==entries.Count?0:1;
    }
}
