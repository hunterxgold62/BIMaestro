using Newtonsoft.Json;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Snapshot = BIMaestro.ModelLoading.ModelOpeningService.Snapshot;
using Preview = BIMaestro.ModelLoading.ModelOpeningService.Preview;

namespace BIMaestro.ModelLoading
{
    // Runs only on a worker, before document opening. Paths, not filenames, identify models.
    internal static class ModelOpeningHistory
    {
        private sealed class Session
        {
            public DateTime When;
            public double Hours;
        }
        private sealed class Model
        {
            public Snapshot Snapshot;
            public DateTime LatestRow;
            public readonly Dictionary<string, Session> Sessions = new Dictionary<string, Session>();
        }
        internal static string Normalize(string key) => (key ?? "").Trim().Replace('/', '\\');
        private static string LegacyId(string id) => (id ?? "").Split('|').Last().Trim();
        internal static List<Snapshot> ReadHistory(string workbookPath, ISet<string> excludeSessions = null)
        {
            var models = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(workbookPath)) return new List<Snapshot>();
            using var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var book = new XSSFWorkbook(stream);
            var sheet = book.GetSheet("Historique_Temps_Revit");
            if (sheet == null) throw new InvalidDataException("Historique_Temps_Revit is missing.");
            var format = new DataFormatter();
            DateTime week = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
            for (int i = sheet.FirstRowNum + 1; i <= sheet.LastRowNum; i++)
            {
                var row = sheet.GetRow(i); if (row == null) continue;
                string Cell(int column) => format.FormatCellValue(row.GetCell(column)).Trim();
                string id = LegacyId(Cell(1)), central = Cell(10), local = Cell(11);
                var candidates = new[] { central, id, local }.Where(v => !string.IsNullOrWhiteSpace(v) && v.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (candidates.Count == 0) continue;
                string key = candidates[0];
                var known = candidates.Select(v => models.TryGetValue(Normalize(v), out var existing) ? existing : null).Where(v => v != null).Distinct().ToList();
                var model = known.FirstOrDefault() ?? new Model { Snapshot = new Snapshot { File = key, Name = Cell(2), Hours = 0, WeekHours = 0 } };
                foreach (var other in known.Skip(1))
                {
                    Merge(model, other);
                    foreach (var alias in other.Snapshot.Aliases) models[Normalize(alias)] = model;
                }
                if (!string.IsNullOrWhiteSpace(central) && central.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) model.Snapshot.File = central;
                var snapshot = model.Snapshot;
                foreach (string alias in candidates)
                {
                    if (!snapshot.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) snapshot.Aliases.Add(alias);
                    models[Normalize(alias)] = model;
                }
                if (!TryDate(Cell(4), Cell(5), out var when)) continue;
                if (when >= model.LatestRow)
                {
                    model.LatestRow = when; snapshot.Name = Cell(2);
                    if (bool.TryParse(Cell(9), out bool shared)) snapshot.IsWorkshared = shared;
                }
                string kind = Cell(0);
                if (kind.Equals("Ouvert", StringComparison.OrdinalIgnoreCase) && when > snapshot.LastOpened) snapshot.LastOpened = when;
                if (!kind.Equals("Fermé", StringComparison.OrdinalIgnoreCase)) continue;
                if (excludeSessions != null && excludeSessions.Contains(Cell(7))) continue;
                double hours = Hours(row.GetCell(6), format);
                if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0) continue;
                snapshot.Hours += hours;
                if (when.Date >= week) snapshot.WeekHours += hours;
                string sessionId = Cell(7);
                // Older workbooks have no session ID: a row is one known session.
                if (string.IsNullOrEmpty(sessionId)) sessionId = "legacy-row-" + i.ToString(CultureInfo.InvariantCulture);
                if (!model.Sessions.TryGetValue(sessionId, out var session)) model.Sessions[sessionId] = session = new Session();
                session.Hours += hours;
                if (when > session.When) session.When = when;
            }
            foreach (var model in models.Values.Distinct())
                model.Snapshot.LastSessionHours = model.Sessions.Values.OrderByDescending(v => v.When).FirstOrDefault()?.Hours;
            return models.Values.Distinct().Select(m => m.Snapshot).ToList();
        }
        private static void Merge(Model target, Model source)
        {
            target.Snapshot.Hours += source.Snapshot.Hours;
            target.Snapshot.WeekHours += source.Snapshot.WeekHours;
            if (source.Snapshot.LastOpened > target.Snapshot.LastOpened) target.Snapshot.LastOpened = source.Snapshot.LastOpened;
            if (source.LatestRow > target.LatestRow)
            {
                target.LatestRow = source.LatestRow; target.Snapshot.Name = source.Snapshot.Name;
                target.Snapshot.IsWorkshared = source.Snapshot.IsWorkshared ?? target.Snapshot.IsWorkshared;
            }
            target.Snapshot.Aliases.AddRange(source.Snapshot.Aliases.Where(v => !target.Snapshot.Aliases.Contains(v, StringComparer.OrdinalIgnoreCase)));
            foreach (var pair in source.Sessions)
            {
                if (target.Sessions.TryGetValue(pair.Key, out var prior))
                {
                    prior.Hours += pair.Value.Hours;
                    if (pair.Value.When > prior.When) prior.When = pair.Value.When;
                }
                else target.Sessions[pair.Key] = pair.Value;
            }
        }
        private static bool TryDate(string date, string time, out DateTime when)
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.CurrentCulture })
                if (DateTime.TryParse(date + " " + time, culture, DateTimeStyles.AllowWhiteSpaces, out when)) return true;
            when = default; return false;
        }
        private static double Hours(ICell cell, DataFormatter formatter)
        {
            if (cell == null) return 0;
            if (cell.CellType == CellType.Numeric) return cell.NumericCellValue * 24;
            string value = formatter.FormatCellValue(cell);
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("fr-FR") })
            {
                if (TimeSpan.TryParse(value, culture, out var duration)) return duration.TotalHours;
                if (double.TryParse(value, NumberStyles.Float, culture, out double days)) return days * 24;
            }
            return double.NaN;
        }
        internal static string Hash(string key)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        internal static List<Preview> ReadPreviews(Snapshot model, string cacheRoot)
        {
            var previews = new List<Preview>(model.Views ?? new List<Preview>());
            foreach (string key in model.Aliases.Concat(new[] { model.File }).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct())
            {
                string folder = Path.Combine(cacheRoot, Hash(key.Trim()).Substring(0, 20));
                if (!Directory.Exists(folder)) continue;
                foreach (string image in Directory.EnumerateFiles(folder, "*.png"))
                {
                    try
                    {
                        string metadata = image + ".view.json";
                        Preview preview = null;
                        if (File.Exists(metadata))
                        {
                            try { preview = JsonConvert.DeserializeObject<Preview>(File.ReadAllText(metadata)); } catch { }
                        }
                        // Existing files have no type metadata: use their real modification time,
                        // without guessing whether their image depicts a 3D view, plan or sheet.
                        preview ??= new Preview { Id = "legacy-png:" + Path.GetFileNameWithoutExtension(image), Name = "Aperçu enregistré" };
                        preview.Path = image;
                        preview.Seen = File.GetLastWriteTime(image);
                        previews.Add(preview);
                    }
                    catch { }
                }
            }
            var available = previews.Where(v => !string.IsNullOrWhiteSpace(v.Path) && File.Exists(v.Path))
                .OrderByDescending(v => !string.IsNullOrEmpty(v.ViewType)).ThenByDescending(v => v.Seen)
                .GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var known3d = available.Where(v => v.ViewType == "ThreeD").ToList();
            return (known3d.Count > 0 ? known3d : available).OrderByDescending(v => v.Seen).Take(3).ToList();
        }
    }
}
