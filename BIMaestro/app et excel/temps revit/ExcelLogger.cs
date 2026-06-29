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
    private const string SnapshotFormatVersion = "v3";

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

    private static readonly string SnapshotTempDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "RevitLogs", "Temp");

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

                CleanupSnapshotTempFilesCore();

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
                        EndDocumentSessionLogFallback(key, ws.DocumentName, ws.RevitVersion, ws.GetTotalActiveDuration(), ws.GetDailySeconds(), ws.Classification);

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
                RevitVersion = SafeGetRevitVersion(uiApp),
                Classification = TimeLogMetadataExtractor.Classify(document, uiApp, key, SafeGetDocumentName(document))
            };

            _sessions[key] = ws;
            StartDocumentSessionLog(document, uiApp, ws.Classification);
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
        ws.Cells["H1"].Value = "Session ID";
        ws.Cells["I1"].Value = "Document Kind";
        ws.Cells["J1"].Value = "Is Workshared";
        ws.Cells["K1"].Value = "Central Path";
        ws.Cells["L1"].Value = "Local Path";
        ws.Cells["M1"].Value = "Path Hash";
        ws.Cells["N1"].Value = "Revit User";
        ws.Cells["O1"].Value = "Windows User";
        ws.Cells["P1"].Value = "Machine";
        ws.Cells["Q1"].Value = "Process ID";
        ws.Cells["R1"].Value = "Project Info JSON";
        ws.Column(7).Style.Numberformat.Format = "[hh]:mm:ss";
        }

    private static void LogEvent(string eventType, Document document, UIApplication uiApp, TimeSpan duration, TimeLogMetadata classification = null)
    {
        try
        {
            classification = classification ?? TimeLogMetadataExtractor.Classify(document, uiApp, SafeGetDocumentKey(document), SafeGetDocumentName(document));

            var entry = new LogEntry
            {
                EventType = eventType,
                DocumentId = SafeGetDocumentKey(document),
                DocumentName = SafeGetDocumentName(document),
                RevitVersion = SafeGetRevitVersion(uiApp),
                When = DateTime.Now,
                Duration = duration,
                Classification = classification
            };

            AppendLogEntries(new[] { entry });
        }
        catch { }
    }

    private static void LogClosedSession(
        string documentKey,
        string documentName,
        string revitVersion,
        IEnumerable<WorkTimeSlice> activeSlices,
        TimeSpan fallbackDuration,
        TimeLogMetadata classification)
    {
        try
        {
            var entries = BuildClosedLogEntries(documentKey, documentName, revitVersion, activeSlices, fallbackDuration, classification);
            AppendLogEntries(entries);
        }
        catch { }
    }

    private static List<LogEntry> BuildClosedLogEntries(
        string documentKey,
        string documentName,
        string revitVersion,
        IEnumerable<WorkTimeSlice> activeSlices,
        TimeSpan fallbackDuration,
        TimeLogMetadata classification)
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
                AddClosedEntry(entries, documentKey, documentName, revitVersion, boundary.AddTicks(-1), boundary - start, classification);
                start = boundary;
            }

            AddClosedEntry(entries, documentKey, documentName, revitVersion, end, end - start, classification);
        }

        if (entries.Count == 0 && fallbackDuration > TimeSpan.Zero)
            AddClosedEntry(entries, documentKey, documentName, revitVersion, DateTime.Now, fallbackDuration, classification);

        return entries;
    }

    private static void AddClosedEntry(
        List<LogEntry> entries,
        string documentKey,
        string documentName,
        string revitVersion,
        DateTime when,
        TimeSpan duration,
        TimeLogMetadata classification)
    {
        if (duration.TotalSeconds < 1) return;

        entries.Add(new LogEntry
        {
            EventType = "Fermé",
            DocumentId = documentKey,
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "(unknown)" : documentName,
            RevitVersion = string.IsNullOrWhiteSpace(revitVersion) ? "(unknown)" : revitVersion,
            When = when,
            Duration = duration,
            Classification = classification ?? TimeLogMetadataExtractor.ClassifyFromLog(documentKey, documentName, null, null, null, null)
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
                ws.Cells[lastRow, 8].Value = entry.Classification?.SessionId;
                ws.Cells[lastRow, 9].Value = entry.Classification?.DocumentKind;
                ws.Cells[lastRow, 10].Value = entry.Classification?.IsWorkshared;
                ws.Cells[lastRow, 11].Value = entry.Classification?.CentralPath;
                ws.Cells[lastRow, 12].Value = entry.Classification?.LocalPath;
                ws.Cells[lastRow, 13].Value = entry.Classification?.PathHash;
                ws.Cells[lastRow, 14].Value = entry.Classification?.RevitUser;
                ws.Cells[lastRow, 15].Value = entry.Classification?.WindowsUser;
                ws.Cells[lastRow, 16].Value = entry.Classification?.MachineName;
                ws.Cells[lastRow, 17].Value = entry.Classification?.ProcessId;
                ws.Cells[lastRow, 18].Value = entry.Classification?.ProjectInfoJson;
            }

            package.Save();
        }
    }

    public static void StartDocumentSessionLog(Document document, UIApplication uiApp)
        => LogEvent("Ouvert", document, uiApp, TimeSpan.Zero);

    public static void StartDocumentSessionLog(Document document, UIApplication uiApp, TimeLogMetadata classification)
        => LogEvent("Ouvert", document, uiApp, TimeSpan.Zero, classification);

    public static void EndDocumentSessionLog(Document document, UIApplication uiApp, WorkSession session)
    {
        string documentKey = SafeGetDocumentKey(document);
        string documentName = SafeGetDocumentName(document);
        string revitVersion = SafeGetRevitVersion(uiApp);

        var classification = session?.Classification
                             ?? TimeLogMetadataExtractor.Classify(document, uiApp, documentKey, documentName);

        LogClosedSession(
            documentKey,
            documentName,
            revitVersion,
            session?.GetActiveSlices(),
            session?.GetTotalActiveDuration() ?? TimeSpan.Zero,
            classification);
    }

    public static void EndDocumentSessionLog(Document document, UIApplication uiApp, TimeSpan activeDuration)
        => LogEvent("Fermé", document, uiApp, activeDuration);

    public static void TouchHeartbeat(Document document, UIApplication uiApp, TimeSpan activeDuration) { }

    public static void EndDocumentSessionLogFallback(string documentKey, TimeSpan activeDuration)
        => EndDocumentSessionLogFallback(documentKey, "(unknown)", "(unknown)", activeDuration, null, null);

    private static void EndDocumentSessionLogFallback(
        string documentKey,
        string documentName,
        string revitVersion,
        TimeSpan activeDuration,
        Dictionary<string, long> dailySeconds,
        TimeLogMetadata classification)
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
                        TimeSpan.FromSeconds(Math.Max(0, kv.Value)),
                        classification ?? TimeLogMetadataExtractor.ClassifyFromLog(documentKey, documentName, null, null, null, null));
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
                Duration = activeDuration,
                Classification = classification ?? TimeLogMetadataExtractor.ClassifyFromLog(documentKey, documentName, null, null, null, null)
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
        public TimeLogMetadata Classification;
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
                DailySeconds = dailySeconds,
                Classification = session.Classification ?? TimeLogMetadataExtractor.ClassifyFromLog(docKey, session.DocumentName, null, null, null, null)
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
                        s.DailySeconds,
                        s.Classification);

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
        Dictionary<string, long> dailySeconds,
        TimeLogMetadata classification)
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
                    TimeSpan.FromSeconds(Math.Max(0, kv.Value)),
                    classification ?? TimeLogMetadataExtractor.ClassifyFromLog(documentKey, documentName, null, null, null, null));
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
                Duration = activeDuration,
                Classification = classification ?? TimeLogMetadataExtractor.ClassifyFromLog(documentKey, documentName, null, null, null, null)
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
            return ParseV3Snapshot(parts);

        if (string.Equals(parts[0], "v2", StringComparison.Ordinal))
            return ParseV2Snapshot(parts);

        return ParseLegacySnapshot(parts);
    }

    private static Snapshot ParseV3Snapshot(string[] parts)
    {
        try
        {
            if (parts.Length < 10) return null;

            var snapshot = ParseV2Snapshot(parts);
            if (snapshot == null) return null;

            if (parts.Length >= 21)
            {
                snapshot.Classification = new TimeLogMetadata
                {
                    SessionId = DecodeField(parts[10]),
                    DocumentKind = DecodeField(parts[11]),
                    IsWorkshared = DecodeField(parts[12]),
                    CentralPath = DecodeField(parts[13]),
                    LocalPath = DecodeField(parts[14]),
                    PathHash = DecodeField(parts[15]),
                    RevitUser = DecodeField(parts[16]),
                    WindowsUser = DecodeField(parts[17]),
                    MachineName = DecodeField(parts[18]),
                    ProcessId = DecodeField(parts[19]),
                    ProjectInfoJson = DecodeField(parts[20])
                };
            }

            return snapshot;
        }
        catch
        {
            return null;
        }
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
                DailySeconds = daily,
                Classification = TimeLogMetadataExtractor.ClassifyFromLog(DecodeField(parts[2]), DecodeField(parts[3]), null, null, null, null)
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
                DailySeconds = new Dictionary<string, long>(StringComparer.Ordinal),
                Classification = TimeLogMetadataExtractor.ClassifyFromLog(docKey, parts[nameIndex], null, null, null, null)
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
        string tempPath = null;

        try
        {
            if (map == null || map.Count == 0)
            {
                TryDeleteSnapshotFileCore();
                return;
            }

            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);

            if (!Directory.Exists(SnapshotTempDirectory))
                Directory.CreateDirectory(SnapshotTempDirectory);

            tempPath = Path.Combine(
                SnapshotTempDirectory,
                "TimeSnapshots.dat." + CurrentProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".tmp");

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
                        EncodeField(SerializeDailySeconds(s.DailySeconds)),
                        EncodeField(s.Classification?.SessionId),
                        EncodeField(s.Classification?.DocumentKind),
                        EncodeField(s.Classification?.IsWorkshared),
                        EncodeField(s.Classification?.CentralPath),
                        EncodeField(s.Classification?.LocalPath),
                        EncodeField(s.Classification?.PathHash),
                        EncodeField(s.Classification?.RevitUser),
                        EncodeField(s.Classification?.WindowsUser),
                        EncodeField(s.Classification?.MachineName),
                        EncodeField(s.Classification?.ProcessId),
                        EncodeField(s.Classification?.ProjectInfoJson)
                    }));
                }
            }

            CopyFileWithRetry(tempPath, SnapshotPath, true);

            try { File.SetAttributes(SnapshotPath, FileAttributes.Hidden); } catch { }
        }
        catch { }
        finally
        {
            TryDeleteFileWithRetry(tempPath);
            CleanupSnapshotTempFilesCore();
        }
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
        TryDeleteFileWithRetry(SnapshotPath);
    }

    private static void CleanupSnapshotTempFilesCore()
    {
        try
        {
            if (Directory.Exists(LogDirectory))
            {
                foreach (var file in Directory.GetFiles(LogDirectory, "TimeSnapshots.dat.*.tmp"))
                    TryDeleteFileWithRetry(file);
            }

            if (Directory.Exists(SnapshotTempDirectory))
            {
                foreach (var file in Directory.GetFiles(SnapshotTempDirectory, "TimeSnapshots.dat.*.tmp"))
                    TryDeleteFileWithRetry(file);
            }
        }
        catch { }
    }

    private static void CopyFileWithRetry(string sourcePath, string destinationPath, bool overwrite)
    {
        const int attempts = 6;

        for (int i = 0; i < attempts; i++)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite);
                return;
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (i < attempts - 1)
            {
                Thread.Sleep(150);
            }
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    private static void TryDeleteFileWithRetry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        const int attempts = 6;

        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (i < attempts - 1)
            {
                Thread.Sleep(150);
            }
            catch
            {
                return;
            }
        }
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
        public TimeLogMetadata Classification;
    }
}

public class TimeLogMetadata
{
    public string SessionId { get; set; }
    public string DocumentKind { get; set; }
    public string IsWorkshared { get; set; }
    public string CentralPath { get; set; }
    public string LocalPath { get; set; }
    public string PathHash { get; set; }
    public string RevitUser { get; set; }
    public string WindowsUser { get; set; }
    public string MachineName { get; set; }
    public string ProcessId { get; set; }
    public string ProjectInfoJson { get; set; }
}

public static class TimeLogMetadataExtractor
{
    public static TimeLogMetadata Classify(Document document, UIApplication uiApp, string documentKey, string documentName)
    {
        string localPath = TryGetLocalPath(document);
        string centralPath = TryGetCentralPath(document);
        string stablePath = NormalizeDocumentId(FirstNonEmpty(centralPath, localPath, documentKey));

        return new TimeLogMetadata
        {
            SessionId = Guid.NewGuid().ToString("N"),
            DocumentKind = GetDocumentKind(FirstNonEmpty(localPath, centralPath, documentKey, documentName)),
            IsWorkshared = SafeIsWorkshared(document),
            CentralPath = centralPath,
            LocalPath = localPath,
            PathHash = ComputeStableHash(stablePath),
            RevitUser = TryGetRevitUser(uiApp),
            WindowsUser = Environment.UserName,
            MachineName = Environment.MachineName,
            ProcessId = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
            ProjectInfoJson = ExtractProjectInfoJson(document)
        };
    }

    public static TimeLogMetadata ClassifyFromLog(
        string documentId,
        string documentName,
        string sessionId,
        string documentKind,
        string isWorkshared,
        string centralPath)
    {
        string localPath = NormalizeDocumentId(documentId);
        string stablePath = FirstNonEmpty(centralPath, localPath, documentName);

        return new TimeLogMetadata
        {
            SessionId = FirstNonEmpty(sessionId, Guid.NewGuid().ToString("N")),
            DocumentKind = FirstNonEmpty(documentKind, GetDocumentKind(stablePath)),
            IsWorkshared = FirstNonEmpty(isWorkshared, string.Empty),
            CentralPath = centralPath,
            LocalPath = localPath,
            PathHash = ComputeStableHash(stablePath),
            RevitUser = string.Empty,
            WindowsUser = Environment.UserName,
            MachineName = Environment.MachineName,
            ProcessId = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
            ProjectInfoJson = string.Empty
        };
    }

    public static string NormalizeDocumentId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            return documentId;

        var parts = documentId.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(p => p.Trim())
                              .Where(p => !string.IsNullOrWhiteSpace(p))
                              .ToArray();

        return parts.Length > 1 ? parts[parts.Length - 1] : documentId.Trim();
    }

    private static string TryGetLocalPath(Document document)
    {
        try { return CleanValue(document?.PathName); }
        catch { return null; }
    }

    private static string TryGetCentralPath(Document document)
    {
        try
        {
            if (document == null || !document.IsWorkshared) return null;

            var centralModelPath = document.GetWorksharingCentralModelPath();
            return centralModelPath == null
                ? null
                : CleanValue(ModelPathUtils.ConvertModelPathToUserVisiblePath(centralModelPath));
        }
        catch
        {
            return null;
        }
    }

    private static string SafeIsWorkshared(Document document)
    {
        try { return document?.IsWorkshared == true ? "true" : "false"; }
        catch { return string.Empty; }
    }

    private static string GetDocumentKind(string pathOrName)
    {
        string text = (pathOrName ?? string.Empty).Trim().ToLowerInvariant();
        if (text.EndsWith(".rfa")) return "RFA";
        if (text.EndsWith(".rvt")) return "RVT";

        return string.Empty;
    }

    private static string TryGetRevitUser(UIApplication uiApp)
    {
        try { return CleanValue(uiApp?.Application?.Username); }
        catch { return null; }
    }

    private static string ExtractProjectInfoJson(Document document)
    {
        try
        {
            var info = document?.ProjectInformation;
            if (info == null) return string.Empty;

            var items = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in info.Parameters)
            {
                string name = CleanValue(parameter?.Definition?.Name);
                if (string.IsNullOrWhiteSpace(name)) continue;

                string value = ReadParameterValue(parameter);
                if (!string.IsNullOrWhiteSpace(value))
                    items[name] = value;
            }

            return ToJson(items);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadParameterValue(Parameter parameter)
    {
        try
        {
            if (parameter == null) return null;

            string asValueString = CleanValue(parameter.AsValueString());
            if (!string.IsNullOrWhiteSpace(asValueString))
                return asValueString;

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return CleanValue(parameter.AsString());
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    return parameter.AsElementId()?.IntegerValue.ToString(CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }
        catch { return null; }
    }

    private static string ToJson(SortedDictionary<string, string> items)
    {
        if (items == null || items.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append('{');

        bool first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(',');
            first = false;

            sb.Append('"').Append(JsonEscape(item.Key)).Append("\":\"");
            sb.Append(JsonEscape(item.Value)).Append('"');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string ComputeStableHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in value.Trim().ToUpperInvariant())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            return hash.ToString("X16", CultureInfo.InvariantCulture);
        }
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string CleanValue(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
    public TimeLogMetadata Classification { get; set; }
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

        try
        {
            var updated = TimeLogMetadataExtractor.Classify(document, uiApp, null, DocumentName);
            if (!string.IsNullOrWhiteSpace(Classification?.SessionId))
                updated.SessionId = Classification.SessionId;

            Classification = updated;
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
