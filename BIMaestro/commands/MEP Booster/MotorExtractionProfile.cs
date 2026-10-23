using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal sealed class MotorExtractionProfile
    {
        internal XYZ Size, Center;
        internal string Description;
        internal static MotorExtractionProfile Read(FamilyInstance pump)
        {
            var frame = pump.GetTransform();
            var points = Points(pump, frame.Inverse);
            if (points.Count == 0) throw new InvalidOperationException("La pompe ne contient pas de solides exploitables.");
            var min = Min(points); var max = Max(points); var size = max - min;
            int lengthAxis = size.X >= size.Y ? 0 : 1;
            var fallback = new MotorExtractionProfile { Center = (min + max) / 2,
                Size = new XYZ(size.X * (lengthAxis == 0 ? .6 : 1), size.Y * (lengthAxis == 1 ? .6 : 1), size.Z),
                Description = "Gabarit approximatif : 60 % de la longueur horizontale, largeur et hauteur conservées. Positionnez-le sur le moteur. Ce n’est pas une identification automatique." };
            // Explicit family adapter. Other families retain a conservative,
            // editable envelope rather than being classified by their names.
            if (pump.Symbol.Family.Name != "CML_Pompe axiale monocellulaire générique"
                && pump.Symbol.Family.Name != "CML_Pompe en ligne monocellulaire générique") return fallback;
            Document familyDoc = null;
            try
            {
                familyDoc = pump.Document.EditFamily(pump.Symbol.Family);
                var manager = familyDoc.FamilyManager;
                var type = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == pump.Symbol.Name);
                if (type == null) { fallback.Description += " Type CML absent du document de famille."; return fallback; }
                using (var transaction = new Transaction(familyDoc, "Lecture temporaire du moteur"))
                {
                    transaction.Start(); manager.CurrentType = type; familyDoc.Regenerate();
                    var lengthParameter = manager.Parameters.Cast<FamilyParameter>().FirstOrDefault(p => p.Definition.Name == "Moteur L");
                    double expectedLength = pump.LookupParameter("Moteur L")?.AsDouble() ?? pump.Symbol.LookupParameter("Moteur L")?.AsDouble() ?? 0;
                    if (lengthParameter == null || expectedLength <= 0 || Math.Abs((type.AsDouble(lengthParameter) ?? 0) - expectedLength) > 1 / 304.8)
                    { fallback.Description += " Dimensions CML non concordantes : gabarit générique utilisé."; transaction.RollBack(); return fallback; }
                    var motor = new FilteredElementCollector(familyDoc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                        .FirstOrDefault(i => i.Symbol.Family.Name == "CML_Moteur BT");
                    if (motor == null) { transaction.RollBack(); return fallback; }
                    var motorPoints = Points(motor, Transform.Identity);
                    if (motorPoints.Count == 0) { transaction.RollBack(); return fallback; }
                    var motorMin = Min(motorPoints); var motorMax = Max(motorPoints); var motorSize = motorMax - motorMin;
                    // Keep the pump's full crosswise width, even when the modelled
                    // motor itself is narrower. Never reduce all three axes.
                    motorSize = new XYZ(lengthAxis == 1 ? Math.Max(motorSize.X, size.X) : Math.Max(motorSize.X,expectedLength),
                        lengthAxis == 0 ? Math.Max(motorSize.Y, size.Y) : Math.Max(motorSize.Y,expectedLength), motorSize.Z);
                    var result = new MotorExtractionProfile { Size = motorSize, Center = (motorMin + motorMax) / 2,
                        Description = "Moteur seul : géométrie CML_Moteur BT du type sélectionné, avec largeur au moins égale à celle de la pompe. Gabarit modifiable. Le trajet suppose le moteur déjà désaccouplé." };
                    transaction.RollBack(); return result;
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException || ex is ArgumentException)
            { fallback.Description += " Lecture du moteur indisponible : gabarit générique utilisé."; return fallback; }
            finally { if (familyDoc != null) familyDoc.Close(false); }
        }
        internal static List<XYZ> Points(Element element, Transform coordinates)
        {
            var points = new List<XYZ>();
            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
            using (var geometry = element.get_Geometry(options))
            {
                if (geometry != null) Read(geometry);
                void Read(GeometryElement items)
                {
                    foreach (var item in items)
                        if (item is GeometryInstance nested) { using (var expanded = nested.GetInstanceGeometry()) Read(expanded); }
                        else if (item is Solid solid && solid.Volume > 1e-12)
                            foreach (Edge edge in solid.Edges) foreach (var p in edge.Tessellate()) points.Add(coordinates.OfPoint(p));
                }
            }
            return points;
        }
        private static XYZ Min(IList<XYZ> p) => new XYZ(p.Min(v => v.X), p.Min(v => v.Y), p.Min(v => v.Z));
        private static XYZ Max(IList<XYZ> p) => new XYZ(p.Max(v => v.X), p.Max(v => v.Y), p.Max(v => v.Z));
    }
}
