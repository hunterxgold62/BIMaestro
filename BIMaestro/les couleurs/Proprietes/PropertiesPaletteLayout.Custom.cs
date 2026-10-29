using System;
using System.Collections.Generic;
using System.Linq;
using BIMaestro.Localization;

namespace BIMaestro.PropertiesAppearance
{
    internal static partial class PropertiesPaletteLayout
    {
        private static int _originalRowCount,_customRowCount;
        private static string _customSignature;
        private static string _customNotice;
        private static string LayoutStatus()
        {
            string text=string.Join(" ",new[]{HasApplied?UiLanguage.T("Ordre personnalisé appliqué à la palette Revit.", "Custom order applied to the Revit palette."):null,_customNotice}.Where(s=>!string.IsNullOrEmpty(s)));
            return text.Length>0?text:null;
        }
        private static readonly Dictionary<int,string> _customMembership=new Dictionary<int,string>();
        internal static PropertyTarget ParameterAt(NativePropertiesGrid grid,AppearanceContext context,uint row)
        {
            if(_customRowCount==0||grid.Window!=_window||grid.Core!=_core||context.Signature!=_context||row<1||row>_current.Count)return null;
            int token=_current[(int)row-1];
            foreach(var mapping in _parameterTokens.Where(p=>p.Value==token))
                if(grid.FindParameterRow(mapping.Key)==(int)row)return context.Parameters.FirstOrDefault(p=>p.NativeParameterId==mapping.Key);
            return null;
        }
        internal static string CustomGroupAt(NativePropertiesGrid grid,uint row)
        {
            if(_customRowCount==0||grid.Window!=_window||grid.Core!=_core||row<1||row>_current.Count)return null;
            return _customMembership.TryGetValue(_current[(int)row-1],out var group)?group:null;
        }
        internal static List<PropertyPreviewRow> OriginalPreview(NativePropertiesGrid grid,AppearanceContext context)
        {
            if(_customRowCount==0||context.Signature!=_context||grid.Window!=_window||grid.Core!=_core||!ParameterMappingMatches(grid,_current))return null;
            return _baseline.Take(_originalRowCount).Where(r=>r.Preview!=null).Select(r=>{
                var p=context.Parameters.FirstOrDefault(t=>t.Key==r.Preview.ParameterKey);int row=_current.IndexOf(r.Token)+1;
                return new PropertyPreviewRow{Header=r.Preview.Header,GroupKey=r.Preview.GroupKey,ParameterKey=r.Preview.ParameterKey,Label=r.Preview.Label,
                    Value=r.Preview.Header?"":p?.DisplayValue??grid.Value((uint)row,2),ReadOnly=r.Preview.Header?r.Preview.GroupKey==null||r.Preview.GroupKey.StartsWith("native:",StringComparison.Ordinal):p==null||p.ReadOnly||!grid.CanStyle((uint)row)};
            }).ToList();
        }
        private static void PrepareCustomGroups(NativePropertiesGrid grid,AppearanceContext context,AppearanceLayout layout,string version,Action beforeMove)
        {
            var definitions=(layout.CustomGroups??new List<AppearanceCustomGroup>()).ToList();
            string signature=Newtonsoft.Json.JsonConvert.SerializeObject(definitions);
            if(_customSignature==signature)return;
            if(_customRowCount==0&&definitions.Count==0){_customSignature=signature;_customNotice=null;return;}
            _customNotice=null;
            try{NativeCustomGroupRows.Qualify(version);}
            catch(Exception error)when(_customRowCount==0){_customSignature=signature;_customNotice=error.Message;return;}
            long revision=PropertiesAppearanceService.ModelRevision;
            if(_customRowCount>0)
            {
                RestoreBaseline(grid,beforeMove);
                var expected=Read(grid).Take(_originalRowCount).ToList();
                RequiresNativeRebuild=true;
                using(grid.DeferDrawing())NativeCustomGroupRows.RemoveTail(grid,(uint)_originalRowCount+1);
                if(!SameCells(Read(grid),expected))throw new InvalidOperationException("Le retrait des bandeaux n’a pas conservé les lignes natives attendues.");
                if(!Capture(grid,context,Read(grid)))throw new InvalidOperationException("Les paramètres natifs ne sont pas synchronisés après retrait du bandeau.");
                if(!ParameterMappingMatches(grid,_current))throw new InvalidOperationException("Identités natives modifiées après retrait du bandeau.");
                RequiresNativeRebuild=false;
            }
            // Resolve every member by its saved parameter key before the first insertion.
            var plans=new List<Tuple<AppearanceCustomGroup,List<Tuple<string,List<int>>>>>();
            var assigned=new HashSet<int>();
            foreach(var definition in definitions)
            {
                if(string.IsNullOrWhiteSpace(definition.Name)||definition.Name.Length>80||definition.Name.Any(char.IsControl))throw new InvalidOperationException("Nom de bandeau invalide.");
                if(_baseline.Take(_originalRowCount).Any(r=>r.Preview?.Header==true&&AppearancePreferences.Normalize(r.Label)==AppearancePreferences.Normalize(definition.Name)))throw new InvalidOperationException("Ce nom est déjà utilisé par un groupe Revit.");
                if(_groups.Any(g=>g.Key==definition.Key))throw new InvalidOperationException("Identifiant de groupe dupliqué.");
                var members=new List<Tuple<string,List<int>>>();
                foreach(var key in definition.Members)
                {
                    var p=context.Parameters.SingleOrDefault(t=>t.Key==key);
                    if(p==null||p.ReadOnly)continue;
                    if(!p.NativeParameterId.HasValue||!_parameterTokens.TryGetValue(p.NativeParameterId.Value,out var token))continue;
                    int row=_current.IndexOf(token)+1;
                    if(!grid.CanStyle((uint)row))continue;
                    if(!assigned.Add(token))throw new InvalidOperationException("Un paramètre ne peut appartenir qu’à un bandeau personnalisé.");
                    var block=_groups.SelectMany(g=>g.Parameters).SingleOrDefault(pair=>pair.Item2.Contains(token));
                    if(block==null)throw new InvalidOperationException("Bloc de paramètre natif introuvable.");
                    members.Add(block);
                }
                plans.Add(Tuple.Create(definition,members));
            }
            if(plans.Count>20)throw new InvalidOperationException("Maximum : 20 bandeaux personnalisés.");
            if(_baseline.Count+plans.Count*2>2000)throw new InvalidOperationException("La palette contient trop de lignes pour ajouter ces bandeaux.");
            if(plans.Count>0)beforeMove?.Invoke();
            using(grid.DeferDrawing())
            {
                foreach(var plan in plans)
                {
                    int token=_baseline.Count;RequiresNativeRebuild=true;
                    NativeCustomGroupRows.Add(grid,plan.Item1.Name);
                    var added=Read(grid).Take(2).ToArray();added[0].Token=token;added[1].Token=token+1;
                    added[1].Preview=new PropertyPreviewRow{Header=true,GroupKey=plan.Item1.Key,Label=plan.Item1.Name};
                    _baseline.AddRange(added);_current.InsertRange(0,new[]{token,token+1});_customRowCount+=2;
                    if(!ParameterMappingMatches(grid,_current))throw new InvalidOperationException("Identités natives modifiées après insertion du bandeau.");
                    var group=new GroupBlock{Key=plan.Item1.Key,Prefix=new List<int>{token,token+1},Parameters=plan.Item2};
                    foreach(var member in plan.Item2)
                    {
                        foreach(var origin in _groups)origin.Parameters.Remove(member);
                        foreach(int memberToken in member.Item2)_customMembership[memberToken]=plan.Item1.Key;
                    }
                    _customMembership[token+1]=plan.Item1.Key;_groups.Add(group);RequiresNativeRebuild=false;
                }
            }
            if(PropertiesAppearanceService.ModelRevision!=revision)throw new InvalidOperationException("Le modèle a changé pendant la préparation des bandeaux ; actualisation suspendue.");
            _customSignature=signature;
        }
    }
}
