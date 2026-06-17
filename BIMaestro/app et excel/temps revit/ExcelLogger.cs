using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class ExcelLogger
{
    private const string WorksheetName = "Historique_Temps_Revit";
    private const string StorageMutexName = "BIMaestro_TimeLogger_Storage_v2";
    private const string SnapshotFormatVersion = "v2";

    private static string _excelFilePath;
    private static readonly object _storageLock = new object();
    private static readonly object _stateLock = new object();

    private static readonly Dictionary<string, WorkSession> _sessions =
        new Dictionary<string, WorkSession>(StringComparer.Ordinal);

    private static string _currentDocKey;
    private static Document _currentDoc;
    private static UIApplication _uiApp;

    private static ActivityMonitor _activity;
    private static DateTime _lastSnapshotWriteUtc = DateTime.MinValue;
    private static bool _systemEventsRegistered;

    private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;
    private static readonly TimeSpan SnapshotHeartbeatInterval = TimeSpan.FromMinutes(1);

    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs");

    private static readonly string SnapshotPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "TimeSnapshots.dat");

    public static void Initialize()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ExcelPackage.License.SetNonCommercialPersonal("OK1");

            WithStorageLock(() =>
            {
                if (!Directory.Exists(LogDirectory))
                    Directory.CreateDirectory(LogDirectory);

                _excelFilePath = Path.Combine(LogDirectory, "Historique_Temps_Revit.xlsx");
                if (!File.Exists(_excelFilePath)) CreateExcelFileCore();
                else EnsureSheetExistsCore();

                RecoverSnapshotsToExcelCore();
            });

            RegisterSystemEvents();
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
        if (document == null) return;

        lock (_stateLock)
        {
            _uiApp = uiApp;
            EnsureSession(document, uiApp, out var key);

            var activeDoc = uiApp?.ActiveUIDocument?.Document;
            if (activeDoc != null && activeDoc.Equals(document))
                ActivateDocument(document, uiApp);
            else if (_sessions.TryGetValue(key, out var ws))
                UpsertSnapshot(key, ws, true);
        }
    }

    public static void OnDocumentClosing(Document document, UIApplication uiApp)
    {
        if (document == null) return;

        lock (_stateLock)
        {
            _uiApp = uiApp;
            var key = BuildDocumentKey(document);

            if (_sessions.TryGetValue(key, out var ws))
            {
                ws.RefreshMetadata(document, uiApp);
                ws.StopSegment();

                EndDocumentSessionLog(document, uiApp, ws);
                _sessions.Remove(key);
                RemoveSnapshot(key);

                if (string.Equals(_currentDocKey, key, StringComparison.Ordinal))
                {
                    _currentDocKey = null;
                    _currentDoc = null;
                }
            }
        }
    }

    public static void OnViewActivated(Document newDoc, UIApplication uiApp)
    {
        if (newDoc == null || !newDoc.IsValidObject) return;

        lock (_stateLock)
        {
            _uiApp = uiApp;
            ActivateDocument(newDoc, uiApp);
        }
    }

    public static void OnIdling(UIApplication uiApp)
    {
        lock (_stateLock)
        {
            _uiApp = uiApp;
            if (_activity == null || _uiApp == null) return;

            var change = _activity.Update();

            if (_currentDocKey == null || !_sessions.TryGetValue(_currentDocKey, out var ws))
                return;

            if (change == ActivityChange.BecameInactive)
            {
                ws.Pause();
                UpsertSnapshot(_currentDocKey, ws, true);
            }
            else if (change == ActivityChange.BecameActive)
            {
                ws.Resume();
                UpsertSnapshot(_currentDocKey, ws, true);
            }
            else
            {
                UpsertSnapshotIfDue(_currentDocKey, ws);
            }
        }
    }

    public static void Shutdown()
    {
        lock (_stateLock)
        {
            try
            {
                UnregisterSystemEvents();

                foreach (var kv in _sessions.ToList())
                {
                    var key = kv.Key;
                    var ws = kv.Value;

                    ws.StopSegment();

                    if (ws.LastKnownDocument != null && _uiApp != null)
                        EndDocumentSessionLog(ws.LastKnownDocument, _uiApp, ws);
                    else
                        EndDocumentSessionLogFallback(key, ws.DocumentName, ws.RevitVersion, ws.GetTotalActiveDuration(), ws.GetDailySeconds());

                    RemoveSnapshot(key);
                }
            }
            catch { }
            finally
            {
                RemoveSnapshotsForCurrentProcess();
                _sessions.Clear();
                _currentDoc = null;
                _currentDocKey = null;
            }
        }
    }

    private static WorkSession EnsureSession(Document document, UIApplication uiApp, out string key)
    {
        key = BuildDocumentKey(document);

        if (!_sessions.TryGetValue(key, out var ws))
        {
            ws = new WorkSession
            {
                LastKnownDocument = document,
                DocumentName = SafeGetDocumentName(document),
                RevitVersion = SafeGetRevitVersion(uiApp)
            };

            _sessions[key] = ws;
            StartDocumentSessionLog(document, uiApp);
            UpsertSnapshot(key, ws, true);
        }
        else
        {
            ws.RefreshMetadata(document, uiApp);
        }

        return ws;
    }

    private static void ActivateDocument(Document newDoc, UIApplication uiApp)
    {
        var next = EnsureSession(newDoc, uiApp, out var newKey);

        if (string.Equals(_currentDocKey, newKey, StringComparison.Ordinal))
        {
            if (!next.HasOpenSegment)
                next.StartSegment();

            UpsertSnapshotIfDue(newKey, next);
            return;
        }

        if (_currentDocKey != null && _sessions.TryGetValue(_currentDocKey, out var prev))
        {
            prev.StopSegment();
            UpsertSnapshot(_currentDocKey, prev, true);
        }

        next.StartSegment();
        _currentDocKey = newKey;
        _currentDoc = newDoc;
        UpsertSnapshot(newKey, next, true);
    }

    private static void RegisterSystemEvents()
    {
        if (_systemEventsRegistered) return;

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            _systemEventsRegistered = true;
        }
        catch { }
    }

    private static void UnregisterSystemEvents()
    {
        if (!_systemEventsRegistered) return;

        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
        }
        catch { }
        finally
        {
            _systemEventsRegistered = false;
        }
    }

    private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        lock (_stateLock)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                PauseAllSessionsAndSnapshot();
                _activity?.ForceInactive();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _activity?.ResetAfterResume();
            }
        }
    }

    private static void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        lock (_stateLock)
        {
            PauseAllSessionsAndSnapshot();
            _activity?.ForceInactive();
        }
    }

    private static void PauseAllSessionsAndSnapshot()
    {
        foreach (var kv in _sessions)
        {
            kv.Value.Pause();
            UpsertSnapshot(kv.Key, kv.Value, true);
        }
    }

    private static void CreateExcelFile()
        => WithStorageLock(CreateExcelFileCore);

    private static void CreateExcelFileCore()
    {
        using (var package = new ExcelPackage(new FileInfo(_excelFilePath)))
        {
            var ws = package.Workbook.Worksheets[WorksheetName]
                     ?? package.Workbook.Worksheets.Add(WorksheetName);

            EnsureHeader(ws);
            package.Save();
        }
    }

    private static void EnsureSheetExists()
        => WithStorageLock(EnsureSheetExistsCore);

    private static void EnsureSheetExistsCore()
    {
        using (var package = new ExcelPackage(new FileInfo(_excelFilePath)))
        {
            var ws = package.Workbook.Worksheets[WorksheetName]
                     ?? package.Workbook.Worksheets.Add(WorksheetName);

            EnsureHeader(ws);
            package.Save();
        }
    }

    private static void EnsureHeader(ExcelWorksheet ws)
    {
        ws.Cells["A1"].Value = "Event";
        ws.Cells["B1"].Value = "Document ID";
        ws.Cells["C1"].Value = "Document Name";
        ws.Cells["D1"].Value = "Revit Version";
        ws.Cells["E1"].Value = "Date";
        ws.Cells["F1"].Value = "Time";
        ws.Cells["G1"].Value = "Duration";
        ws.Column(7).Style.Numberformat.Format = "[hh]:mm:ss";
    }

    private static void LogEvent(string eventType, Document document, UIApplication uiApp, TimeSpan duration)
    {
        try
        {
            var entry = new LogEntry
            {
                EventType = eventType,
                DocumentId = SafeGetDocumentKey(document),
                DocumentName = SafeGetDocumentName(document),
                RevitVersion = SafeGetRevitVersion(uiApp),
                When = DateTime.Now,
                Duration = duration
            };

            AppendLogEntries(new[] { entry });
        }
        catch { }
    }

    private static void LogClosedSession(string documentKey, string documentName, string revitVersion, IEnumerable<WorkTimeSlice> activeSlices, TimeSpan fallbackDuration)
    {
        try
        {
            var entries = BuildClosedLogEntries(documentKey, documentName, revitVersion, activeSlices, fallbackDuration);
            AppendLogEntries(entries);
        }
        catch { }
    }

    private static List<LogEntry> BuildClosedLogEntries(
        string documentKey,
        string documentName,
        string revitVersion,
        IEnumerable<WorkTimeSlice> activeSlices,
        TimeSpan fallbackDuration)
    {
        var entries = new List<LogEntry>();

        foreach (var slice in activeSlices ?? Enumerable.Empty<WorkTimeSlice>())
        {
            var start = slice.Start;
            var end = slice.End;
            if (end <= start) continue;

            while (start.Date < end.Date)
            {
                var boundary = start.Date.AddDays(1);
                AddClosedEntry(entries, documentKey, documentName, revitVersion, boundary.AddTicks(-1), boundary - start);
                start = boundary;
            }

            AddClosedEntry(entries, documentKey, documentName, revitVersion, end, end - start);
        }

        if (entries.Count == 0 && fallbackDuration > TimeSpan.Zero)
            AddClosedEntry(entries, documentKey, documentName, revitVersion, DateTime.Now, fallbackDuration);

        return entries;
    }

    private static void AddClosedEntry(
        List<LogEntry> entries,
        string documentKey,
        string documentName,
        string revitVersion,
        DateTime when,
        TimeSpan duration)
    {
        if (duration.TotalSeconds < 1) return;

        entries.Add(new LogEntry
        {
            EventType = "Fermé",
            DocumentId = documentKey,
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "(unknown)" : documentName,
            RevitVersion = string.IsNullOrWhiteSpace(revitVersion) ? "(unknown)" : revitVersion,
            When = when,
            Duration = duration
        });
    }

    private static void AppendLogEntries(IEnumerable<LogEntry> entries)
        => WithStorageLock(() => AppendLogEntriesCore(entries));

    private static void AppendLogEntriesCore(IEnumerable<LogEntry> entries)
    {
        var list = (entries ?? Enumerable.Empty<LogEntry>()).ToList();
        if (list.Count == 0) return;

        using (var package = new ExcelPackage(new FileInfo(_excelFilePath)))
        {
            var ws = package.Workbook.Worksheets[WorksheetName]
                     ?? package.Workbook.Worksheets.Add(WorksheetName);

            EnsureHeader(ws);

            int lastRow = ws.Dimension?.End.Row ?? 1;
            foreach (var entry in list)
            {
                lastRow++;

                ws.Cells[lastRow, 1].Value = entry.EventType;
                ws.Cells[lastRow, 2].Value = entry.DocumentId;
                ws.Cells[lastRow, 3].Value = entry.DocumentName;
                ws.Cells[lastRow, 4].Value = entry.RevitVersion;
                ws.Cells[lastRow, 5].Value = entry.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                ws.Cells[lastRow, 6].Value = entry.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                ws.Cells[lastRow, 7].Value = entry.Duration;
                ws.Cells[lastRow, 7].Style.Numberformat.Format = "[hh]:mm:ss";
            }

            package.Save();
        }
    }

    public static void StartDocumentSessionLog(Document document, UIApplication uiApp)
        => LogEvent("Ouvert", document, uiApp, TimeSpan.Zero);

    public static void EndDocumentSessionLog(Document document, UIApplication uiApp, WorkSession session)
    {
        string documentKey = SafeGetDocumentKey(document);
        string documentName = SafeGetDocumentName(document);
        string revitVersion = SafeGetRevitVersion(uiApp);

        LogClosedSession(documentKey, documentName, revitVersion, session?.GetActiveSlices(), session?.GetTotalActiveDuration() ?? TimeSpan.Zero);
    }

    public static void EndDocumentSessionLog(Document document, UIApplication uiApp, TimeSpan activeDuration)
        => LogEvent("Fermé", document, uiApp, activeDuration);

    public static void TouchHeartbeat(Document document, UIApplication uiApp, TimeSpan activeDuration) { }

    public static void EndDocumentSessionLogFallback(string documentKey, TimeSpan activeDuration)
        => EndDocumentSessionLogFallback(documentKey, "(unknown)", "(unknown)", activeDuration, null);

    private static void EndDocumentSessionLogFallback(
        string documentKey,
        string documentName,
        string revitVersion,
        TimeSpan activeDuration,
        Dictionary<string, long> dailySeconds)
    {
        try
        {
            if (dailySeconds != null && dailySeconds.Count > 0)
            {
                var entries = new List<LogEntry>();
                foreach (var kv in dailySeconds.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day))
                        continue;

                    AddClosedEntry(
                        entries,
                        documentKey,
                        documentName,
                        revitVersion,
                        day.AddDays(1).AddTicks(-1),
                        TimeSpan.FromSeconds(Math.Max(0, kv.Value)));
                }

                AppendLogEntries(entries);
                return;
            }

            var entry = new LogEntry
            {
                EventType = "Fermé",
                DocumentId = documentKey,
                DocumentName = documentName,
                RevitVersion = revitVersion,
                When = DateTime.Now,
                Duration = activeDuration
            };

            AppendLogEntries(new[] { entry });
        }
        catch { }
    }

    private static string SafeGetRevitVersion(UIApplication uiApp)
    {
        try { return uiApp.Application.VersionNumber; }
        catch { return "Inconnue"; }
    }

    private static string SafeGetDocumentName(Document document)
    {
        try { return string.IsNullOrWhiteSpace(document?.Title) ? "(unknown)" : document.Title; }
        catch { return "(unknown)"; }
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
            if (doc != null)
            {
                string localPath = doc.PathName;
                string centralPath = null;

                if (doc.IsWorkshared)
                {
                    try
                    {
                        var centralModelPath = doc.GetWorksharingCentralModelPath();
                        if (centralModelPath != null)
                            centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(centralModelPath);
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(centralPath))
                    return centralPath;

                if (!string.IsNullOrWhiteSpace(localPath))
                    return localPath;

                return string.IsNullOrWhiteSpace(doc.Title) ? "(sans nom)" : doc.Title;
            }

            return "(unknown)";
        }
        catch
        {
            return string.IsNullOrWhiteSpace(doc?.Title) ? "(unknown)" : doc.Title;
        }
    }

    private class Snapshot
    {
        public string StorageKey;
        public string DocKey;
        public string DocName;
        public string RevitVersion;
        public long ActiveSeconds;
        public long LastUpdateTicks;
        public bool IsOpen;
        public int ProcessId;
        public Dictionary<string, long> DailySeconds = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    private static void UpsertSnapshotIfDue(string docKey, WorkSession session)
    {
        if ((DateTime.UtcNow - _lastSnapshotWriteUtc) < SnapshotHeartbeatInterval)
            return;

        UpsertSnapshot(docKey, session, false);
    }

    private static void UpsertSnapshot(string docKey, WorkSession session, bool force)
    {
        if (string.IsNullOrWhiteSpace(docKey) || session == null) return;

        if (!force && (DateTime.UtcNow - _lastSnapshotWriteUtc) < SnapshotHeartbeatInterval)
            return;

        WithStorageLock(() =>
        {
            var map = LoadSnapshotsCore();
            var storageKey = BuildSnapshotStorageKey(docKey, CurrentProcessId);
            var dailySeconds = session.GetDailySeconds();

            map[storageKey] = new Snapshot
            {
                StorageKey = storageKey,
                DocKey = docKey,
                DocName = session.DocumentName ?? "(unknown)",
                RevitVersion = session.RevitVersion ?? "(unknown)",
                ActiveSeconds = (long)session.GetTotalActiveDuration().TotalSeconds,
                LastUpdateTicks = DateTime.UtcNow.Ticks,
                IsOpen = true,
                ProcessId = CurrentProcessId,
                DailySeconds = dailySeconds
            };

            SaveSnapshotsCore(map);
            _lastSnapshotWriteUtc = DateTime.UtcNow;
        });
    }

    private static void RemoveSnapshot(string docKey)
    {
        if (string.IsNullOrWhiteSpace(docKey)) return;

        WithStorageLock(() =>
        {
            var map = LoadSnapshotsCore();
            if (map.Remove(BuildSnapshotStorageKey(docKey, CurrentProcessId)))
                SaveSnapshotsCore(map);
        });
    }

    private static void RemoveSnapshotsForCurrentProcess()
    {
        try
        {
            WithStorageLock(() =>
            {
                var map = LoadSnapshotsCore();
                var keys = map.Where(kv => kv.Value.ProcessId == CurrentProcessId)
                              .Select(kv => kv.Key)
                              .ToList();

                foreach (var key in keys)
                    map.Remove(key);

                if (keys.Count > 0)
                    SaveSnapshotsCore(map);
            });
        }
        catch { }
    }

    private static void RecoverSnapshotsToExcelCore()
    {
        var map = LoadSnapshotsCore();
        var toRemove = new List<string>();

        foreach (var kv in map)
        {
            var s = kv.Value;
            if (!s.IsOpen) continue;

            if (!IsProcessAlive(s.ProcessId))
            {
                EndDocumentSessionLogFallbackCore(
                    s.DocKey,
                    string.IsNullOrWhiteSpace(s.DocName) ? "(unknown)" : s.DocName,
                    string.IsNullOrWhiteSpace(s.RevitVersion) ? "(unknown)" : s.RevitVersion,
                    TimeSpan.FromSeconds(Math.Max(0, s.ActiveSeconds)),
                    s.DailySeconds);

                toRemove.Add(kv.Key);
            }
        }

        foreach (var key in toRemove)
            map.Remove(key);

        if (toRemove.Count > 0)
            SaveSnapshotsCore(map);
        else if (map.Count == 0)
            TryDeleteSnapshotFileCore();
    }

    private static void EndDocumentSessionLogFallbackCore(
        string documentKey,
        string documentName,
        string revitVersion,
        TimeSpan activeDuration,
        Dictionary<string, long> dailySeconds)
    {
        if (dailySeconds != null && dailySeconds.Count > 0)
        {
            var entries = new List<LogEntry>();
            foreach (var kv in dailySeconds.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day))
                    continue;

                AddClosedEntry(
                    entries,
                    documentKey,
                    documentName,
                    revitVersion,
                    day.AddDays(1).AddTicks(-1),
                    TimeSpan.FromSeconds(Math.Max(0, kv.Value)));
            }

            AppendLogEntriesCore(entries);
            return;
        }

        AppendLogEntriesCore(new[]
        {
            new LogEntry
            {
                EventType = "Fermé",
                DocumentId = documentKey,
                DocumentName = documentName,
                RevitVersion = revitVersion,
                When = DateTime.Now,
                Duration = activeDuration
            }
        });
    }

    private static Dictionary<string, Snapshot> LoadSnapshots()
        => WithStorageLock(LoadSnapshotsCore);

    private static Dictionary<string, Snapshot> LoadSnapshotsCore()
    {
        var dict = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(SnapshotPath)) return dict;

            foreach (var line in File.ReadAllLines(SnapshotPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var snapshot = ParseSnapshotLine(line);
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.StorageKey))
                    continue;

                dict[snapshot.StorageKey] = snapshot;
            }
        }
        catch { }
        return dict;
    }

    private static Snapshot ParseSnapshotLine(string line)
    {
        var parts = line.Split('|');
        if (parts.Length == 0) return null;

        if (string.Equals(parts[0], SnapshotFormatVersion, StringComparison.Ordinal))
            return ParseV2Snapshot(parts);

        return ParseLegacySnapshot(parts);
    }

    private static Snapshot ParseV2Snapshot(string[] parts)
    {
        try
        {
            if (parts.Length < 9) return null;

            long sec = 0, ticks = 0;
            int pid = 0;
            long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out sec);
            long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
            int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);

            var daily = parts.Length >= 10 ? DeserializeDailySeconds(DecodeField(parts[9])) : new Dictionary<string, long>(StringComparer.Ordinal);

            return new Snapshot
            {
                StorageKey = DecodeField(parts[1]),
                DocKey = DecodeField(parts[2]),
                DocName = DecodeField(parts[3]),
                RevitVersion = DecodeField(parts[4]),
                ActiveSeconds = sec,
                LastUpdateTicks = ticks,
                IsOpen = parts[7] == "1",
                ProcessId = pid,
                DailySeconds = daily
            };
        }
        catch
        {
            return null;
        }
    }

    private static Snapshot ParseLegacySnapshot(string[] parts)
    {
        try
        {
            if (parts.Length < 6) return null;

            bool hasPid = parts.Length >= 7
                          && (parts[parts.Length - 2] == "1" || parts[parts.Length - 2] == "0")
                          && int.TryParse(parts[parts.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            int pidIndex = hasPid ? parts.Length - 1 : -1;
            int isOpenIndex = hasPid ? parts.Length - 2 : parts.Length - 1;
            int ticksIndex = isOpenIndex - 1;
            int secondsIndex = isOpenIndex - 2;
            int versionIndex = isOpenIndex - 3;
            int nameIndex = isOpenIndex - 4;

            if (nameIndex < 1) return null;

            long sec = 0, ticks = 0;
            int pid = 0;
            long.TryParse(parts[secondsIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out sec);
            long.TryParse(parts[ticksIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
            if (pidIndex >= 0) int.TryParse(parts[pidIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);

            string docKey = string.Join("|", parts.Take(nameIndex));

            return new Snapshot
            {
                StorageKey = BuildSnapshotStorageKey(docKey, pid),
                DocKey = docKey,
                DocName = parts[nameIndex],
                RevitVersion = parts[versionIndex],
                ActiveSeconds = sec,
                LastUpdateTicks = ticks,
                IsOpen = parts[isOpenIndex] == "1",
                ProcessId = pid,
                DailySeconds = new Dictionary<string, long>(StringComparer.Ordinal)
            };
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSnapshots(Dictionary<string, Snapshot> map)
        => WithStorageLock(() => SaveSnapshotsCore(map));

    private static void SaveSnapshotsCore(Dictionary<string, Snapshot> map)
    {
        try
        {
            if (map == null || map.Count == 0)
            {
                TryDeleteSnapshotFileCore();
                return;
            }

            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);

            string tempPath = SnapshotPath + "." + CurrentProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";

            using (var sw = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                foreach (var s in map.Values)
                {
                    sw.WriteLine(string.Join("|", new[]
                    {
                        SnapshotFormatVersion,
                        EncodeField(s.StorageKey),
                        EncodeField(s.DocKey),
                        EncodeField(s.DocName),
                        EncodeField(s.RevitVersion),
                        s.ActiveSeconds.ToString(CultureInfo.InvariantCulture),
                        s.LastUpdateTicks.ToString(CultureInfo.InvariantCulture),
                        s.IsOpen ? "1" : "0",
                        s.ProcessId.ToString(CultureInfo.InvariantCulture),
                        EncodeField(SerializeDailySeconds(s.DailySeconds))
                    }));
                }
            }

            File.Copy(tempPath, SnapshotPath, true);
            File.Delete(tempPath);

            try { File.SetAttributes(SnapshotPath, FileAttributes.Hidden); } catch { }
        }
        catch { }
    }

    private static string BuildSnapshotStorageKey(string docKey, int processId)
        => processId.ToString(CultureInfo.InvariantCulture) + ":" + (docKey ?? "(unknown)");

    private static string EncodeField(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string DecodeField(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
        catch { return value ?? string.Empty; }
    }

    private static string SerializeDailySeconds(Dictionary<string, long> dailySeconds)
    {
        if (dailySeconds == null || dailySeconds.Count == 0) return string.Empty;

        return string.Join(";", dailySeconds
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static Dictionary<string, long> DeserializeDailySeconds(string value)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value)) return result;

        foreach (var part in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split(new[] { '=' }, 2);
            if (pair.Length != 2) continue;

            if (long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                result[pair[0]] = seconds;
        }

        return result;
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            var proc = Process.GetProcessById(processId);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteSnapshotFile()
        => WithStorageLock(TryDeleteSnapshotFileCore);

    private static void TryDeleteSnapshotFileCore()
    {
        try { if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath); } catch { }
    }

    private static void WithStorageLock(Action action)
    {
        lock (_storageLock)
        {
            Mutex mutex = null;
            bool acquired = false;

            try
            {
                mutex = new Mutex(false, StorageMutexName);
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }

                if (!acquired) return;
                action();
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }

                try { mutex?.Dispose(); } catch { }
            }
        }
    }

    private static T WithStorageLock<T>(Func<T> action)
    {
        T result = default(T);
        WithStorageLock(() => result = action());
        return result;
    }

    private class LogEntry
    {
        public string EventType;
        public string DocumentId;
        public string DocumentName;
        public string RevitVersion;
        public DateTime When;
        public TimeSpan Duration;
    }
}

public class WorkSession
{
    private readonly List<(DateTime Start, DateTime End, TimeSpan Active)> _segments =
        new List<(DateTime, DateTime, TimeSpan)>();

    private readonly List<WorkTimeSlice> _activeSlices = new List<WorkTimeSlice>();

    private DateTime? _segmentStart;
    private DateTime? _activeStart;
    private TimeSpan _activeAccum = TimeSpan.Zero;
    private bool _isPaused = true;

    public Document LastKnownDocument { get; set; }
    public string DocumentName { get; set; }
    public string RevitVersion { get; set; }
    public bool HasOpenSegment => _segmentStart != null;

    public void RefreshMetadata(Document document, UIApplication uiApp)
    {
        LastKnownDocument = document ?? LastKnownDocument;

        try
        {
            if (!string.IsNullOrWhiteSpace(document?.Title))
                DocumentName = document.Title;
        }
        catch { }

        try
        {
            if (uiApp?.Application != null)
                RevitVersion = uiApp.Application.VersionNumber;
        }
        catch { }
    }

    public void StartSegment()
    {
        if (_segmentStart != null) return;
        _segmentStart = DateTime.Now;
        Resume();
    }

    public void StopSegment()
    {
        if (_segmentStart == null) return;

        var end = DateTime.Now;
        CloseActiveSlice(end);

        _segments.Add((_segmentStart.Value, end, _activeAccum));
        _segmentStart = null;
        _activeStart = null;
        _activeAccum = TimeSpan.Zero;
        _isPaused = true;
    }

    public void Pause()
    {
        if (_isPaused) return;

        CloseActiveSlice(DateTime.Now);
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

        return sum < TimeSpan.Zero ? TimeSpan.Zero : sum;
    }

    public List<WorkTimeSlice> GetActiveSlices()
    {
        var list = new List<WorkTimeSlice>(_activeSlices);

        if (_segmentStart != null && !_isPaused && _activeStart.HasValue)
        {
            var now = DateTime.Now;
            if (now > _activeStart.Value)
                list.Add(new WorkTimeSlice(_activeStart.Value, now));
        }

        return list;
    }

    public Dictionary<string, long> GetDailySeconds()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var slice in GetActiveSlices())
        {
            var start = slice.Start;
            var end = slice.End;
            if (end <= start) continue;

            while (start.Date < end.Date)
            {
                var boundary = start.Date.AddDays(1);
                AddDailySeconds(result, start.Date, boundary - start);
                start = boundary;
            }

            AddDailySeconds(result, start.Date, end - start);
        }

        return result;
    }

    private static void AddDailySeconds(Dictionary<string, long> result, DateTime day, TimeSpan duration)
    {
        long seconds = (long)Math.Max(0, duration.TotalSeconds);
        if (seconds <= 0) return;

        string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        result.TryGetValue(key, out var current);
        result[key] = current + seconds;
    }

    private void CloseActiveSlice(DateTime end)
    {
        if (!_activeStart.HasValue) return;

        if (end < _activeStart.Value)
            end = _activeStart.Value;

        var duration = end - _activeStart.Value;
        if (duration > TimeSpan.Zero)
        {
            _activeAccum += duration;
            _activeSlices.Add(new WorkTimeSlice(_activeStart.Value, end));
        }

        _activeStart = null;
    }
}

public struct WorkTimeSlice
{
    public WorkTimeSlice(DateTime start, DateTime end)
    {
        Start = start;
        End = end;
    }

    public DateTime Start { get; }
    public DateTime End { get; }
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
            cpuRatio = cpuDelta.TotalMilliseconds / wallDelta.TotalMilliseconds;

        bool busyRecent = cpuRatio >= _cpuBusyThreshold;

        IntPtr fgWnd = GetForegroundWindow();
        uint fgPid = 0;
        GetWindowThreadProcessId(fgWnd, out fgPid);
        bool revitForeground = (fgPid == (uint)_proc.Id);
        bool otherRevitForeground = !revitForeground && IsRevitProcess(fgPid);

        if (revitForeground) _lastForegroundSeen = now;

        TimeSpan osIdle = GetOsIdleTime();

        bool shouldPause;
        if (revitForeground)
        {
            shouldPause = !busyRecent && osIdle >= _idleThreshold;
        }
        else
        {
            var focusThreshold = otherRevitForeground ? TimeSpan.Zero : _unfocusedThreshold;
            bool lostFocusForLong = (now - _lastForegroundSeen) >= focusThreshold;
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

    public void ForceInactive()
    {
        _inactive = true;
        ResetAfterResume();
    }

    public void ResetAfterResume()
    {
        _lastUpdate = DateTime.UtcNow;
        try { _lastCpu = _proc.TotalProcessorTime; } catch { }
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

    private static bool IsRevitProcess(uint processId)
    {
        if (processId == 0) return false;

        try
        {
            using (var process = Process.GetProcessById((int)processId))
            {
                return process.ProcessName.IndexOf("Revit", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}

internal enum ActivityChange { None, BecameInactive, BecameActive }
