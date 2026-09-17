using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BIMaestro.Codex
{
    internal sealed class GridComponent
    {
        internal string Name, Material;
        internal double[] Min, Max;
        internal bool Coarse, Medium, Fine;
    }
    internal sealed class ComponentGridSpec
    {
        internal string Name, ColumnsParameter, RowsParameter;
        internal int U, V;
        internal double[] Size, Gap;
        internal LengthExpression Width, Height;
        internal GridComponent[] Components;
        internal int Columns(Dictionary<string, double> values) => (int)Math.Floor((Width.Value(values) + Gap[0]) / (Size[U] + Gap[0]) + 1e-9);
        internal double Volume => Components.Where(c => c.Fine).Any() ? VolumeAt(2) : Components.Where(c => c.Medium).Any() ? VolumeAt(1) : VolumeAt(0);
        private double VolumeAt(int detail) => Components.Where(c => detail == 2 ? c.Fine : detail == 1 ? c.Medium : c.Coarse).Sum(c => Enumerable.Range(0, 3).Aggregate(1d, (n,a) => n * (c.Max[a] - c.Min[a])));
        internal static JObject Schema(JObject expression)
        {
            JObject Obj(JObject fields) => new JObject { ["type"]="object", ["properties"]=fields, ["required"]=new JArray(fields.Properties().Select(p=>p.Name)), ["additionalProperties"]=false };
            JObject Vec(int count, double min) => new JObject { ["type"]="array", ["minItems"]=count, ["maxItems"]=count, ["items"]=new JObject { ["type"]="number", ["minimum"]=min, ["maximum"]=100000 } };
            JObject Name() => new JObject { ["type"]="string", ["maxLength"]=40, ["pattern"]="^[A-Za-z][A-Za-z0-9_]{0,39}$" };
            return new JObject { ["type"]="array", ["minItems"]=0, ["maxItems"]=4,
                ["description"]="Grilles rectangulaires de MODULES COMPLETS imbriqués (cadre, vitrage, cellules...). Chaque composant est un bloc fixe avec son matériau et ses niveaux de détail. Les dimensions du module restent constantes ; extent_u/extent_v pilotent le nombre de colonnes/lignes par rounddown((étendue+jeu)/(module+jeu)). Origine locale [0,0,0], deux axes distincts, 0/1 gérés par visibilité conditionnelle. Au plus 200 modules visibles et 750 solides. Fournir les dimensions d'une référence fabricant connue ; ne pas prétendre qu'il existe une taille universelle de panneau solaire.",
                ["items"]=Obj(new JObject {
                    ["name"]=Name(), ["columns_parameter"]=Name(), ["rows_parameter"]=Name(),
                    ["axis_u"]=new JObject { ["type"]="string", ["enum"]=new JArray("x","y","z") },
                    ["axis_v"]=new JObject { ["type"]="string", ["enum"]=new JArray("x","y","z") },
                    ["module_size_mm"]=Vec(3,1), ["gap_uv_mm"]=Vec(2,0), ["extent_u"]=expression.DeepClone(), ["extent_v"]=expression.DeepClone(),
                    ["components"]=new JObject { ["type"]="array", ["minItems"]=1, ["maxItems"]=20, ["items"]=Obj(new JObject {
                        ["name"]=Name(), ["material"]=new JObject { ["type"]="string", ["maxLength"]=70 },
                        ["minimum_mm"]=Vec(3,0), ["maximum_mm"]=Vec(3,1),
                        ["coarse"]=new JObject { ["type"]="boolean" }, ["medium"]=new JObject { ["type"]="boolean" }, ["fine"]=new JObject { ["type"]="boolean" }
                    }) }
                }) };
        }
        internal static void Parse(JObject source, CodexParametricDesign design, HashSet<string> names)
        {
            if (source["component_grids"] == null) return;
            foreach (JObject item in CodexFamilyDesign.Items(source,"component_grids",0,4))
            {
                CodexFamilyDesign.Keys(item,"name","columns_parameter","rows_parameter","axis_u","axis_v","module_size_mm","gap_uv_mm","extent_u","extent_v","components");
                double[] Vec(JObject obj,string key,int count,double min) => CodexFamilyDesign.Items(obj,key,count,count).Select(t=>CodexFamilyDesign.Scalar(t,key,min,100000)).ToArray();
                var grid=new ComponentGridSpec { Name=CodexFamilyDesign.String(item,"name",40), ColumnsParameter=CodexFamilyDesign.String(item,"columns_parameter",40), RowsParameter=CodexFamilyDesign.String(item,"rows_parameter",40),
                    U="xyz".IndexOf(CodexFamilyDesign.String(item,"axis_u",1),StringComparison.Ordinal), V="xyz".IndexOf(CodexFamilyDesign.String(item,"axis_v",1),StringComparison.Ordinal),
                    Size=Vec(item,"module_size_mm",3,1), Gap=Vec(item,"gap_uv_mm",2,0), Width=LengthExpression.Parse(item["extent_u"] as JObject,names), Height=LengthExpression.Parse(item["extent_v"] as JObject,names) };
                if(grid.U<0 || grid.V<0 || grid.U==grid.V) throw new InvalidOperationException("Une grille exige deux axes distincts.");
                var reserved=design.Parameters.Concat(design.Angles).Select(p=>p.Name).Concat(design.Registry?.Parameters.Select(p=>p.Name)??Enumerable.Empty<string>()).Concat(design.Arrays.SelectMany(a=>new[]{a.CountParameter,a.Grid?.ColumnsParameter})).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(string name in new[]{grid.ColumnsParameter,grid.RowsParameter})
                    if(!Regex.IsMatch(name,"^[A-Za-z][A-Za-z0-9_]{0,39}$") || name.StartsWith("BIM_",StringComparison.OrdinalIgnoreCase) || !reserved.Add(name)) throw new InvalidOperationException("Nom de nombre réservé ou en double : "+name);
                if(design.Parts.Any(p=>p.Name==grid.Name)||design.Arrays.Any(a=>a.Name==grid.Name)) throw new InvalidOperationException("Nom de grille en double : "+grid.Name);
                grid.Components=CodexFamilyDesign.Items(item,"components",1,20).Cast<JObject>().Select(c=>{
                    CodexFamilyDesign.Keys(c,"name","material","minimum_mm","maximum_mm","coarse","medium","fine");
                    var part=new GridComponent { Name=CodexFamilyDesign.String(c,"name",40),Material=CodexFamilyDesign.String(c,"material",70),Min=Vec(c,"minimum_mm",3,0),Max=Vec(c,"maximum_mm",3,1) };
                    foreach(var key in new[]{"coarse","medium","fine"}) if(c[key]?.Type!=JTokenType.Boolean) throw new InvalidOperationException("Visibilité booléenne attendue : "+key);
                    part.Coarse=(bool)c["coarse"];part.Medium=(bool)c["medium"];part.Fine=(bool)c["fine"];
                    if(!design.Metadata.Materials.Any(m=>m.Name==part.Material)||Enumerable.Range(0,3).Any(a=>part.Max[a]-part.Min[a]<1 || part.Max[a]>grid.Size[a]) || !(part.Coarse||part.Medium||part.Fine)) throw new InvalidOperationException("Composant de module invalide : "+part.Name);
                    return part;
                }).ToArray();
                if(grid.Components.Select(c=>c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=grid.Components.Length) throw new InvalidOperationException("Noms de composants en double.");
                // Every populated detail level must describe the declared module envelope.
                for(int level=0;level<3;level++)
                {
                    var parts=grid.Components.Where(c=>level==0?c.Coarse:level==1?c.Medium:c.Fine).ToArray();
                    if(parts.Length>0 && Enumerable.Range(0,3).Any(a=>Math.Abs(parts.Min(c=>c.Min[a]))>0.01 || Math.Abs(parts.Max(c=>c.Max[a])-grid.Size[a])>0.01)) throw new InvalidOperationException("Les composants doivent couvrir l'encombrement du module à chaque niveau de détail utilisé.");
                }
                var max=grid.Size.Select(n=>new LengthExpression{Offset=n}).ToArray();max[grid.U]=grid.Width;
                design.Arrays.Add(new ParametricArray { Name=grid.Name,Material=grid.Components[0].Material, CountParameter=grid.RowsParameter,Axis=grid.V,SmallCounts=true,Grid=grid,Composite=true,
                    Min=Enumerable.Range(0,3).Select(_=>new LengthExpression()).ToArray(),Max=max,Span=LengthExpression.Combine(grid.Height,new LengthExpression{Offset=grid.Gap[1]}),Pitch=new LengthExpression{Offset=grid.Size[grid.V]+grid.Gap[1]} });
            }
        }
    }
}
