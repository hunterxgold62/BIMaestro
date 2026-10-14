namespace Analyse
{
    // The isolated history harness has no UI selection pipeline.
    internal static class ElementHistoryTracker
    {
        internal static string GetDocumentKeyForHistory(Autodesk.Revit.DB.Document doc)
            => string.IsNullOrEmpty(doc.PathName) ? doc.GetHashCode().ToString() : doc.PathName;
        internal static bool GetLatestSelectionForNativePriority(Autodesk.Revit.DB.Document doc,
            out System.DateTime atUtc, out System.Collections.Generic.HashSet<int> selectedIds)
        {
            atUtc = default(System.DateTime);
            selectedIds = null;
            return false;
        }
    }

    internal static class CollaborativeModelTrackerStore
    {
        internal static string ActiveDirectory => System.IO.Path.GetDirectoryName(typeof(CollaborativeModelTrackerStore).Assembly.Location);
    }
}
