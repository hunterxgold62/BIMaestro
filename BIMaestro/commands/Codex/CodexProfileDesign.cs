using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal static class CodexProfileDesign
    {
        internal static void ValidateConstraintBudget(IEnumerable<ParametricPart> parts)
        {
            var driven = parts.Where(p => p.Profile != null && p.Profile.Any(v => v.Any(e => e.Terms.Count != 0))).ToArray();
            var large = driven.FirstOrDefault(p => p.Profile.Length > 24);
            if (large != null || driven.Sum(p => p.Profile.Length) > 96)
                throw new InvalidOperationException("Profil paramétrique trop complexe pour une création interactive stable : maximum 24 sommets par profil piloté et 96 au total. " +
                    "Conserver les détails décoratifs en profils fixes (terms=[]), simplifier les contours ou utiliser une famille géométrique fixe. Ne pas relancer le même descriptif." +
                    (large == null ? "" : " Pièce : " + large.Name));
        }
        internal static double Area(double[][] p) => Math.Abs(p.Select((v, i) => v[0] * p[(i + 1) % p.Length][1] - v[1] * p[(i + 1) % p.Length][0]).Sum()) / 2;
        private static double Cross(double[] a, double[] b, double[] p) => (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]);
        private static bool On(double[] a, double[] b, double[] p) => Math.Abs(Cross(a, b, p)) < 1e-6 && p[0] >= Math.Min(a[0], b[0]) - 1e-6 && p[0] <= Math.Max(a[0], b[0]) + 1e-6 && p[1] >= Math.Min(a[1], b[1]) - 1e-6 && p[1] <= Math.Max(a[1], b[1]) + 1e-6;
        private static bool Intersects(double[] a, double[] b, double[] c, double[] d) => On(a, b, c) || On(a, b, d) || On(c, d, a) || On(c, d, b) || Math.Sign(Cross(a, b, c)) != Math.Sign(Cross(a, b, d)) && Math.Sign(Cross(c, d, a)) != Math.Sign(Cross(c, d, b));
        internal static void Validate(ParametricPart part, Dictionary<string, double> values)
        {
            if (part.Profile == null) return;
            var points = part.Profile.Select(p => p.Select(e => e.Value(values)).ToArray()).ToArray(); var uv = part.ProfileAxes;
            if (part.Openings.Count > 0) throw new InvalidOperationException("Un profil polygonal ne reçoit pas encore d'ouvertures internes : " + part.Name);
            for (int axis = 0; axis < 2; axis++)
                if (Math.Abs(points.Min(p => p[axis]) - part.Min[uv[axis]].Value(values)) > 0.01 || Math.Abs(points.Max(p => p[axis]) - part.Max[uv[axis]].Value(values)) > 0.01) throw new InvalidOperationException("Les limites minimum/maximum doivent correspondre au profil : " + part.Name);
            if (Area(points) < 1) throw new InvalidOperationException("Profil de surface nulle ou trop petite : " + part.Name);
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Length];
                if (Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2)) < 1) throw new InvalidOperationException("Arête de profil inférieure à 1 mm : " + part.Name);
                if (Math.Abs(Cross(points[(i + points.Length - 1) % points.Length], a, b)) < 1e-6) throw new InvalidOperationException("Sommets redondants ou retournés dans le profil : " + part.Name);
                for (int j = i + 2; j < points.Length; j++)
                    if (!(i == 0 && j == points.Length - 1) && Intersects(a, b, points[j], points[(j + 1) % points.Length])) throw new InvalidOperationException("Profil auto-intersecté : " + part.Name);
            }
        }
    }
}
