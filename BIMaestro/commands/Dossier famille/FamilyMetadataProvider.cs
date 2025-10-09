using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Famille
{
    internal static class FamilyMetadataProvider
    {
        private sealed class MetadataRequest
        {
            public string FamilyPath;
            public TaskCompletionSource<string> Tcs;
        }

        private sealed class Handler : IExternalEventHandler
        {
            private readonly Queue<MetadataRequest> _queue;
            public Handler(Queue<MetadataRequest> queue) => _queue = queue;
            public string GetName() => nameof(FamilyMetadataProvider);

            public void Execute(UIApplication app)
            {
                MetadataRequest request;
                while ((request = Dequeue()) != null)
                {
                    string result = null;
                    try
                    {
                        result = ExtractOmniClassNumber(app, request.FamilyPath);
                    }
                    catch
                    {
                        result = null;
                    }
                    request.Tcs.TrySetResult(result);
                }
            }

            private MetadataRequest Dequeue()
            {
                lock (_queue)
                {
                    return _queue.Count > 0 ? _queue.Dequeue() : null;
                }
            }
        }

        private static readonly Queue<MetadataRequest> _queue = new();
        private static ExternalEvent _eventRef;
        private static Handler _handler;

        public static bool IsAvailable => _eventRef != null;

        public static void Initialize(UIApplication app)
        {
            if (app == null || _handler != null) return;

            _handler = new Handler(_queue);
            try
            {
                _eventRef = ExternalEvent.Create(_handler);
            }
            catch
            {
                _handler = null;
                _eventRef = null;
            }
        }

        public static Task<string> RequestOmniClassNumberAsync(string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath) || _eventRef == null)
                return Task.FromResult<string>(null);

            var request = new MetadataRequest
            {
                FamilyPath = familyPath,
                Tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            lock (_queue)
            {
                _queue.Enqueue(request);
            }

            try
            {
                _eventRef.Raise();
            }
            catch
            {
                request.Tcs.TrySetResult(null);
            }

            return request.Tcs.Task;
        }

        private static string ExtractOmniClassNumber(UIApplication uiapp, string path)
        {
            if (uiapp == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            if (WouldUpgrade(uiapp.Application, path))
                return null;

            Document famDoc = null;
            try
            {
                famDoc = uiapp.Application.OpenDocumentFile(path);
                if (famDoc == null || !famDoc.IsFamilyDocument)
                    return null;

                var fromFamily = ReadParameter(famDoc.OwnerFamily?.get_Parameter(BuiltInParameter.OMNICLASS_NUMBER));
                if (!string.IsNullOrWhiteSpace(fromFamily))
                    return fromFamily;

                var symbol = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();

                if (symbol != null)
                {
                    var fromSymbol = ReadParameter(symbol.get_Parameter(BuiltInParameter.OMNICLASS_NUMBER));
                    if (!string.IsNullOrWhiteSpace(fromSymbol))
                        return fromSymbol;
                }

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (famDoc != null)
                {
                    try { famDoc.Close(false); } catch { }
                }
            }
        }

        private static string ReadParameter(Parameter parameter)
        {
            if (parameter == null) return null;

            try
            {
                return parameter.StorageType switch
                {
                    StorageType.String => TrimOrNull(parameter.AsString()),
                    StorageType.Integer => TrimOrNull(parameter.AsValueString()),
                    StorageType.Double => TrimOrNull(parameter.AsValueString()),
                    StorageType.ElementId => TrimOrNull(parameter.AsValueString()),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static string TrimOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim();
        }

        private static bool WouldUpgrade(Application app, string rfaPath)
        {
            try
            {
                var info = BasicFileInfo.Extract(rfaPath);
                if (info == null) return true;

                int current = ParseYear(app?.VersionNumber);

                if (TryGetSavedMajorVersion(info, out int saved))
                    return saved != current;

                if (TryGetYearFromProp(info, "RevitBuild", out saved) ||
                    TryGetYearFromProp(info, "RevitProduct", out saved) ||
                    TryGetYearFromProp(info, "Format", out saved))
                    return saved != current;

                return true;
            }
            catch
            {
                return true;
            }
        }

        private static bool TryGetSavedMajorVersion(object info, out int year)
        {
            return
                TryGetYearFromProp(info, "SavedInVersion", out year) ||
                TryGetYearFromProp(info, "SavedInVersionNumber", out year) ||
                TryGetYearFromProp(info, "SavedInVersionMajor", out year) ||
                TryGetYearFromProp(info, "SavedIn", out year) ||
                TryGetYearFromProp(info, "FileVersion", out year);
        }

        private static bool TryGetYearFromProp(object obj, string propName, out int year)
        {
            year = 0;
            var property = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) return false;

            var val = property.GetValue(obj);
            if (val == null) return false;

            if (val is int i)
            {
                year = NormalizeToYear(i);
                return year >= 2008;
            }

            string s = val.ToString();
            var m = Regex.Match(s, @"\b(20\d{2})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out year))
                return true;

            if (int.TryParse(s, out i))
            {
                year = NormalizeToYear(i);
                return year >= 2008;
            }

            return false;
        }

        private static int NormalizeToYear(int value)
        {
            if (value < 100 && value >= 8) return 2000 + value;
            return value;
        }

        private static int ParseYear(string value)
        {
            if (int.TryParse(value, out int v)) return NormalizeToYear(v);
            var m = Regex.Match(value ?? string.Empty, @"\b(20\d{2})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int y)) return y;
            return 0;
        }
    }
}
