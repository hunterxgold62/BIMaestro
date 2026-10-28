using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        internal static Element ResolveNativeMember(Document source, Element root, string historicalId)
        {
            if(string.IsNullOrWhiteSpace(historicalId)) return root;
            if(NativeHistoricalIdentity(root)==historicalId) return root;
            var tagged=new FilteredElementCollector(source).WherePasses(new Autodesk.Revit.DB.ExtensibleStorage.ExtensibleStorageFilter(NativeCopySchemaId))
                .Where(e=>NativeHistoricalIdentity(e)==historicalId).ToList();
            if(tagged.Count>0)
            {
                if(tagged.Count!=1 || OpeningRoot(tagged[0]).Id!=root.Id)
                    throw new InvalidOperationException("Membre natif absent de son ensemble ou ambigu : "+historicalId);
                return tagged[0];
            }
            var legacy=Aggregate(source,root).Where(e=>ElementHistoryRestoration.GetOrigins(e)?.Contains(historicalId)==true).ToList();
            if(legacy.Count!=1) throw new InvalidOperationException("Membre historique introuvable ou ambigu : "+historicalId);
            return legacy[0];
        }

        // Validate only after Commit, inside the still-rollbackable group.
        internal static void VerifyNativeHosts(Document source, Dictionary<string, Element> mapping, Document destination)
        {
            var resolved = new Dictionary<string, Element>(StringComparer.Ordinal);
            List<Element> retained = null;
            bool IsUniqueId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length > 37
                && value[36] == '-' && Guid.TryParse(value.Substring(0,36), out _) && value.Substring(37).All(Uri.IsHexDigit);
            Element ExpectedTarget(Element expected)
            {
                if (expected == null) return null;
                if (resolved.TryGetValue(expected.UniqueId,out var known)) return known;
                if (mapping.TryGetValue(expected.UniqueId,out var copied))
                {
                    var actual=copied?.IsValidObject==true ? destination.GetElement(copied.Id) : null;
                    if(actual==null) throw new InvalidOperationException("Copie native annulée : support copié disparu : "+expected.UniqueId);
                    return resolved[expected.UniqueId]=actual;
                }
                var direct=ElementHistoryReconstruction.FindOriginal(destination,expected.UniqueId);
                if(direct!=null) return resolved[expected.UniqueId]=direct;
                var identities=new HashSet<string>((ElementHistoryRestoration.GetOrigins(expected)??new List<string>())
                    .Concat(new[]{expected.UniqueId,NativeHistoricalIdentity(expected)}).Where(IsUniqueId),StringComparer.Ordinal);
                var matches=identities.Select(uid=>ElementHistoryReconstruction.FindOriginal(destination,uid))
                    .Where(e=>e!=null&&e.GetType()==expected.GetType()).ToList();
                retained??=new FilteredElementCollector(destination).WhereElementIsNotElementType().ToElements().ToList();
                matches.AddRange(retained.Where(e=>e.GetType()==expected.GetType()
                    &&(ElementHistoryRestoration.GetOrigins(e)??new List<string>()).Any(identities.Contains)));
                matches=matches.GroupBy(e=>e.Id).Select(g=>g.First()).ToList();
                if(matches.Count!=1) throw new InvalidOperationException("Copie native annulée : support historique introuvable ou ambigu : "+expected.UniqueId+" ("+matches.Count+").");
                return resolved[expected.UniqueId]=matches[0];
            }
            void CheckReference(Element original,string relation,Element expected,Element actual)
            {
                var required=ExpectedTarget(expected);
                if(required==null&&actual==null || required!=null&&actual?.Id==required.Id) return;
                throw new InvalidOperationException("Copie native annulée : "+relation+" non conservé pour "+NativeCopyIdentity(original)
                    +". Attendu : "+NativeCopyIdentity(required)+"; obtenu : "+NativeCopyIdentity(actual));
            }
            foreach(var pair in mapping)
            {
                var original=source.GetElement(pair.Key);
                var actual=pair.Value?.IsValidObject==true ? destination.GetElement(pair.Value.Id) : null;
                if(original==null||actual==null) throw new InvalidOperationException("Objet disparu avant validation des supports : "+pair.Key);
                if(original is FamilyInstance originalFamily)
                {
                    if(!(actual is FamilyInstance actualFamily)) throw new InvalidOperationException("Classe de famille modifiée : "+pair.Key);
                    CheckReference(original,"hôte",originalFamily.Host,actualFamily.Host);
                    CheckReference(original,"composant parent",originalFamily.SuperComponent,actualFamily.SuperComponent);
                }
#if REVIT2024 || REVIT2025_OR_GREATER
                if(original is Toposolid originalTerrain)
                {
                    if(!(actual is Toposolid actualTerrain)) throw new InvalidOperationException("Classe de terrain modifiée : "+pair.Key);
                    CheckReference(original,"terrain hôte",source.GetElement(originalTerrain.HostTopoId),destination.GetElement(actualTerrain.HostTopoId));
                }
#endif
            }
        }
    }
}
