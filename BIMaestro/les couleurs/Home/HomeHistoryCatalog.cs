using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace BIMaestro.Home
{
    /// <summary>Read-only, bounded index of the time logger. Never opens a model or writes the workbook.</summary>
    internal static class HomeHistoryCatalog
    {
        private static readonly object Gate = new object();
        private static string _json = JsonConvert.SerializeObject(new { status = "loading", entries = new object[0] });
        private static string _stamp;
        private static DateTime _nextCheck;
        private static bool _busy;
        private static HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static List<Entry> _entries = new List<Entry>();
        private static DateTime _validatedAt;
        private static int _hostYear = 2026, _revision, _total, _inaccessible, _unsupported, _newer, _pending;
        private static bool _checking;
        private static double _elapsed;
        private static Task<bool> _remoteProbe;
        private static readonly HashSet<string> ThumbnailPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _thumbnailWorker;
        private static int _publishedRevision;
        private sealed class PreviewMemo { internal string Stamp, Url; internal DateTime At; }
        private static readonly Dictionary<string, PreviewMemo> PreviewCache = new Dictionary<string, PreviewMemo>(StringComparer.OrdinalIgnoreCase);
        internal static readonly string SourcePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "Historique_Temps_Revit.xlsx");

        internal static string GetPayloadJson() { lock (Gate) return _json; }
        internal static bool IsRefreshing { get { lock (Gate) return _busy; } }
        internal static bool IsKnownPath(string path) { lock (Gate) return IsModelPath(path) && _known.Contains(path); }
        internal static void ConfigureVersion(string version)
        {
            if (!int.TryParse(version, out int year)) return;
            lock (Gate) { if (_hostYear == year) return; _hostYear = year; _stamp = null; _nextCheck = DateTime.MinValue; }
        }

        internal static void RequestThumbnails(IEnumerable<string> paths)
        {
            if (paths == null) return;
            lock (Gate)
            {
                foreach (string path in paths.Take(96)) if (path != null && _known.Contains(path)) ThumbnailPending.Add(path);
                if (_thumbnailWorker || ThumbnailPending.Count == 0) return;
                _thumbnailWorker = true;
            }
            Task.Run(() =>
            {
                bool released = false;
                try
                {
                    for (;;)
                    {
                        Entry entry;
                        lock (Gate)
                        {
                            if (ThumbnailPending.Count == 0) { _thumbnailWorker = false; released = true; return; }
                            string path = ThumbnailPending.First(); ThumbnailPending.Remove(path);
                            entry = _entries.FirstOrDefault(e => string.Equals(e.path, path, StringComparison.OrdinalIgnoreCase));
                        }
                        if (entry == null || !IsFilesystemPath(entry.path) || IsRemote(entry.path)) continue;
                        try
                        {
                            var info = new FileInfo(entry.path);
                            if (!info.Exists)
                            {
                                lock (Gate) { _entries.RemoveAll(e => string.Equals(e.path, entry.path, StringComparison.OrdinalIgnoreCase)); _known.Remove(entry.path); }
                                Publish(); continue;
                            }
                            string stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
                            PreviewMemo memo;
                            lock (Gate) PreviewCache.TryGetValue(entry.path, out memo);
                            if (memo == null || memo.Stamp != stamp || DateTime.UtcNow - memo.At > TimeSpan.FromMinutes(5))
                            {
                                byte[] bytes = HomeEmbeddedPreview.TryRead(entry.path);
                                memo = new PreviewMemo { Stamp = stamp, At = DateTime.UtcNow,
                                    Url = bytes == null ? null : "data:image/png;base64," + Convert.ToBase64String(bytes) };
                                lock (Gate)
                                {
                                    PreviewCache[entry.path] = memo;
                                    // Bound decoded payload memory as the user pages through a large journal.
                                    while (PreviewCache.Count > 256 || PreviewCache.Values.Sum(v => (long)(v.Url?.Length ?? 0)) > 16 * 1024 * 1024)
                                    {
                                        string oldest = PreviewCache.OrderBy(p => p.Value.At).First().Key;
                                        PreviewCache.Remove(oldest);
                                        var oldEntry = _entries.FirstOrDefault(e => string.Equals(e.path, oldest, StringComparison.OrdinalIgnoreCase));
                                        if (oldEntry != null) oldEntry.thumbnailDataUrl = null;
                                    }
                                }
                            }
                            bool changed = false;
                            lock (Gate)
                            {
                                var current = _entries.FirstOrDefault(e => string.Equals(e.path, entry.path, StringComparison.OrdinalIgnoreCase));
                                if (current != null && (!current.thumbnailAttempted || current.thumbnailDataUrl != memo.Url)) { current.thumbnailDataUrl = memo.Url; current.thumbnailAttempted = true; changed = true; }
                            }
                            if (changed) Publish();
                        }
                        catch
                        {
                            // A failed optional read must not cause a polling loop on the UI side.
                            lock (Gate)
                            {
                                var current = _entries.FirstOrDefault(e => string.Equals(e.path, entry.path, StringComparison.OrdinalIgnoreCase));
                                if (current != null) current.thumbnailAttempted = true;
                                PreviewCache[entry.path] = new PreviewMemo { Stamp = entry.fileStamp, At = DateTime.UtcNow };
                            }
                            Publish();
                        }
                    }
                }
                finally
                {
                    if (!released)
                    {
                        string[] restart;
                        lock (Gate) { _thumbnailWorker = false; restart = ThumbnailPending.ToArray(); }
                        if (restart.Length > 0) RequestThumbnails(restart);
                    }
                }
            });
        }

        private static void Publish()
        {
            // Called by background workers only; serialization never runs in a Revit UI callback.
            object snapshot; int revision;
            lock (Gate) { revision = ++_revision; snapshot = new { status = "ready", source = SourcePath,
                revision = revision.ToString(CultureInfo.InvariantCulture), checking = _checking, pending = _pending,
                totalLogged = _total, inaccessible = _inaccessible, unsupported = _unsupported, newerVersion = _newer,
                hostVersion = _hostYear, elapsedMs = _elapsed, entries = _entries.OrderByDescending(e => e.recordedVersion == _hostYear)
                    .ThenByDescending(e => e.recordedVersion).ThenByDescending(e => e.lastOpened, StringComparer.Ordinal)
                    .Select(e => new { e.path, e.filename, e.extension, e.lastOpened, e.lastLoggedActivity,
                        e.thumbnailPath, e.thumbnailDataUrl, e.thumbnailAttempted, e.fileStamp, e.recordedVersion, versions = e.versions.ToArray() }).ToArray() }; }
            string json = JsonConvert.SerializeObject(snapshot);
            lock (Gate) if (revision > _publishedRevision) { _publishedRevision = revision; _json = json; }
        }

        internal static void RequestRefresh(bool force = false)
        {
            lock (Gate)
            {
                if (_busy || (!force && DateTime.UtcNow < _nextCheck)) return;
                _busy = true;
                _nextCheck = DateTime.UtcNow.AddSeconds(30);
            }
            Task.Run(() =>
            {
                try
                {
                    var info = new FileInfo(SourcePath);
                    if (!info.Exists) throw new FileNotFoundException("Votre journal Excel n’existe pas encore.");
                    string stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
                    lock (Gate) { if (!force && stamp == _stamp && DateTime.UtcNow - _validatedAt < TimeSpan.FromMinutes(5)) return; }
                    var timer = Stopwatch.StartNew();
                    var entries = Read();
                    lock (Gate) { _entries.Clear(); _known.Clear(); _total = entries.Count; _pending = entries.Count;
                        _inaccessible = _unsupported = _newer = 0; _checking = true; }
                    Publish();
                    bool remoteBlocked = false;
                    foreach (var entry in entries.OrderBy(e => IsRemote(e.path)))
                    {
                        bool accepted = false;
                        if (entry.recordedVersion > _hostYear) { lock (Gate) _newer++; }
                        else if (!IsFilesystemPath(entry.path)) { lock (Gate) _unsupported++; }
                        else if (IsRemote(entry.path))
                        {
                            if (!remoteBlocked)
                            {
                                if (_remoteProbe != null && !_remoteProbe.IsCompleted) remoteBlocked = true;
                                else
                                {
                                    string candidate = entry.path;
                                    _remoteProbe = Task.Run(() => File.Exists(candidate));
                                    if (_remoteProbe.Wait(350)) accepted = _remoteProbe.Result;
                                    else remoteBlocked = true;
                                }
                            }
                            if (!accepted) { lock (Gate) _unsupported++; }
                        }
                        else
                        {
                            try { var file = new FileInfo(entry.path); accepted = file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) == 0;
                                if (accepted) { using (var probe = new FileStream(entry.path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) probe.ReadByte();
                                    entry.fileStamp = file.LastWriteTimeUtc.Ticks + ":" + file.Length;
                                    lock (Gate) if (PreviewCache.TryGetValue(entry.path, out PreviewMemo memo) && memo.Stamp == entry.fileStamp && DateTime.UtcNow - memo.At < TimeSpan.FromMinutes(5))
                                    { entry.thumbnailDataUrl = memo.Url; entry.thumbnailAttempted = true; }
                                } }
                            catch { accepted = false; }
                            if (!accepted) { lock (Gate) _inaccessible++; }
                        }
                        lock (Gate) { _pending--; if (accepted) { _entries.Add(entry); _known.Add(entry.path); } }
                        if (_pending % 16 == 0) { _elapsed = timer.Elapsed.TotalMilliseconds; Publish(); }
                    }
                    lock (Gate) { _stamp = stamp; _validatedAt = DateTime.UtcNow; _checking = false; _elapsed = timer.Elapsed.TotalMilliseconds; }
                    Publish();
                }
                catch (Exception ex)
                {
                    // Retain no stale success indication when the current source cannot be read.
                    lock (Gate)
                    {
                        _known.Clear();
                        _entries.Clear();
                        _checking = false;
                        _publishedRevision = ++_revision;
                        _stamp = null;
                        _json = JsonConvert.SerializeObject(new { status = "error", source = SourcePath,
                            error = "Lecture du journal impossible : " + ex.Message, entries = new object[0] });
                    }
                }
                finally { lock (Gate) _busy = false; }
            });
        }

        private sealed class Entry
        {
            public string path, filename, extension, lastOpened, lastLoggedActivity, thumbnailPath = null, thumbnailDataUrl, fileStamp;
            public bool thumbnailAttempted;
            public int recordedVersion;
            public List<string> versions = new List<string>();
        }

        private static List<Entry> Read()
        {
            var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            using (var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var book = new XSSFWorkbook(stream))
            {
                var sheet = book.GetSheet("Historique_Temps_Revit");
                if (sheet == null) throw new InvalidDataException("Feuille Historique_Temps_Revit absente.");
                var header = sheet.GetRow(0);
                if (header == null) throw new InvalidDataException("En-têtes du journal absents.");
                var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.LastCellNum; c++) { string key = Text(header.GetCell(c)); if (key.Length > 0) columns[key] = c; }
                if (!columns.ContainsKey("Document ID") && !columns.ContainsKey("Local Path"))
                    throw new InvalidDataException("Colonnes de chemins absentes.");
                Func<IRow, string, string> value = (row, key) => columns.TryGetValue(key, out int c) ? Text(row.GetCell(c)) : "";
                // The workbook belongs to the logger; bound work for unexpectedly large histories.
                int end = Math.Min(sheet.LastRowNum, 200000);
                for (int r = 1; r <= end; r++)
                {
                    var row = sheet.GetRow(r); if (row == null) continue;
                    string path = null;
                    foreach (string key in new[] { "Central Path", "Local Path", "Document ID" })
                    {
                        string candidate = value(row, key).Split('|').Last().Trim();
                        if (IsModelPath(candidate)) { path = candidate; break; }
                    }
                    if (path == null) continue;
                    string normalized = path.Replace('/', '\\');
                    if (!map.TryGetValue(normalized, out Entry entry))
                        map[normalized] = entry = new Entry { path = path, filename = normalized.Substring(normalized.LastIndexOf('\\') + 1),
                            extension = Path.GetExtension(path).ToLowerInvariant(), lastLoggedActivity = "" };
                    string when = value(row, "Date") + "T" + value(row, "Time");
                    if (string.CompareOrdinal(when, entry.lastLoggedActivity) > 0) entry.lastLoggedActivity = when;
                    string evt = value(row, "Event");
                    if ((evt.Equals("Ouvert", StringComparison.OrdinalIgnoreCase) || evt.Equals("Opened", StringComparison.OrdinalIgnoreCase)) &&
                        string.CompareOrdinal(when, entry.lastOpened) > 0) entry.lastOpened = when;
                    string version = value(row, "Revit Version");
                    if (version.Length > 0 && !entry.versions.Contains(version)) entry.versions.Add(version);
                    var match = Regex.Match(version, @"20\d{2}");
                    if (match.Success && int.TryParse(match.Value, out int year)) entry.recordedVersion = Math.Max(entry.recordedVersion, year);
                }
            }
            var result = map.Values.Where(e => !string.IsNullOrEmpty(e.lastOpened)).OrderByDescending(e => e.lastOpened, StringComparer.Ordinal).Take(10000).ToList();
            return result;
        }

        private static bool IsFilesystemPath(string path) => path.StartsWith("\\\\", StringComparison.Ordinal) || (path.Length > 2 && path[1] == ':');
        private static bool IsRemote(string path)
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
            if (!IsFilesystemPath(path)) return false;
            try { return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network; } catch { return true; }
        }

        internal static bool IsModelPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return false;
            if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) return false;
            return (path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
                path.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase);
        }

        private static string Text(ICell cell)
        {
            if (cell == null) return "";
            if (cell.CellType == CellType.Numeric && DateUtil.IsCellDateFormatted(cell))
                return cell.DateCellValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            return cell.ToString().Trim();
        }
    }
}
