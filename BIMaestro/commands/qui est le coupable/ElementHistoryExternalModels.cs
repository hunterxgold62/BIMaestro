using Autodesk.Revit.DB;
using System;

namespace Analyse
{
    // External model containers are outside object recovery. A whole-RVT opening
    // file remains an allowed safety checkpoint; this predicate applies to its
    // elements, never to the archive file or source document itself.
    internal static class ElementHistoryExternalModels
    {
        internal static bool IsExcluded(Element element) => element is RevitLinkInstance
            || element is RevitLinkType || element is ImportInstance || element is CADLinkType;

        // Exact native class identity captured while alive, independent of UI
        // language, filenames, category labels or an ordinary native IfcGUID.
        internal static bool IsExcludedClass(string fullName) =>
            string.Equals(fullName, "Autodesk.Revit.DB.RevitLinkInstance", StringComparison.Ordinal)
            || string.Equals(fullName, "Autodesk.Revit.DB.RevitLinkType", StringComparison.Ordinal)
            || string.Equals(fullName, "Autodesk.Revit.DB.ImportInstance", StringComparison.Ordinal)
            || string.Equals(fullName, "Autodesk.Revit.DB.CADLinkType", StringComparison.Ordinal);
    }
}
