using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        public sealed class ExcavationRelation
        {
            public string Kind { get; set; }
            public long IntersectedId { get; set; }
            public string IntersectedUid { get; set; }
            public long IntersectingId { get; set; }
            public string IntersectingUid { get; set; }
            public double VolumeCubicFeet { get; set; }
        }
        readonly Dictionary<string, List<ExcavationRelation>> excavationByUid = new(StringComparer.Ordinal);
        readonly List<string> excavationProbeErrors = new();

        void RefreshExcavations(Document document)
        {
            excavationByUid.Clear(); excavationProbeErrors.Clear();
            foreach (var terrain in new FilteredElementCollector(document).OfClass(typeof(Toposolid)).Cast<Toposolid>())
            {
                try
                {
                    foreach (var data in terrain.GetIntersectingElementData())
                    {
                        var pair = new ExcavationRelation
                        {
                            Kind = data.IntersectionType.ToString(), IntersectedId = data.IntersectedElementId.Value,
                            IntersectedUid = document.GetElement(data.IntersectedElementId)?.UniqueId,
                            IntersectingId = data.IntersectingElementId.Value, IntersectingUid = document.GetElement(data.IntersectingElementId)?.UniqueId,
                            VolumeCubicFeet = data.IntersectionVolume
                        };
                        foreach (var uid in new[] { pair.IntersectedUid, pair.IntersectingUid }.Where(uid => uid != null).Distinct())
                        {
                            if (!excavationByUid.TryGetValue(uid, out var pairs)) excavationByUid[uid] = pairs = new List<ExcavationRelation>();
                            pairs.Add(pair);
                        }
                    }
                }
                catch (Exception ex) { excavationProbeErrors.Add(terrain.Id.Value + ":" + ex.GetType().Name + ":" + ex.Message); }
            }
        }
    }
}
