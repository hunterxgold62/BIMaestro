using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal enum MaintenanceFeatureConfidence { None, Low, Medium, High }
    internal enum MaintenanceFeatureDimensionKind { WheelRadius, WheelDiameter, ChainDrop, ChainSpacing, CapDiameter, CartridgeDiameter, CartridgeLength, ExtractionTravel, LeverLength, ChainRadius, LeverRotation }

    internal sealed class MaintenanceFeatureSuggestion
    {
        internal MaintenanceAccessOperation Operation;
        internal XYZ CommandPoint, Axis; // WORLD; Axis is a proposed signed direction.
        internal Transform Frame; // Origin = CommandPoint; local Z = Axis.
        internal double Radius, HandleThickness, ChainDrop, ChainSpacing, RemovalTravel;
        internal double InitialAngleDegrees, RotationDegrees;
        internal bool RotationAssumed;
        internal bool HasRotation => RotationDegrees != 0;
        internal XYZ RemovalSize, RemovalCenterOffset; // LOCAL to the suggested Frame.
        internal MaintenanceFeatureConfidence Confidence;
        internal bool Ambiguous, DirectionAmbiguous;
        internal bool Manufacturer; // Always false: model parameters are not a product maintenance notice.
        internal readonly List<string> Evidence = new List<string>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<MaintenanceFeatureCandidate> Candidates = new List<MaintenanceFeatureCandidate>();
        internal bool HasCommandPoint => CommandPoint != null;
        internal bool HasAxis => Axis != null;
        internal bool HasRadius => Radius > 0;
        internal bool HasHandleThickness => HandleThickness > 0;
        internal bool HasChain => ChainDrop > 0 && ChainSpacing > 0;
        internal bool HasRemoval => RemovalSize != null;
        internal bool HasRemovalTravel => RemovalTravel > 0;
    }
    internal sealed class MaintenanceFeatureCandidate
    {
        internal XYZ CommandPoint, Axis;
        internal Transform Frame; // Optional: lever candidate X is its observed initial direction.
        internal double Radius, Score, CenterlineRadius, HandleThickness;
        internal string Label;
        internal readonly List<string> Evidence = new List<string>();
    }
    // Plain observations are detached from Revit geometry/documents, so cached
    // family probes cannot retain native solids or modify the project document.
    internal sealed class MaintenanceFeatureObservation
    {
        internal XYZ Center, Axis;
        internal double Radius, AxialLength, Coverage, CenterlineRadius, RimThickness;
        internal bool Cylinder, Linear, ComponentExplicit;
        internal bool ProjectVisible; // Read from the selected project's visible instance geometry.
        internal string Component, Origin;
        internal XYZ[] ComponentPoints;
        internal XYZ[] CurvePoints;
        internal Transform ComponentFrame;
    }
    internal sealed class MaintenanceFeatureDimension
    {
        internal MaintenanceFeatureDimensionKind Kind;
        internal string Name, Component, Origin;
        internal double Value;
    }
    internal sealed class MaintenanceFeatureConnector
    {
        internal XYZ Center, Axis;
        internal double Radius;
    }

    internal static class MaintenanceFeatureRecognition
    {
        private sealed class ChainSection
        {
            internal double InnerRadius, OuterRadius, CenterlineRadius;
            internal bool Symmetric;
            internal string Evidence;
        }
        private static double Mm(double value) => value / 304.8;
        internal static string Normalize(string value)
        {
            if (value == null) return "";
            var text = new StringBuilder();
            foreach (char c in value.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) text.Append(char.ToLowerInvariant(c));
            return text.ToString();
        }
        internal static MaintenanceFeatureDimensionKind? DimensionKind(string name)
        {
            string n = Normalize(name);
            bool wheel = Contains(n, "volant", "handwheel", "chainwheel", "rouedechaine");
            bool chain = Contains(n, "chaine", "chain");
            bool cartridge = Contains(n, "cartouche", "cartridge", "tamis", "panier", "basket", "strainerinsert");
            bool cap = Contains(n, "capot", "couvercle", "bouchon", "cover", "bonnet");
            if (Contains(n,"poignee","handle","levier","lever") && Contains(n,"course","debattement","travel","sweep","stroke")
                && Contains(n,"ang","rotat","degree")) return MaintenanceFeatureDimensionKind.LeverRotation;
            if (Contains(n, "poignee", "handle", "levier", "lever") && Contains(n, "long", "length")) return MaintenanceFeatureDimensionKind.LeverLength;
            if (wheel && Contains(n, "fixation", "rayonarc", "section", "epaisseur", "hub", "tube")) return null;
            if (wheel && Contains(n, "diam", "ø")) return MaintenanceFeatureDimensionKind.WheelDiameter;
            if (wheel && Contains(n, "rayon", "radius")) return MaintenanceFeatureDimensionKind.WheelRadius;
            if (chain && Contains(n, "rayon", "radius")) return MaintenanceFeatureDimensionKind.ChainRadius;
            if (chain && Contains(n, "ecart", "spacing", "entraxe", "brins", "width")) return MaintenanceFeatureDimensionKind.ChainSpacing;
            if (chain && Contains(n, "descente", "drop", "hauteur", "height", "pendante", "hanging")) return MaintenanceFeatureDimensionKind.ChainDrop;
            if (Contains(n, "extraction", "extract", "maintenance", "demontage", "withdrawal") && Contains(n, "course", "travel", "distance", "clearance", "degagement"))
                return MaintenanceFeatureDimensionKind.ExtractionTravel;
            if (cartridge && Contains(n, "long", "length", "hauteur", "height")) return MaintenanceFeatureDimensionKind.CartridgeLength;
            if (cartridge && Contains(n, "diam")) return MaintenanceFeatureDimensionKind.CartridgeDiameter;
            if (cap && Contains(n, "diam")) return MaintenanceFeatureDimensionKind.CapDiameter;
            return null;
        }
        private static bool Contains(string source, params string[] fragments) => fragments.Any(source.Contains);

        internal static MaintenanceFeatureSuggestion Infer(MaintenanceAccessOperation preferred, IEnumerable<MaintenanceFeatureObservation> input,
            IEnumerable<MaintenanceFeatureDimension> parameterInput, IEnumerable<MaintenanceFeatureConnector> connectorInput)
        {
            var geometry = input.ToArray(); var dimensions = parameterInput.ToArray(); var connectors = connectorInput.ToArray();
            if(preferred==MaintenanceAccessOperation.Actuator)return Recognize(preferred,geometry,dimensions,connectors);
            bool manual=preferred==MaintenanceAccessOperation.HandwheelValve||preferred==MaintenanceAccessOperation.LeverValve||preferred==MaintenanceAccessOperation.ChainwheelValve;
            var actuator=manual?Actuator(geometry,dimensions):null;
            if(actuator!=null)
            {
                // Flattened project geometry duplicates the motor's circular
                // faces. Only a separate identified component can establish a
                // manual control here; a mounting face is never its pivot.
                geometry=geometry.Where(o=>o.ComponentExplicit&&!Contains(Normalize(o.Component),"actionneur","actuator","servomoteur")).ToArray();
            }
            MaintenanceFeatureSuggestion Manual(MaintenanceFeatureSuggestion suggestion)
            {
                if(actuator==null)return suggestion;
                suggestion.Evidence.AddRange(actuator.Evidence.Take(1));
                if(!suggestion.HasCommandPoint)
                    suggestion.Warnings.Insert(0,"Commande motorisée reconnue, mais aucun organe de manœuvre manuelle n’est identifié de façon fiable. Précisez un volant, levier ou dispositif à chaîne réellement présent ; la commande électrique n’est pas simulée. Pour retirer le moteur, choisissez « Déposer un actionneur ». ");
                else suggestion.Evidence.Add("Organe manuel distinct de l’actionneur : son repère est utilisé pour la manœuvre, sans volume ni course de dépose.");
                return suggestion;
            }
            if(preferred==MaintenanceAccessOperation.LeverValve)return Manual(Lever(geometry,dimensions,connectors));
            if (preferred == MaintenanceAccessOperation.HandwheelValve)
            {
                var chain = Recognize(MaintenanceAccessOperation.ChainwheelValve, geometry, dimensions, connectors);
                bool explicitChain = dimensions.Any(p => (p.Kind == MaintenanceFeatureDimensionKind.WheelRadius || p.Kind == MaintenanceFeatureDimensionKind.WheelDiameter)
                    && Contains(Normalize(p.Name), "chaine", "chain"));
                if (chain.HasCommandPoint && (chain.HasChain || explicitChain)) return Manual(chain);
                if(chain.Ambiguous&&explicitChain)return Manual(chain);
                var wheel=Recognize(MaintenanceAccessOperation.HandwheelValve,geometry,dimensions,connectors);
                if(wheel.HasCommandPoint||wheel.Ambiguous)return Manual(wheel);
                var lever=Lever(geometry,dimensions,connectors);
                if(lever.HasCommandPoint||lever.Ambiguous||lever.HasRadius)return Manual(lever);
                return Manual(wheel);
            }
            return Manual(Recognize(preferred, geometry, dimensions, connectors));
        }

        private sealed class LeverPart
        {
            internal XYZ[] Points;
            internal string Label;
        }
        private static MaintenanceFeatureSuggestion Lever(MaintenanceFeatureObservation[] input,MaintenanceFeatureDimension[] dimensions,MaintenanceFeatureConnector[] ports)
        {
            var result=new MaintenanceFeatureSuggestion {Operation=MaintenanceAccessOperation.LeverValve};
            // A family editor can expose the hidden open AND closed handle.
            // Project-visible solids are authoritative whenever available.
            bool visible=input.Any(o=>o.ProjectVisible&&o.ComponentPoints?.Length>=8);
            var observations=(visible?input.Where(o=>o.ProjectVisible):input).ToArray();
            var parts=observations.Where(o=>o.ComponentPoints?.Length>=8).GroupBy(o=>o.ComponentPoints)
                .Select(g=>new LeverPart {Points=g.Key,Label=g.First().Component}).ToArray();
            double declaredLength=Dimension(dimensions,MaintenanceFeatureDimensionKind.LeverLength);
            var connectors=ports.Where(c=>c.Center!=null&&c.Axis!=null&&c.Radius>0).ToArray();
            if(connectors.Length<2)
            {
                result.Warnings.Add("Axe de levier non établi : deux raccordements et une tige distincte sont nécessaires pour distinguer poignée, bride et tuyauterie. Pointez le repère manuellement.");
                return result;
            }
            var pairs=(from a in connectors from b in connectors where !ReferenceEquals(a,b)
                let distance=(a.Center-b.Center).GetLength() orderby distance descending select new {A=a,B=b,Distance=distance}).ToArray();
            var pair=pairs.FirstOrDefault(p=>p.Distance>Mm(5)&&Math.Abs(p.A.Axis.Normalize().DotProduct((p.B.Center-p.A.Center).Normalize()))>.9
                &&Math.Abs(p.B.Axis.Normalize().DotProduct((p.B.Center-p.A.Center).Normalize()))>.9);
            if(pair==null)
            {result.Warnings.Add("La ligne hydraulique est ambiguë : aucun axe de levier n’est déduit de ces raccordements.");return result;}
            XYZ flow=(pair.B.Center-pair.A.Center).Normalize();double scale=(pair.A.Radius+pair.B.Radius)/2;
            var stems=new List<MaintenanceFeatureObservation>();
            foreach(var o in observations.Where(o=>o.Cylinder&&o.Center!=null&&o.Axis!=null&&o.Axis.GetLength()>.99
                &&o.Radius>=Math.Max(Mm(.5),scale*.025)&&o.Radius<scale*2&&o.AxialLength>=Math.Max(Mm(.5),o.Radius*.05)))
            {
                XYZ axis=o.Axis.Normalize();if(Math.Abs(axis.DotProduct(flow))>.45)continue;
                if(stems.Any(s=>Math.Abs(s.Axis.Normalize().DotProduct(axis))>.99&&Radial(o.Center-s.Center,axis)<Math.Max(Mm(.5),o.Radius*.03)
                    &&Math.Abs((o.Center-s.Center).DotProduct(axis))<Mm(.5)&&Math.Abs(o.Radius-s.Radius)<Mm(.5)))continue;
                stems.Add(o);
            }
            foreach(var stem in stems)
            {
                XYZ axis=stem.Axis.Normalize();
                double along=(stem.Center-pair.A.Center).DotProduct(flow);
                if(along<-scale||along>pair.Distance+scale)continue;
                XYZ fromFlow=stem.Center-(pair.A.Center+flow*along);
                if(fromFlow.GetLength()<Math.Max(Mm(1),scale*.15))continue;
                if(fromFlow.DotProduct(axis)<0)axis=-axis;
                if(Math.Abs(fromFlow.Normalize().DotProduct(axis))<.9)continue;
                // A circular axis leading to another pipe opening is a port,
                // not evidence of an operating stem.
                if(connectors.Any(c=>Math.Abs(c.Axis.Normalize().DotProduct(axis))>.9&&Radial(c.Center-stem.Center,axis)<Math.Max(Mm(2),c.Radius*.5)))continue;
                double cover=observations.Where(o=>o.Cylinder&&o.Center!=null&&o.Axis!=null&&Math.Abs(o.Axis.Normalize().DotProduct(axis))>.99
                    &&(o.Center-stem.Center).GetLength()<Mm(1)&&Math.Abs(o.Radius-stem.Radius)<Mm(.5)).Sum(o=>o.Coverage);
                if(cover<Math.PI*.8)continue;
                foreach(var part in parts)
                {
                    XYZ[] points=part.Points;
                    // Determine the dominant direction in the stem-normal plane.
                    // The sign comes from the one-sided arm, never a family name.
                    XYZ u=(flow-axis*flow.DotProduct(axis)).Normalize(),v=axis.CrossProduct(u).Normalize();
                    XYZ center=points.Aggregate(XYZ.Zero,(a,b)=>a+b)/points.Length;
                    double covXX=0,covXY=0,covYY=0;
                    foreach(var point in points){XYZ d=point-center;double x=d.DotProduct(u),y=d.DotProduct(v);covXX+=x*x;covXY+=x*y;covYY+=y*y;}
                    double angle=.5*Math.Atan2(2*covXY,covXX-covYY);
                    XYZ xaxis=u*Math.Cos(angle)+v*Math.Sin(angle);
                    double lo=points.Min(p=>(p-stem.Center).DotProduct(xaxis)),hi=points.Max(p=>(p-stem.Center).DotProduct(xaxis));
                    if(-lo>hi){xaxis=-xaxis;double previous=lo;lo=-hi;hi=-previous;}
                    XYZ yaxis=axis.CrossProduct(xaxis).Normalize();
                    double ymin=points.Min(p=>(p-stem.Center).DotProduct(yaxis)),ymax=points.Max(p=>(p-stem.Center).DotProduct(yaxis));
                    double zmin=points.Min(p=>(p-stem.Center).DotProduct(axis)),zmax=points.Max(p=>(p-stem.Center).DotProduct(axis));
                    double width=ymax-ymin,excursion=zmax-zmin;
                    if(hi<Math.Max(Mm(12),scale*1.5)||hi>Math.Min(Mm(3000),Math.Max(Mm(150),scale*25))
                        ||width<Mm(1)||hi<width*2.5||-lo>hi*.4||lo>Math.Max(Mm(2),stem.Radius*.3)
                        ||Math.Abs((ymax+ymin)/2)>Math.Max(Mm(1),width*.12)||excursion>hi*.5)continue;
                    // The arm must actually meet the end of the modeled stem.
                    if(Math.Abs(zmin-stem.AxialLength/2)>Math.Max(Mm(2),stem.Radius*.4))continue;
                    var grip=points.Where(p=>(p-stem.Center).DotProduct(xaxis)>hi*.6).ToArray();
                    if(grip.Length<2)continue;
                    double gripZ=(grip.Min(p=>(p-stem.Center).DotProduct(axis))+grip.Max(p=>(p-stem.Center).DotProduct(axis)))/2;
                    XYZ pivot=stem.Center+axis*gripZ;
                    // A square envelope contains measured width AND the entire
                    // bent arm's axial excursion around its grip-height plane.
                    double thickness=2*Math.Max(Math.Max(Math.Abs(ymin),Math.Abs(ymax)),Math.Max(Math.Abs(zmin-gripZ),Math.Abs(zmax-gripZ)));
                    if(-lo>thickness/2+Mm(1))continue;
                    var frame=Transform.Identity;frame.Origin=pivot;frame.BasisX=xaxis;frame.BasisY=yaxis;frame.BasisZ=axis;
                    var candidate=new MaintenanceFeatureCandidate {CommandPoint=pivot,Axis=axis,Frame=frame,Radius=hi,HandleThickness=thickness,
                        Label=part.Label,Score=14};
                    candidate.Evidence.Add("Tige cylindrique distincte de la ligne des raccordements et poignée solide allongée, reliée à son extrémité.");
                    candidate.Evidence.Add("Poignée mesurée : portée "+(hi*304.8).ToString("0.#")+" mm ; largeur "+(width*304.8).ToString("0.#")
                        +" mm ; dénivelé "+(excursion*304.8).ToString("0.#")+" mm ; section carrée englobante "+(thickness*304.8).ToString("0.#")+" mm.");
                    candidate.Evidence.Add("Pivot proposé sur l’axe de tige à hauteur de prise ; X suit la poignée initiale visible. L’épaisseur englobante n’est pas l’épaisseur de matière.");
                    if(declaredLength>0&&Math.Abs(declaredLength-hi)<Math.Max(Mm(2),hi*.05))
                    {candidate.Score+=3;candidate.Evidence.Add("Longueur de poignée paramétrée concordante avec la géométrie ; le paramètre ne suffit pas à identifier son pivot.");}
                    if(result.Candidates.Any(existing=>(existing.CommandPoint-pivot).GetLength()<Mm(2)&&existing.Frame.BasisX.DotProduct(xaxis)>.99
                        &&Math.Abs(existing.Radius-hi)<Mm(2)))continue;
                    result.Candidates.Add(candidate);
                }
            }
            result.Candidates.Sort((a,b)=>b.Score.CompareTo(a.Score));
            if(result.Candidates.Count==0)
            {
                if(declaredLength>0&&parts.Length>0)
                {result.Radius=declaredLength;result.Confidence=MaintenanceFeatureConfidence.Low;result.Evidence.Add("Longueur de poignée disponible ; pivot et orientation non résolus géométriquement.");}
                result.Warnings.Add("Aucun levier simple suffisamment identifiable : pointez le pivot, l’axe et la direction de la poignée. Aucun capot circulaire n’est utilisé comme levier.");
                return result;
            }
            var best=result.Candidates[0];
            result.Ambiguous=result.Candidates.Count>1&&result.Candidates[1].Score>=best.Score-3;
            if(result.Ambiguous)
            {result.Confidence=MaintenanceFeatureConfidence.Low;result.Warnings.Add("Plusieurs poignées ou tiges sont aussi plausibles : choisissez le repère et la position initiale ; aucune poignée n’est fusionnée avec une autre.");return result;}
            result.CommandPoint=best.CommandPoint;result.Axis=best.Axis;result.Frame=best.Frame;result.Radius=best.Radius;result.HandleThickness=best.HandleThickness;
            result.Confidence=MaintenanceFeatureConfidence.Medium;result.InitialAngleDegrees=0;result.RotationDegrees=90;result.RotationAssumed=true;result.DirectionAmbiguous=true;
            result.Evidence.AddRange(best.Evidence);
            if(visible)result.Evidence.Add("Repère établi sur la géométrie visible de l’instance ; positions alternatives de la famille exclues de cette identification.");
            var courses=dimensions.Where(d=>d.Kind==MaintenanceFeatureDimensionKind.LeverRotation&&!double.IsNaN(d.Value)&&!double.IsInfinity(d.Value)&&Math.Abs(d.Value)>=1&&Math.Abs(d.Value)<=720).ToArray();
            if(courses.Length>0&&courses.Max(d=>d.Value)-courses.Min(d=>d.Value)<=1)
            {result.RotationDegrees=courses.Average(d=>d.Value);result.RotationAssumed=false;result.Evidence.Add("Course angulaire de levier renseignée dans les paramètres du modèle ; sens et butées à confirmer, aucune notice constructeur lue.");}
            else result.Warnings.Add("Course de 90° proposée comme hypothèse réglable ; le sens de rotation, les butées et l’ouverture réelle ne sont pas reconnus.");
            result.Warnings.Add("Poignée et axe proposés depuis la géométrie : confirmez leur fonction, la position initiale et la course réelle. Aucun état ouvert/fermé ni conformité constructeur n’est certifié.");
            return result;
        }

        private static MaintenanceFeatureSuggestion Actuator(MaintenanceFeatureObservation[] geometry, MaintenanceFeatureDimension[] parameters)
        {
            var components = geometry.Where(o => o.ComponentExplicit && o.ComponentFrame != null && o.ComponentPoints != null
                && Contains(Normalize(o.Component), "actionneur", "actuator", "servomoteur")).GroupBy(o => o.Component).ToArray();
            var candidates = new List<MaintenanceFeatureSuggestion>();
            foreach (var component in components)
            {
                Transform frame = component.First().ComponentFrame;
                XYZ[] points = component.Select(o => o.ComponentPoints).Distinct().SelectMany(p => p).Select(frame.Inverse.OfPoint).ToArray();
                if (points.Length < 12) continue;
                XYZ lo = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
                XYZ hi = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
                XYZ size = hi - lo;
                // Confirm that the component insertion frame lies on its lower
                // mounting face. A name on unrelated geometry is insufficient.
                if (size.X < Mm(20) || size.Y < Mm(20) || size.Z < Mm(20) || Math.Abs(lo.Z) > Math.Max(Mm(5), size.Z * .08)
                    || lo.X > Mm(5) || hi.X < -Mm(5) || lo.Y > Mm(5) || hi.Y < -Mm(5)) continue;
                var result = new MaintenanceFeatureSuggestion { Operation = MaintenanceAccessOperation.Actuator, CommandPoint = frame.Origin,
                    Axis = frame.BasisZ.Normalize(), Frame = frame, RemovalSize = size, RemovalCenterOffset = (lo + hi) / 2,
                    Confidence = MaintenanceFeatureConfidence.Medium, DirectionAmbiguous = false };
                result.Evidence.Add("Sous-composant d’actionneur isolé avec géométrie solide : " + component.Key + ".");
                result.Evidence.Add("Repère d’insertion concordant avec la face de montage ; gabarit mesuré sur ce seul composant.");
                result.Warnings.Add("Sens de dépose proposé selon le repère de montage. Fixations, accouplement, câbles et démontabilité interne restent à confirmer.");
                ApplyDimension(result, parameters, MaintenanceFeatureDimensionKind.ExtractionTravel, v => result.RemovalTravel = v);
                if (!result.HasRemovalTravel) result.Warnings.Add("Course de dépose absente des paramètres reconnus : aucune distance de retrait n’est inventée.");
                candidates.Add(result);
            }
            if (candidates.Count == 1) return candidates[0];
            return null;
        }

        internal static MaintenanceFeatureSuggestion Recognize(MaintenanceAccessOperation operation,
            IEnumerable<MaintenanceFeatureObservation> input, IEnumerable<MaintenanceFeatureDimension> parameterInput,
            IEnumerable<MaintenanceFeatureConnector> connectorInput)
        {
            if(operation==MaintenanceAccessOperation.Actuator)
            {
                var actuator=Actuator(input.ToArray(),parameterInput.ToArray());
                if(actuator!=null)return actuator;
                var missing=new MaintenanceFeatureSuggestion {Operation=MaintenanceAccessOperation.Actuator};
                missing.Warnings.Add("Actionneur et face de montage non identifiés de façon fiable : précisez le point de dépose et le sens de retrait. Un volant ou un capot de filtre ne remplace pas un actionneur.");
                return missing;
            }
            if(operation==MaintenanceAccessOperation.LeverValve)return Lever(input.ToArray(),parameterInput.ToArray(),connectorInput.ToArray());
            var result = new MaintenanceFeatureSuggestion { Operation = operation, Manufacturer = false };
            var all = input.ToArray();
            var observations = all.Where(o => o.Center != null && o.Axis != null && o.Radius >= Mm(25) && o.Radius <= Mm(3000)
                && o.Axis.GetLength() > .99 && o.Coverage >= Math.PI * .9).ToArray();
            var parameters = parameterInput.Where(p => p.Value > 0 && !double.IsNaN(p.Value) && !double.IsInfinity(p.Value)).ToArray();
            var connectors = connectorInput.Where(c => c.Center != null && c.Axis != null).ToArray();
            var groups = new List<List<MaintenanceFeatureObservation>>();
            foreach (var o in observations.OrderByDescending(o => o.Radius))
            {
                var group = groups.FirstOrDefault(g => g.Any(q => SameRing(q, o)));
                if (group == null) { group = new List<MaintenanceFeatureObservation>(); groups.Add(group); }
                group.Add(o);
            }
            foreach (var group in groups)
            {
                var top = group.OrderByDescending(o => o.Radius).First();
                XYZ axis = top.Axis.Normalize();
                var sameRadius = group.Where(o => Math.Abs(o.Radius - top.Radius) <= Math.Max(Mm(2), top.Radius * .03)).ToArray();
                XYZ center = sameRadius.Select(o => o.Center).Aggregate(XYZ.Zero, (a, b) => a + b) / sameRadius.Length;
                double thickness = group.Max(o => (o.Center - center).DotProduct(axis) + o.AxialLength / 2)
                    - group.Min(o => (o.Center - center).DotProduct(axis) - o.AxialLength / 2);
                var candidate = new MaintenanceFeatureCandidate { CommandPoint = center, Axis = axis, Radius = top.Radius,
                    Label = top.Component, Score = 3 };
                var torus = group.Where(o => o.CenterlineRadius > 0 && o.RimThickness > 0 && Math.Abs(o.Radius - top.Radius) <= Math.Max(Mm(3), top.Radius * .08))
                    .OrderBy(o => Math.Abs(o.Radius - top.Radius)).FirstOrDefault();
                if (torus != null) { candidate.CenterlineRadius = torus.CenterlineRadius; candidate.HandleThickness = torus.RimThickness; }
                candidate.Evidence.Add("Géométrie circulaire : rayon " + (top.Radius * 304.8).ToString("0.#") + " mm, axe et centre mesurés.");
                if (group.Count > 1) { candidate.Score += 2; candidate.Evidence.Add("Plusieurs arcs/faces coaxiaux concordants."); }
                bool wheel = operation == MaintenanceAccessOperation.HandwheelValve || operation == MaintenanceAccessOperation.ChainwheelValve;
                string path = Normalize(string.Join(" ", group.Select(o => o.Component)));
                bool named = group.Any(o => o.ComponentExplicit) && (wheel ? Contains(path, "volant", "handwheel", "chainwheel")
                    || (operation == MaintenanceAccessOperation.ChainwheelValve && Contains(path, "reducteurachaine", "chaingear")) : Contains(path, "capot", "couvercle", "bouchon", "cover", "bonnet"));
                if (named) { candidate.Score += 4; candidate.Evidence.Add("Le composant géométrique porte un indice de " + (wheel ? "volant" : "capot") + "."); }
                if (thickness <= top.Radius * .6) candidate.Score += 2;
                else candidate.Score -= 3;
                var radiusParameters = parameters.Where(p => wheel ? p.Kind == MaintenanceFeatureDimensionKind.WheelDiameter || p.Kind == MaintenanceFeatureDimensionKind.WheelRadius
                    : p.Kind == MaintenanceFeatureDimensionKind.CapDiameter).ToArray();
                var matching = radiusParameters.Where(p => Math.Abs((p.Kind == MaintenanceFeatureDimensionKind.WheelRadius ? p.Value : p.Value / 2) - top.Radius)
                    <= Math.Max(Mm(5), top.Radius * .08)).ToArray();
                if (matching.Length > 0)
                {
                    candidate.Score += 7;
                    candidate.Evidence.Add("Dimension concordante : " + string.Join(", ", matching.Select(p => p.Name + " (" + p.Origin + ")").Distinct()));
                }
                foreach (var connector in connectors)
                {
                    double parallel = Math.Abs(axis.DotProduct(connector.Axis.Normalize()));
                    XYZ delta = center - connector.Center;
                    double radial = (delta - connector.Axis.Normalize() * delta.DotProduct(connector.Axis.Normalize())).GetLength();
                    if (parallel > .96 && radial < Math.Max(Mm(15), connector.Radius * 1.1))
                    {
                        candidate.Score -= 8;
                        candidate.Evidence.Add("Cercle aligné sur un raccordement : bride/bouche possible.");
                        break;
                    }
                }
                if (connectors.Length >= 2)
                {
                    XYZ flow = (connectors[connectors.Length - 1].Center - connectors[0].Center);
                    if (flow.GetLength() > Mm(20) && Math.Abs(axis.DotProduct(flow.Normalize())) < .8)
                    { candidate.Score += 2; candidate.Evidence.Add("Axe distinct de la ligne des raccordements."); }
                }
                if (!wheel && connectors.Length > 0)
                {
                    XYZ body = BranchOrigin(center, axis, connectors);
                    double outward = (center - body).DotProduct(axis);
                    if (outward < 0) axis = -axis;
                    candidate.Axis = axis;
                    double faceOffset = group.Max(o => (o.Center - center).DotProduct(axis) + o.AxialLength / 2);
                    candidate.CommandPoint = center + axis * faceOffset;
                    // At the intersection with the main flow line the signed
                    // direction is undefined. A construction circle there can
                    // appear terminal when its arbitrary normal points inward;
                    // it is not evidence of an external maintenance cover.
                    bool clearOfJunction = Math.Abs(outward) > Math.Max(Mm(10), top.Radius * .2);
                    if (clearOfJunction)
                    { candidate.Score += 2; candidate.Evidence.Add("Face extérieure proposée à l’opposé du centre des raccordements."); }
                    else candidate.Evidence.Add("Section au niveau de la jonction hydraulique : sa géométrie ne prouve pas une fermeture extérieure.");
                    var coaxial = observations.Where(o => o.Radius >= top.Radius * .65 && o.Radius <= top.Radius * 1.5
                        && Math.Abs(o.Axis.Normalize().DotProduct(axis)) > .985
                        && Radial(o.Center - center, axis) < Math.Max(Mm(5), top.Radius * .1)).ToArray();
                    double farthest = coaxial.Max(o => (o.Center - center).DotProduct(axis) + o.AxialLength / 2);
                    if (clearOfJunction && Math.Abs((candidate.CommandPoint - center).DotProduct(axis) - farthest) < Mm(3)
                        && coaxial.Any(o => (o.Center - center).DotProduct(axis) < -Math.Max(Mm(25), top.Radius * .4)))
                    { candidate.Score += 8; candidate.Evidence.Add("Fermeture circulaire terminale d’une branche distincte des raccordements."); }
                    else candidate.Score -= 4;
                }
                if (wheel && connectors.Length > 0)
                {
                    XYZ body = connectors.Select(c => c.Center).Aggregate(XYZ.Zero, (a, b) => a + b) / connectors.Length;
                    if ((center - body).DotProduct(candidate.Axis) < 0) candidate.Axis = -candidate.Axis;
                }
                if (wheel && operation == MaintenanceAccessOperation.ChainwheelValve && TryChain(top, all, out double drop, out double spacing))
                {
                    candidate.Score += 8;
                    candidate.Evidence.Add("Deux brins verticaux sous cette roue sont identifiés géométriquement.");
                }
                var duplicate = result.Candidates.FirstOrDefault(c => NearCandidate(c, candidate));
                if (duplicate == null) result.Candidates.Add(candidate);
                else if (candidate.Score > duplicate.Score) { result.Candidates.Remove(duplicate); result.Candidates.Add(candidate); }
            }
            result.Candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (result.Candidates.Count == 0 || result.Candidates[0].Score < 5)
            {
                result.Warnings.Add("Aucun volant/capot n’est suffisamment identifiable dans la géométrie disponible. Pointez le centre et choisissez l’axe manuellement.");
                return result;
            }
            var best = result.Candidates[0];
            result.Evidence.AddRange(best.Evidence);
            result.Ambiguous = result.Candidates.Count > 1 && result.Candidates[1].Score >= best.Score - 3;
            if (result.Ambiguous)
            {
                result.Confidence = MaintenanceFeatureConfidence.Low;
                result.Warnings.Add("Plusieurs pièces circulaires sont aussi plausibles : aucun repère n’est appliqué automatiquement. Confirmez le volant/capot dans la vue.");
                return result;
            }
            // Geometry alone does not distinguish a handwheel from every flange.
            bool parameterProof = best.Evidence.Any(e => e.StartsWith("Dimension concordante", StringComparison.Ordinal));
            bool semantic = best.Evidence.Any(e => e.StartsWith("Le composant", StringComparison.Ordinal));
            bool structuralCap = best.Evidence.Any(e => e.StartsWith("Fermeture circulaire terminale", StringComparison.Ordinal));
            bool chainProof = best.Evidence.Any(e => e.StartsWith("Deux brins verticaux", StringComparison.Ordinal));
            if (best.Score < 9 || (!parameterProof && !semantic && !structuralCap && !chainProof))
            {
                result.Confidence = MaintenanceFeatureConfidence.Low;
                result.Warnings.Add("Une pièce circulaire est visible mais sa fonction reste incertaine. Utilisez les candidats pour confirmer le point de commande.");
                return result;
            }
            result.CommandPoint = best.CommandPoint; result.Axis = best.Axis; result.Radius = best.Radius;
            result.Frame = Frame(best.CommandPoint, best.Axis);
            result.Confidence = parameterProof && semantic && best.Score >= 17 ? MaintenanceFeatureConfidence.High : MaintenanceFeatureConfidence.Medium;
            result.Warnings.Add("Repère proposé depuis la famille : vérifiez visuellement la pièce, le sens et les cotes avant le contrôle. Aucune notice constructeur n’a été lue.");
            if (operation == MaintenanceAccessOperation.HandwheelValve || operation == MaintenanceAccessOperation.ChainwheelValve)
            {
                var nominalValues = parameters.Where(p => p.Kind == MaintenanceFeatureDimensionKind.WheelRadius || p.Kind == MaintenanceFeatureDimensionKind.WheelDiameter)
                    .Select(p => p.Kind == MaintenanceFeatureDimensionKind.WheelRadius ? p.Value : p.Value / 2)
                    .Where(r => Math.Abs(r - best.Radius) <= Math.Max(Mm(5), best.Radius * .08)).ToArray();
                double nominal = nominalValues.Length > 0 && nominalValues.Max() - nominalValues.Min() <= Mm(2) ? nominalValues.Average() : 0;
                if (nominal > 0)
                {
                    result.Radius = nominal;
                    double section = 2 * (best.Radius - nominal);
                    if (section >= Mm(2) && section <= Math.Min(Mm(120), nominal * .8)) result.HandleThickness = section;
                    result.Evidence.Add("Rayon de prise / ligne moyenne : " + (nominal * 304.8).ToString("0.#")
                        + " mm, paramètre principal recoupé avec le rayon extérieur géométrique.");
                }
                else if (best.CenterlineRadius > 0 && best.HandleThickness > 0)
                {
                    result.Radius = best.CenterlineRadius; result.HandleThickness = best.HandleThickness;
                    result.Evidence.Add("Ligne moyenne et diamètre de section mesurés sur la couronne de révolution.");
                }
                if (!result.HasHandleThickness)
                {
                    // Keep an observed outer radius from being enlarged a second
                    // time by the default 35mm rendering tube. This 2mm is only
                    // the minimum supported visual trace, not a measured rim.
                    result.HandleThickness = Mm(2);
                    result.Warnings.Add((nominal <= 0 && best.CenterlineRadius <= 0 ? "Rayon extérieur observé utilisé. " : "Rayon de prise proposé. ")
                        + "Section de couronne non identifiable : tracé fin de 2 mm utilisé. Vérifiez le rayon de prise et la section réels du volant.");
                }
                else result.Evidence.Add("Diamètre de section représenté : " + (result.HandleThickness * 304.8).ToString("0.#") + " mm.");
            }
            if (operation == MaintenanceAccessOperation.ChainwheelValve)
            {
                // Reuse the confirmed outer wheel, not whichever coaxial
                // construction circle happens to be closest by floating-point
                // noise. A smaller inner chain contour can share its centre
                // but reject the actual brin spacing (Antibes1045267).
                var observed = new MaintenanceFeatureObservation { Center = best.CommandPoint, Axis = best.Axis, Radius = best.Radius };
                if (TryChain(observed, all, out double drop, out double spacing, out ChainSection section))
                {
                    result.ChainDrop = drop; result.ChainSpacing = spacing;
                    result.Evidence.Add("Descente et écartement mesurés sur les deux brins modélisés.");
                    result.Evidence.Add(section.Evidence);
                    double declared = Dimension(parameters, MaintenanceFeatureDimensionKind.ChainRadius);
                    if (section.Symmetric && declared > 0)
                    {
                        if (Math.Abs(declared - section.InnerRadius) < Mm(2))
                            result.Evidence.Add("Le paramètre « Chaine rayon » concorde avec le rayon intérieur de l’enveloppe de chaîne ("
                                + (declared * 304.8).ToString("0.#") + " mm), pas avec sa ligne moyenne.");
                        else if (Math.Abs(declared - section.CenterlineRadius) < Mm(2))
                            result.Evidence.Add("Le paramètre de rayon de chaîne concorde avec la ligne moyenne des brins mesurés.");
                    }
                }
                ApplyDimension(result, parameters, MaintenanceFeatureDimensionKind.ChainDrop, value => result.ChainDrop = value);
                ApplyDimension(result, parameters, MaintenanceFeatureDimensionKind.ChainSpacing, value => result.ChainSpacing = value);
                if (!result.HasChain) result.Warnings.Add("La chaîne complète n’est pas identifiable : descente et écartement restent à renseigner.");
            }
            if (operation == MaintenanceAccessOperation.Filter || operation == MaintenanceAccessOperation.Actuator)
            {
                result.DirectionAmbiguous = connectors.Length == 0 || Math.Abs((best.CommandPoint - BranchOrigin(best.CommandPoint, best.Axis, connectors)).DotProduct(best.Axis)) < Mm(10);
                if (result.DirectionAmbiguous) result.Warnings.Add("L’axe du capot est visible, mais le sens de retrait n’est pas déterminé : confirmez-le manuellement.");
                double length = Dimension(parameters, MaintenanceFeatureDimensionKind.CartridgeLength), diameter = Dimension(parameters, MaintenanceFeatureDimensionKind.CartridgeDiameter);
                if (length > 0 && diameter > 0)
                {
                    result.RemovalSize = new XYZ(diameter, diameter, length);
                    result.RemovalCenterOffset = new XYZ(0, 0, -length / 2);
                    result.Evidence.Add("Volume de cartouche proposé par ses paramètres de diamètre et longueur ; position intérieure à confirmer.");
                }
                ApplyDimension(result, parameters, MaintenanceFeatureDimensionKind.ExtractionTravel, value => result.RemovalTravel = value);
                if (!result.HasRemoval) result.Warnings.Add("Le capot ne donne pas les dimensions du tamis/de la cartouche. Aucun volume de pièce interne n’est inventé.");
                if (!result.HasRemovalTravel) result.Warnings.Add("La course de retrait n’est pas renseignée dans les paramètres reconnus : reportez-la depuis la pièce ou la notice.");
            }
            return result;
        }

        private static void ApplyDimension(MaintenanceFeatureSuggestion result, MaintenanceFeatureDimension[] parameters, MaintenanceFeatureDimensionKind kind, Action<double> apply)
        {
            double value = Dimension(parameters, kind);
            if (value <= 0) return;
            apply(value);
            result.Evidence.Add("Paramètre de famille : " + string.Join(", ", parameters.Where(p => p.Kind == kind).Select(p => p.Name + " (" + p.Origin + ")").Distinct()) + ".");
        }
        private static double Dimension(MaintenanceFeatureDimension[] parameters, MaintenanceFeatureDimensionKind kind)
        {
            var values = parameters.Where(p => p.Kind == kind).Select(p => p.Value).ToArray();
            return values.Length > 0 && values.Max() - values.Min() <= Math.Max(Mm(2), values.Max() * .02) ? values.Average() : 0;
        }
        private static bool SameRing(MaintenanceFeatureObservation a, MaintenanceFeatureObservation b)
        {
            if (a.Component != b.Component || Math.Abs(a.Axis.Normalize().DotProduct(b.Axis.Normalize())) < .985) return false;
            XYZ axis = a.Axis.Normalize(), delta = b.Center - a.Center;
            return (delta - axis * delta.DotProduct(axis)).GetLength() <= Math.Max(Mm(3), a.Radius * .025)
                && Math.Abs(delta.DotProduct(axis)) <= Math.Max(Mm(15), Math.Max(a.Radius, b.Radius) * .35)
                && Math.Abs(a.Radius - b.Radius) <= Math.Max(Mm(10), Math.Max(a.Radius, b.Radius) * .3);
        }
        private static bool NearCandidate(MaintenanceFeatureCandidate a, MaintenanceFeatureCandidate b)
            => (a.CommandPoint - b.CommandPoint).GetLength() < Math.Max(Mm(8), a.Radius * .08)
                && Math.Abs(a.Axis.DotProduct(b.Axis)) > .98 && Math.Abs(a.Radius - b.Radius) < Math.Max(Mm(5), a.Radius * .08);
        private static Transform Frame(XYZ point, XYZ axis)
        {
            XYZ z = axis.Normalize(), x = XYZ.BasisZ.CrossProduct(z);
            if (x.GetLength() < 1e-6) x = XYZ.BasisX - z * XYZ.BasisX.DotProduct(z);
            x = x.Normalize();
            var frame = Transform.Identity; frame.Origin = point; frame.BasisX = x; frame.BasisY = z.CrossProduct(x); frame.BasisZ = z; return frame;
        }
        private static bool TryChain(MaintenanceFeatureObservation wheel, MaintenanceFeatureObservation[] all, out double drop, out double spacing)
            => TryChain(wheel, all, out drop, out spacing, out ChainSection ignored);
        private static bool TryChain(MaintenanceFeatureObservation wheel, MaintenanceFeatureObservation[] all, out double drop, out double spacing, out ChainSection section)
        {
            drop = spacing = 0;
            section = null;
            if (Math.Abs(wheel.Axis.Normalize().DotProduct(XYZ.BasisZ)) > .15) return false;
            var rawBranches = all.Where(o => (o.Cylinder || o.Linear) && o.AxialLength >= Mm(300) && (o.Linear || o.Radius >= Mm(1)) && o.Radius <= Mm(20)
                && Math.Abs(o.Axis.Normalize().DotProduct(XYZ.BasisZ)) > .98
                && Math.Abs(o.Center.Z + o.AxialLength / 2 - wheel.Center.Z) < Math.Max(Mm(100), wheel.Radius * .5)
                && (new XYZ(o.Center.X - wheel.Center.X, o.Center.Y - wheel.Center.Y, 0)).GetLength() < wheel.Radius * 1.3 + Mm(30)).ToArray();
            // A represented brin may contain its two outline lines and several
            // cylindrical half-faces. Regroup the local cross section before
            // pairing branches; selecting the first two outer edges overstates
            // spacing by one wire diameter on each side.
            var branchGroups = new List<List<MaintenanceFeatureObservation>>();
            foreach (var item in rawBranches)
            {
                var group = branchGroups.FirstOrDefault(g => g.Any(q => new XYZ(q.Center.X - item.Center.X, q.Center.Y - item.Center.Y, 0).GetLength()
                    <= Math.Max(Mm(25), 2 * Math.Max(q.Radius, item.Radius)))
                    && g.All(q => Math.Abs(q.Center.Z - item.Center.Z) < Math.Max(Mm(100), q.AxialLength * .15)));
                if (group == null) { group = new List<MaintenanceFeatureObservation>(); branchGroups.Add(group); }
                group.Add(item);
            }
            var branches = branchGroups.Select(g =>
            {
                double loX = g.Min(o => o.Center.X - o.Radius), hiX = g.Max(o => o.Center.X + o.Radius);
                double loY = g.Min(o => o.Center.Y - o.Radius), hiY = g.Max(o => o.Center.Y + o.Radius);
                double loZ = g.Min(o => o.Center.Z - o.AxialLength / 2), hiZ = g.Max(o => o.Center.Z + o.AxialLength / 2);
                return new MaintenanceFeatureObservation { Center = new XYZ((loX + hiX) / 2, (loY + hiY) / 2, (loZ + hiZ) / 2),
                    AxialLength = hiZ - loZ, Axis = XYZ.BasisZ, Linear = true };
            }).ToArray();
            for (int a = 0; a < branches.Length; a++)
            for (int b = a + 1; b < branches.Length; b++)
            {
                var left = branches[a]; var right = branches[b];
                double separation = new XYZ(left.Center.X - right.Center.X, left.Center.Y - right.Center.Y, 0).GetLength();
                if (separation < Mm(30) || separation > wheel.Radius * 2.2 || Math.Abs(left.AxialLength - right.AxialLength) > Math.Max(Mm(100), left.AxialLength * .15)) continue;
                XYZ middle = (left.Center + right.Center) / 2;
                if (new XYZ(middle.X - wheel.Center.X, middle.Y - wheel.Center.Y, 0).GetLength() > Math.Max(Mm(30), wheel.Radius * .2)) continue;
                drop = wheel.Center.Z - Math.Min(left.Center.Z - left.AxialLength / 2, right.Center.Z - right.AxialLength / 2);
                spacing = separation;
                XYZ tangent = XYZ.BasisZ.CrossProduct(wheel.Axis.Normalize()).Normalize();
                double[] Bounds(List<MaintenanceFeatureObservation> group)
                {
                    return new[] { group.Min(o => (o.Center - wheel.Center).DotProduct(tangent) - o.Radius),
                        group.Max(o => (o.Center - wheel.Center).DotProduct(tangent) + o.Radius) };
                }
                double[] first = Bounds(branchGroups[a]), second = Bounds(branchGroups[b]);
                double firstMiddle = (first[0] + first[1]) / 2, secondMiddle = (second[0] + second[1]) / 2;
                section = new ChainSection { InnerRadius = (Math.Min(Math.Abs(first[0]), Math.Abs(first[1])) + Math.Min(Math.Abs(second[0]), Math.Abs(second[1]))) / 2,
                    OuterRadius = (Math.Max(Math.Abs(first[0]), Math.Abs(first[1])) + Math.Max(Math.Abs(second[0]), Math.Abs(second[1]))) / 2,
                    CenterlineRadius = separation / 2,
                    Symmetric = firstMiddle * secondMiddle < 0 && Math.Abs(firstMiddle + secondMiddle) < Mm(2) };
                section.Evidence = "Sections des brins, perpendiculairement à l’axe de roue : [" + (first[0] * 304.8).ToString("0.#") + "; "
                    + (first[1] * 304.8).ToString("0.#") + "] et [" + (second[0] * 304.8).ToString("0.#") + "; "
                    + (second[1] * 304.8).ToString("0.#") + "] mm ; entraxe des lignes moyennes " + (spacing * 304.8).ToString("0.#") + " mm.";
                double ends = Math.Min(left.Center.Z - left.AxialLength / 2, right.Center.Z - right.AxialLength / 2);
                var loops = all.Where(o => !o.Cylinder && !o.Linear && o.CurvePoints != null && o.CurvePoints.Length > 2
                    && Math.Abs(o.Axis.Normalize().DotProduct(wheel.Axis.Normalize())) > .98
                    && o.Radius > separation * .35 && o.Radius < separation * .65
                    && Math.Abs(o.Center.Z - ends) < Math.Max(Mm(30), separation * .25)
                    && new XYZ(o.Center.X - middle.X, o.Center.Y - middle.Y, 0).GetLength() < Mm(30)).ToArray();
                if (loops.Length > 0) drop = wheel.Center.Z - Math.Min(ends, loops.SelectMany(o => o.CurvePoints).Min(q => q.Z));
                return true;
            }
            return false;
        }
        private static double Radial(XYZ delta, XYZ axis) => (delta - axis * delta.DotProduct(axis)).GetLength();
        private static XYZ BranchOrigin(XYZ center, XYZ axis, MaintenanceFeatureConnector[] connectors)
        {
            XYZ middle = connectors.Select(c => c.Center).Aggregate(XYZ.Zero, (a, b) => a + b) / connectors.Length;
            if (connectors.Length < 2) return middle;
            XYZ line = connectors[connectors.Length - 1].Center - connectors[0].Center;
            if (line.GetLength() < Mm(20)) return middle;
            line = line.Normalize();
            double parallel = axis.DotProduct(line), denominator = 1 - parallel * parallel;
            if (denominator < 1e-4) return middle;
            XYZ offset = center - middle;
            double along = (parallel * offset.DotProduct(line) - offset.DotProduct(axis)) / denominator;
            return center + axis * along;
        }
    }
}
