using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

public static class ExcelLogger
{
    private static string _excelFilePath;
    private static readonly object _lockObj = new object();

    private static readonly Dictionary<string, WorkSession> _sessions = new Dictionary<string, WorkSession>();
    private static string _currentDocKey;
    private static Document _currentDoc;
    private static UIApplication _uiApp;

    private static ActivityMonitor _activity;

    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs");

    private static readonly string SnapshotPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "TimeSnapshots.dat");

    public static void Initialize()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            ExcelPackage.License.SetNonCommercialPersonal("OK1");

            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);

            _excelFilePath = Path.Combine(LogDirectory, "Historique_Temps_Revit.xlsx");
            if (!File.Exists(_excelFilePath)) CreateExcelFile();
            else EnsureSheetExists();

            RecoverSnapshotsToExcel();
        }
        catch { }
    }

    public static void ConfigureActivity(
        TimeSpan idleThreshold,
        TimeSpan unfocusedThreshold,
        TimeSpan busyGapThreshold,
        double cpuBusyThreshold,
        bool countBusyWhenUnfocused)
    {
        _activity = new ActivityMonitor(
            idleThreshold,
            unfocusedThreshold,
            busyGapThreshold,
            cpuBusyThreshold,
            countBusyWhenUnfocused
        );
    }

    public static void OnDocumentOpened(Document document, UIApplication uiApp)
    {
        _uiApp = uiApp;
        var key = BuildDocumentKey(document);

        if (!_sessions.TryGetValue(key, out var ws))
        {
            ws = new WorkSession { LastKnownDocument = document };
            _sessions[key] = ws;

            StartDocumentSessionLog(document, uiApp);
            UpsertSnapshot(key, TimeSpan.Zero, document?.Title ?? "(unknown)", SafeGetRevitVersion(uiApp), true);
        }

        var activeDoc = uiApp?.ActiveUIDocument?.Document;
        if (activeDoc != null && activeDoc.Equals(document))
        {
            ws.StartSegment();
            _currentDoc = document;
            _currentDocKey = key;
        }
    }

    public static void OnDocumentClosing(Document document, UIApplication uiApp)
    {
        _uiApp = uiApp;
        var key = BuildDocumentKey(document);

        if (_sessions.TryGetValue(key, out var ws))
        {
            ws.StopSegment();
            var total = ws.GetTotalActiveDuration();

            EndDocumentSessionLog(document, uiApp, total);
            _sessions.Remove(key);
            RemoveSnapshot(key);

            if (_currentDocKey == key)
            {
                _currentDocKey = null;
                _currentDoc = null;
            }
        }
    }

    public static void OnViewActivated(Document newDoc, UIApplication uiApp)
    {
        _uiApp = uiApp;
        if (newDoc == null || !newDoc.IsValidObject) return;

        var newKey = BuildDocumentKey(newDoc);
        if (_currentDocKey == null || !_currentDocKey.Equals(newKey, StringComparison.Ordinal))
        {
            if (_currentDocKey != null && _sessions.TryGetValue(_currentDocKey, out var prev))
            {
                prev.StopSegment();
                UpsertSnapshot(_currentDocKey, prev.GetTotalActiveDuration(),
                    prev.LastKnownDocument?.Title ?? "(unknown)", SafeGetRevitVersion(_uiApp), true);
            }

            if (!_sessions.TryGetValue(newKey, out var next))
            {
                next = new WorkSession { LastKnownDocument = newDoc };
                _sessions[newKey] = next;
                StartDocumentSessionLog(newDoc, _uiApp);
                UpsertSnapshot(newKey, TimeSpan.Zero, newDoc?.Title ?? "(unknown)", SafeGetRevitVersion(_uiApp), true);
            }

            next.StartSegment();
            _currentDocKey = newKey;
            _currentDoc = newDoc;
        }
    }

    public static void OnIdling(UIApplication uiApp)
    {
        _uiApp = uiApp;
        if (_activity == null || _uiApp == null) return;

        var change = _activity.Update();

        if (_currentDocKey != null && _sessions.TryGetValue(_currentDocKey, out var ws))
        {
            if (change == ActivityChange.BecameInactive)
            {
                ws.Pause();
                UpsertSnapshot(_currentDocKey, ws.GetTotalActiveDuration(),
                    ws.LastKnownDocument?.Title ?? "(unknown)", SafeGetRevitVersion(_uiApp), true);
            }
            else if (change == ActivityChange.BecameActive)
            {
                ws.Resume();
            }
        }
    }

    public static void Shutdown()
    {
        try
        {
            foreach (var kv in _sessions)
            {
                var key = kv.Key;
                var ws = kv.Value;

                ws.StopSegment();
                var total = ws.GetTotalActiveDuration();

                if (ws.LastKnownDocument != null && _uiApp != null)
                    EndDocumentSessionLog(ws.LastKnownDocument, _uiApp, total);
                else
                    EndDocumentSessionLogFallback(key, total);
            }
        }
        catch { }
        finally
        {
            _sessions.Clear();
            _currentDoc = null;
            _currentDocKey = null;
            TryDeleteSnapshotFile();
        }
    }

    private static void CreateExcelFile()
    {
        lock (_lockObj)
        {
            using (var package = new ExcelPackage(new FileInfo(_excelFilePath)))
            {
                var ws = package.Workbook.Worksheets["Historique_Temps_Revit"]
                         ?? package.Workbook.Worksheets.Add("Historique_Temps_Revit");

                ws.Cells["A1"].Value = "Event";
                ws.Cells["B1"].Value = "Document ID";
                ws.Cells["C1"].Value = "Document Name";
                ws.Cells["D1"].Value = "Revit Version";
                ws.Cells["E1"].Value = "Date";
                ws.Cells["F1"].Value = "Time";
                ws.Cells["G1"].Value = "Duration";

                ws.Column(7).Style.Numberformat.Format = "[hh]:mm:ss";
                package.Save();
            }
        }
    }

    private static void EnsureSheetExists()
    {
        lock (_lockObj)
        {
            using (var package = new ExcelPackage(new FileInfo(_excelFilePath)))
            {
                if (package.Workbook.Worksheets["Historique_Temps_Revit"] == null)
                {
                    CreateExcelFile();
                    return;
                }
                package.Save();
            }
        }
    }

    private static void LogEvent(string eventType, Document document, UIApplication uiApp, TimeSpan duration)
    {
        try
        {
            string revitVersion = SafeGetRevitVersion(uiApp);
            string docId = SafeGetDocumentKey(document); // PathName sinon Title
            string docName = document?.Title ?? "(unknown)";
            string date = DateTime.Now.ToString("yyyy-MM-dd");
            string time = DateTime.Now.ToString("HH:mm:ss");

            lock (_lockObj)
            {
                using (var package = new ExcelPackage(new FileInfo(_excelFilePath)))
                {
                    var ws = package.Workbook.Worksheets["Historique_Temps_Revit"]
                             ?? package.Workbook.Worksheets.Add("Historique_Temps_Revit");

                    int lastRow = (ws.Dimension?.End.Row ?? 1) + 1;

                    ws.Cells[lastRow, 1].Value = eventType;
                    ws.Cells[lastRow, 2].Value = docId;
                    ws.Cells[lastRow, 3].Value = docName;
                    ws.Cells[lastRow, 4].Value = revitVersion;
                    ws.Cells[lastRow, 5].Value = date;
                    ws.Cells[lastRow, 6].Value = time;
                    ws.Cells[lastRow, 7].Value = duration;
                    ws.Cells[lastRow, 7].Style.Numberformat.Format = "[hh]:mm:ss";

                    package.Save();
                }
            }
        }
        catch { }
    }

    public static void StartDocumentSessionLog(Document document, UIApplication uiApp)
        => LogEvent("Ouvert", document, uiApp, TimeSpan.Zero);

    public static void EndDocumentSessionLog(Document document, UIApplication uiApp, TimeSpan activeDuration)
        => LogEvent("Fermé", document, uiApp, activeDuration);

    public static void TouchHeartbeat(Document document, UIApplication uiApp, TimeSpan activeDuration) { }

    public static void EndDocumentSessionLogFallback(string documentKey, TimeSpan activeDuration)
    {
        try
        {
            lock (_lockObj)
            {
                using (var package = new ExcelPackage(new FileInfo(_excelFilePath)))
                {
                    var ws = package.Workbook.Worksheets["Historique_Temps_Revit"]
                             ?? package.Workbook.Worksheets.Add("Historique_Temps_Revit");

                    int lastRow = (ws.Dimension?.End.Row ?? 1) + 1;

                    ws.Cells[lastRow, 1].Value = "Fermé";
                    ws.Cells[lastRow, 2].Value = documentKey;
                    ws.Cells[lastRow, 3].Value = "(unknown)";
                    ws.Cells[lastRow, 4].Value = "(unknown)";
                    ws.Cells[lastRow, 5].Value = DateTime.Now.ToString("yyyy-MM-dd");
                    ws.Cells[lastRow, 6].Value = DateTime.Now.ToString("HH:mm:ss");
                    ws.Cells[lastRow, 7].Value = activeDuration;
                    ws.Cells[lastRow, 7].Style.Numberformat.Format = "[hh]:mm:ss";

                    package.Save();
                }
            }
        }
        catch { }
    }

    private static string SafeGetRevitVersion(UIApplication uiApp)
    {
        try { return uiApp.Application.VersionNumber; }
        catch { return "Inconnue"; }
    }

    private static string SafeGetDocumentKey(Document document)
    {
        try
        {
            if (document == null) return "(unknown)";
            return BuildDocumentKey(document);
        }
        catch
        {
            return string.IsNullOrWhiteSpace(document?.Title) ? "(unknown)" : document.Title;
        }
    }

    private static string BuildDocumentKey(Document doc)
    {
        try
        {
            if (!string.IsNullOrEmpty(doc.PathName))
                return doc.PathName;
            return string.IsNullOrWhiteSpace(doc.Title) ? "(sans nom)" : doc.Title;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(doc?.Title) ? "(unknown)" : doc.Title;
        }
    }

    private class Snapshot
    {
        public string DocKey;
        public string DocName;
        public string RevitVersion;
        public long ActiveSeconds;
        public long LastUpdateTicks;
        public bool IsOpen;
    }

    private static void UpsertSnapshot(string docKey, TimeSpan activeDuration, string docName, string revitVersion, bool isOpen)
    {
        var map = LoadSnapshots();
        map[docKey] = new Snapshot
        {
            DocKey = docKey,
            DocName = docName ?? "(unknown)",
            RevitVersion = revitVersion ?? "(unknown)",
            ActiveSeconds = (long)activeDuration.TotalSeconds,
            LastUpdateTicks = DateTime.UtcNow.Ticks,
            IsOpen = isOpen
        };
        SaveSnapshots(map);
    }

    private static void RemoveSnapshot(string docKey)
    {
        var map = LoadSnapshots();
        if (map.Remove(docKey))
            SaveSnapshots(map);
    }

    private static void RecoverSnapshotsToExcel()
    {
        var map = LoadSnapshots();
        bool changed = false;

        foreach (var kv in map)
        {
            var s = kv.Value;
            if (s.IsOpen)
            {
                EndDocumentSessionLogFallback(s.DocKey, TimeSpan.FromSeconds(Math.Max(0, s.ActiveSeconds)));
                s.IsOpen = false;
                changed = true;
            }
        }
        if (changed) SaveSnapshots(map); else TryDeleteSnapshotFile();
    }

    private static Dictionary<string, Snapshot> LoadSnapshots()
    {
        var dict = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(SnapshotPath)) return dict;
            foreach (var line in File.ReadAllLines(SnapshotPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length < 6) continue;

                long sec = 0, ti = 0;
                long.TryParse(parts[3], out sec);
                long.TryParse(parts[4], out ti);

                dict[parts[0]] = new Snapshot
                {
                    DocKey = parts[0],
                    DocName = parts[1],
                    RevitVersion = parts[2],
                    ActiveSeconds = sec,
                    LastUpdateTicks = ti,
                    IsOpen = parts[5] == "1"
                };
            }
        }
        catch { }
        return dict;
    }

    private static void SaveSnapshots(Dictionary<string, Snapshot> map)
    {
        try
        {
            if (map == null || map.Count == 0)
            {
                TryDeleteSnapshotFile();
                return;
            }
            using (var sw = new StreamWriter(SnapshotPath, false))
            {
                foreach (var s in map.Values)
                    sw.WriteLine($"{s.DocKey}|{s.DocName}|{s.RevitVersion}|{s.ActiveSeconds}|{s.LastUpdateTicks}|{(s.IsOpen ? "1" : "0")}");
            }
            try { File.SetAttributes(SnapshotPath, FileAttributes.Hidden); } catch { }
        }
        catch { }
    }

    private static void TryDeleteSnapshotFile()
    {
        try { if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath); } catch { }
    }
}

public class WorkSession
{
    private readonly List<(DateTime Start, DateTime End, TimeSpan Active)> _segments = new List<(DateTime, DateTime, TimeSpan)>();
    private DateTime? _segmentStart;
    private DateTime? _activeStart;
    private TimeSpan _activeAccum = TimeSpan.Zero;
    private bool _isPaused = true;

    public Document LastKnownDocument { get; set; }

    public void StartSegment()
    {
        if (_segmentStart != null) return;
        _segmentStart = DateTime.Now;
        Resume();
    }

    public void StopSegment()
    {
        if (_segmentStart == null) return;
        if (!_isPaused && _activeStart.HasValue)
            _activeAccum += DateTime.Now - _activeStart.Value;

        _segments.Add((_segmentStart.Value, DateTime.Now, _activeAccum));
        _segmentStart = null;
        _activeStart = null;
        _activeAccum = TimeSpan.Zero;
        _isPaused = true;
    }

    public void Pause()
    {
        if (_isPaused) return;
        if (_activeStart.HasValue)
            _activeAccum += DateTime.Now - _activeStart.Value;
        _activeStart = null;
        _isPaused = true;
    }

    public void Resume()
    {
        if (!_isPaused && _activeStart.HasValue) return;
        _activeStart = DateTime.Now;
        _isPaused = false;
    }

    public TimeSpan GetTotalActiveDuration()
    {
        TimeSpan sum = TimeSpan.Zero;
        foreach (var s in _segments) sum += s.Active;

        if (_segmentStart != null)
        {
            if (!_isPaused && _activeStart.HasValue)
                sum += _activeAccum + (DateTime.Now - _activeStart.Value);
            else
                sum += _activeAccum;
        }
        return sum;
    }
}

internal sealed class ActivityMonitor
{
    private readonly TimeSpan _idleThreshold;
    private readonly TimeSpan _unfocusedThreshold;
    private readonly TimeSpan _busyGapThreshold;
    private readonly double _cpuBusyThreshold;
    private readonly bool _countBusyWhenUnfocused;

    private bool _inactive;
    private DateTime _lastUpdate = DateTime.UtcNow;
    private TimeSpan _lastCpu;
    private readonly Process _proc = Process.GetCurrentProcess();
    private readonly int _cpuCount = Environment.ProcessorCount;

    private DateTime _lastForegroundSeen = DateTime.UtcNow;

    public ActivityMonitor(
        TimeSpan idleThreshold,
        TimeSpan unfocusedThreshold,
        TimeSpan busyGapThreshold,
        double cpuBusyThreshold,
        bool countBusyWhenUnfocused)
    {
        _idleThreshold = idleThreshold;
        _unfocusedThreshold = unfocusedThreshold;
        _busyGapThreshold = busyGapThreshold;
        _cpuBusyThreshold = cpuBusyThreshold;
        _countBusyWhenUnfocused = countBusyWhenUnfocused;

        _lastCpu = _proc.TotalProcessorTime;
    }

    public ActivityChange Update()
    {
        var now = DateTime.UtcNow;
        var wallDelta = now - _lastUpdate;

        var cpuNow = _proc.TotalProcessorTime;
        var cpuDelta = cpuNow - _lastCpu;
        double cpuRatio = 0;
        if (wallDelta.TotalMilliseconds > 1)
            cpuRatio = cpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * _cpuCount);

        bool busyRecent = wallDelta >= _busyGapThreshold || cpuRatio >= _cpuBusyThreshold;

        IntPtr fgWnd = GetForegroundWindow();
        uint fgPid = 0;
        GetWindowThreadProcessId(fgWnd, out fgPid);
        bool revitForeground = (fgPid == (uint)_proc.Id);
        if (revitForeground) _lastForegroundSeen = now;

        TimeSpan osIdle = GetOsIdleTime();

        bool shouldPause;
        if (revitForeground)
        {
            shouldPause = !busyRecent && (osIdle >= _idleThreshold);
        }
        else
        {
            bool lostFocusForLong = (now - _lastForegroundSeen) >= _unfocusedThreshold;
            if (_countBusyWhenUnfocused && busyRecent) shouldPause = false;
            else shouldPause = lostFocusForLong;
        }

        ActivityChange change = ActivityChange.None;
        if (shouldPause && !_inactive) { _inactive = true; change = ActivityChange.BecameInactive; }
        if (!shouldPause && _inactive) { _inactive = false; change = ActivityChange.BecameActive; }

        _lastUpdate = now;
        _lastCpu = cpuNow;
        return change;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static TimeSpan GetOsIdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;

        uint tick = (uint)Environment.TickCount;
        uint delta = tick >= lii.dwTime ? tick - lii.dwTime : (uint)(uint.MaxValue - lii.dwTime + tick);
        return TimeSpan.FromMilliseconds(delta);
    }

    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}

internal enum ActivityChange { None, BecameInactive, BecameActive }
