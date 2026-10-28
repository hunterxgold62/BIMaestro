using System;
using System.Linq;

namespace Analyse
{
    // Evidence read from the verified opening model, independent of capture
    // detail, translated category names, and whether reconstruction succeeded.
    internal sealed class HistoryOpeningClassification
    {
        public int SourceElementId { get; set; }
        public string SourceUniqueId { get; set; }
        public string Class { get; set; }
        public string Category { get; set; }
        public long CategoryId { get; set; }
        public bool ViewSpecific { get; set; }
        public bool HasBoundingBox { get; set; }
        // Null retains legacy behavior. False is positive evidence that Revit
        // resolves this ID but does not expose it as a document model element.
        public bool? IsDocumentElement { get; set; }
        public bool IsValid() => SourceElementId > 0 && !string.IsNullOrWhiteSpace(SourceUniqueId)
            && Class?.StartsWith("Autodesk.Revit.DB.", StringComparison.Ordinal) == true;
        public bool IsExcluded()
        {
            if (!IsValid()) return false;
            return IsDocumentElement == false
                || Class == "Autodesk.Revit.DB.Plumbing.PipeInsulation" || Class == "Autodesk.Revit.DB.Mechanical.DuctInsulation"
                || ViewSpecific || Class == "Autodesk.Revit.DB.IndependentTag" || Class == "Autodesk.Revit.DB.Dimension"
                || Class == "Autodesk.Revit.DB.LinearDimension" || Class == "Autodesk.Revit.DB.SpotDimension"
                || Class == "Autodesk.Revit.DB.RadialDimension" || Class == "Autodesk.Revit.DB.AngularDimension" || CategoryId == -2000261
                || Class == "Autodesk.Revit.DB.ExtensibleStorage.DataStorage"
                || Class == "Autodesk.Revit.DB.Sketch" || Class == "Autodesk.Revit.DB.SketchPlane"
                || Class == "Autodesk.Revit.DB.FamilySymbol" || Class == "Autodesk.Revit.DB.Family" || Class == "Autodesk.Revit.DB.ReferencePlane"
                || CategoryId == -2000045
                || CategoryId == 0 && (Class == "Autodesk.Revit.DB.Extrusion" || Class == "Autodesk.Revit.DB.GeomCombination")
                || Class == "Autodesk.Revit.DB.Plumbing.PipingSystem" || Class == "Autodesk.Revit.DB.Mechanical.MechanicalSystem"
                || Class == "Autodesk.Revit.DB.Element" && (IsAxisCategory(CategoryId) || CategoryId == 0 && !HasBoundingBox);
        }
        internal static bool IsAxisCategory(long category) => new long[] {
            -2008214, -2008210, -2008196, -2008141, -2008140, -2008139, -2008136,
            -2008072, -2008066, -2008051, -2008045, -2008021, -2008001, -2000288 }.Contains(category);
    }
}
