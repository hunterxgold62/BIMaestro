// Compilation-only adapter: no native geometry is executed by this project.
// The production MotorEnvelope and BoosterIds live in the main MEP command.
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal static class BoosterIds
    {
        internal static long Value(ElementId id) => id.Value;
    }
    internal sealed class MotorEnvelope
    {
        internal Transform Frame;
        internal XYZ Center, Size, TranslationDirection;
        internal int Axis, Sign;
        internal double Margin, Travel;
        internal XYZ Direction => TranslationDirection ?? XYZ.BasisX;
        internal IEnumerable<XYZ> Corners(double distance, bool clearance) => throw new NotSupportedException();
        internal static Outline Bounds(IEnumerable<XYZ> points) => throw new NotSupportedException();
        internal List<Solid> SweepParts(double distance) => throw new NotSupportedException();
        internal Solid Sweep(double distance) => throw new NotSupportedException();
    }
}
