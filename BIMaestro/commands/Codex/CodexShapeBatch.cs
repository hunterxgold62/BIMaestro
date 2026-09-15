using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    // Validate the complete batch before opening a Revit transaction.
    internal sealed class CodexShapeBatch
    {
        internal bool IsCylinder;
        internal double X, Y, Z, Width, Length, Height, Radius;
        internal string Description => (IsCylinder ? $"Cylindre R={Radius:g}, H={Height:g}" : $"Bloc {Width:g} × {Length:g} × {Height:g}") + $" mm ; X={X:g}, Y={Y:g}, Z={Z:g} mm";

        internal static List<CodexShapeBatch> Parse(JObject args)
        {
            if (args == null || args.Properties().Any(p => p.Name != "boxes" && p.Name != "cylinders") ||
                !(args["boxes"] is JArray boxes) || !(args["cylinders"] is JArray cylinders) ||
                boxes.Count + cylinders.Count < 1 || boxes.Count + cylinders.Count > 50)
                throw new InvalidOperationException("Un lot doit contenir de 1 à 50 formes, réparties entre boxes et cylinders.");
            var result = new List<CodexShapeBatch>();
            foreach (bool cylinder in new[] { false, true })
                foreach (var token in cylinder ? cylinders : boxes)
                {
                    string[] keys = cylinder ? new[] { "x_mm", "y_mm", "z_mm", "height_mm", "radius_mm" } : new[] { "x_mm", "y_mm", "z_mm", "height_mm", "width_mm", "length_mm" };
                    if (!(token is JObject item) || item.Properties().Any(p => !keys.Contains(p.Name)) || keys.Any(k => item[k] == null))
                        throw new InvalidOperationException("Description de forme invalide.");
                    result.Add(new CodexShapeBatch
                    {
                        IsCylinder = cylinder, X = Number(item, "x_mm", -100000), Y = Number(item, "y_mm", -100000), Z = Number(item, "z_mm", -100000),
                        Height = Number(item, "height_mm", 1),
                        Radius = cylinder ? Number(item, "radius_mm", 1, 50000) : 0,
                        Width = cylinder ? 0 : Number(item, "width_mm", 1), Length = cylinder ? 0 : Number(item, "length_mm", 1)
                    });
                }
            return result;
        }

        private static double Number(JObject item, string name, double min, double max = 100000)
        {
            var token = item[name];
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new InvalidOperationException("Nombre attendu : " + name);
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max) throw new InvalidOperationException("Valeur hors limites : " + name);
            return value;
        }
    }
}
