using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal static class CodexComponentGridBuilder
    {
        private const string MaterialPrefix = "BIM_GridMaterial_";
        internal static FamilySymbol Prepare(Document host, string template, CodexParametricDesign design, ParametricArray outer)
        {
            var grid = outer.Grid;
            Document module = null, row = null;
            try
            {
                module = host.Application.NewFamilyDocument(template);
                using (var tx = Begin(module,"Module complet de la grille"))
                {
                    var fm = module.FamilyManager; if(fm.CurrentType==null) fm.NewType("Standard");
                    var moduleDesign = new CodexParametricDesign();
                    var originBuilder = new CodexParametricBuilder(module,moduleDesign);
                    originBuilder.Build(new Dictionary<string,FamilyParameter>());
                    foreach(var plane in new FilteredElementCollector(module).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Where(p=>p.Name.StartsWith("BIM_Origine_",StringComparison.Ordinal))) plane.get_Parameter(BuiltInParameter.ELEM_IS_REFERENCE).Set((int)FamilyInstanceReferenceType.StrongReference);
                    for(int a=0;a<3;a++) fm.Set(fm.AddParameter("Dimension"+"XYZ"[a],GroupTypeId.Geometry,SpecTypeId.Length,true),grid.Size[a]/304.8);
                    fm.AddParameter("Materiau",GroupTypeId.Materials,SpecTypeId.Reference.Material,true);
                    var visible=fm.AddParameter("BIM_Visible",GroupTypeId.Visibility,SpecTypeId.Boolean.YesNo,true);fm.Set(visible,1);
                    var materials=Materials(module,design);
                    foreach(var part in grid.Components)
                    {
                        var primitive=new FamilyPrimitive { Kind="box",Size=part.Max.Select((n,a)=>n-part.Min[a]).ToArray(),Position=part.Min,Rotation=new double[3],Profile=new double[0][] };
                        using(var solid=CodexFamilyBuilder.MakeSolid(primitive))
                        {
                            var form=FreeFormElement.Create(module,solid);
                            fm.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM),materials[part.Material]);
                            fm.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM),visible);
                            CodexParameterBuilder.SetDisplay(module,form,new FamilyDisplaySpec { Component=part.Name,Subcategory=part.Name,Coarse=part.Coarse,Medium=part.Medium,Fine=part.Fine });
                        }
                    }
                    CodexFamilyBuilder.ApplyBranding(fm); Commit(tx);
                }
                row=host.Application.NewFamilyDocument(template);
                var loadedModule=module.LoadFamily(row)??throw new InvalidOperationException("Chargement du module complet refusé.");
                var moduleSymbol=loadedModule.GetFamilySymbolIds().Select(row.GetElement).OfType<FamilySymbol>().First();
                var rowDesign=new CodexParametricDesign { Registry=new CodexFamilyParameters() };
                rowDesign.Registry.Types.Add(new FamilyTypeSpec { Name="Standard" });
                for(int a=0;a<3;a++)
                {
                    double value=a==grid.U?grid.Width.Value(design.Initial):grid.Size[a]; string name="Dimension"+"XYZ"[a];
                    rowDesign.Parameters.Add(new DrivingLength { Name=name,Value=value,TestValue=value });
                    rowDesign.Registry.Parameters.Add(new FamilyParameterSpec { Name=name,Kind="length",Group="geometry",Description="Étendue de la rangée",Instance=true,Value=FamilyValue.Numeric(value,1,0) });
                }
                rowDesign.Registry.Parameters.Add(new FamilyParameterSpec { Name="BIM_Visible",Kind="yesno",Group="visibility",Instance=true,Value=FamilyValue.Bool(true) });
                rowDesign.Registry.Displays.Add(new FamilyDisplaySpec { Component="Modules",VisibleParameter="BIM_Visible" });
                var width=new LengthExpression();width.Terms.Add("Dimension"+"XYZ"[grid.U],1);
                var inner=new ParametricArray { Name="Modules",Material=outer.Material,CountParameter=grid.ColumnsParameter,Axis=grid.U,SmallCounts=true,Composite=true,
                    Min=Enumerable.Range(0,3).Select(_=>new LengthExpression()).ToArray(),Max=grid.Size.Select(n=>new LengthExpression{Offset=n}).ToArray(),
                    Span=LengthExpression.Combine(width,new LengthExpression{Offset=grid.Gap[0]}),Pitch=new LengthExpression{Offset=grid.Size[grid.U]+grid.Gap[0]} };
                rowDesign.Arrays.Add(inner);
                using(var tx=Begin(row,"Rangée de modules complets"))
                {
                    loadedModule.Name="BIMaestro_Module_"+grid.Name+"_"+Guid.NewGuid().ToString("N").Substring(0,6);
                    var fm=row.FamilyManager;if(fm.CurrentType==null)fm.NewType("Standard");
                    fm.AddParameter("Materiau",GroupTypeId.Materials,SpecTypeId.Reference.Material,true);
                    var materials=Materials(row,design);
                    var builder=new CodexParametricBuilder(row,rowDesign);
                    var instances=builder.Build(materials,new Dictionary<string,FamilySymbol>{{inner.Name,moduleSymbol}}).OfType<FamilyInstance>();
                    foreach(var plane in new FilteredElementCollector(row).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Where(p=>p.Name.StartsWith("BIM_Origine_",StringComparison.Ordinal))) plane.get_Parameter(BuiltInParameter.ELEM_IS_REFERENCE).Set((int)FamilyInstanceReferenceType.StrongReference);
                    CodexFamilyBuilder.ApplyBranding(fm);Commit(tx);
                }
                var loadedRow=row.LoadFamily(host)??throw new InvalidOperationException("Chargement de la rangée refusé.");
                return loadedRow.GetFamilySymbolIds().Select(host.GetElement).OfType<FamilySymbol>().First();
            }
            finally { if(row!=null&&row.IsValidObject)row.Close(false);if(module!=null&&module.IsValidObject)module.Close(false); }
        }
        private static Dictionary<string,FamilyParameter> Materials(Document doc,CodexParametricDesign design)
        {
            var result=new Dictionary<string,FamilyParameter>();
            foreach(var spec in design.Metadata.Materials)
            {
                string name="BIMaestro "+CodexFamilyBuilder.SafeName(spec.Name);
                var material=new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m=>m.Name==name)??(Material)doc.GetElement(Material.Create(doc,name));
                material.Color=new Color(spec.Rgb[0],spec.Rgb[1],spec.Rgb[2]);material.Transparency=spec.Transparency;
                var parameter=doc.FamilyManager.AddParameter(MaterialParameterName(spec.Name),GroupTypeId.Materials,SpecTypeId.Reference.Material,true);
                doc.FamilyManager.Set(parameter,material.Id);result.Add(spec.Name,parameter);
            }
            return result;
        }
        internal static void AssociateMaterials(Document doc,FamilyInstance instance,Dictionary<string,FamilyParameter> materials)
        {
            foreach(var entry in materials)
            {
                var target=instance.LookupParameter(MaterialParameterName(entry.Key));
                if(target!=null)doc.FamilyManager.AssociateElementParameterToFamilyParameter(target,entry.Value);
            }
        }
        private static string MaterialParameterName(string name)
        {
            using(var hash=System.Security.Cryptography.SHA256.Create()) return MaterialPrefix+BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(name))).Replace("-", "").Substring(0,24);
        }
        internal static void CheckRow(FamilyInstance row,ComponentGridSpec grid,Dictionary<string,double> values)
        {
            int columns=grid.Columns(values);
            if(row.LookupParameter(grid.ColumnsParameter)?.AsInteger()!=columns)throw new InvalidOperationException("Le nombre de colonnes ne suit pas l'étendue de la grille.");
            if(columns==0)return;
            using(var geometry=CodexParametricArrayBuilder.MeasurableGeometry(row))
            {
                double volume=CodexParametricArrayBuilder.Volume(geometry)*Math.Pow(304.8,3),expected=columns*grid.Volume;
                if(Math.Abs(volume-expected)>Math.Max(1,expected*0.001))throw new InvalidOperationException("La rangée ne contient pas le volume attendu de modules complets.");
            }
        }
        private static Transaction Begin(Document doc,string name)
        {
            var tx=new Transaction(doc,name);tx.Start();tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new CodexFamilyBuilder.Failures(new List<string>())).SetClearAfterRollback(true));return tx;
        }
        private static void Commit(Transaction tx) { if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Construction du module ou de la rangée annulée par Revit."); }
    }
}
