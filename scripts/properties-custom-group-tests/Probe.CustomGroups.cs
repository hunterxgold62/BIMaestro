using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.PropertiesAppearance;

namespace BIMaestro.PropertiesCustomGroupTests
{
 public partial class Probe
 {
  bool CustomGroupCommand(UIApplication app,string command)
  {
   if(!command.StartsWith("custom-",StringComparison.Ordinal))return false;
   autoGroup=false;active=true;PropertiesAppearanceService.CustomLayoutOwnsGrid=null;PropertiesAppearanceService.CustomRowColors=null;
   var context=AppearanceContext.Capture(app);var profile=PropertiesAppearanceService.Saved.Profiles.FirstOrDefault(p=>p.Key==context.CategoryKey)?.Copy()??new AppearanceProfile{Key=context.CategoryKey};
   if(command=="custom-create")
   {
    var owner=NativeGroupExperiment.SelectedOwner(app);bool wall=owner is Wall;
    var ids=wall?new[]{BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,BuiltInParameter.WALL_BASE_OFFSET,BuiltInParameter.WALL_ATTR_ROOM_BOUNDING,BuiltInParameter.WALL_BASE_CONSTRAINT}
     :new[]{BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC,BuiltInParameter.VIEW_DETAIL_LEVEL,BuiltInParameter.VIEW_PHASE,BuiltInParameter.VIEW_PHASE_FILTER};
    var keys=ids.Select(id=>context.Parameters.Single(p=>p.NativeParameterId==(long)id).Key).ToArray();
    profile.Layout.CustomGroups=new List<AppearanceCustomGroup>{new AppearanceCustomGroup{Key="custom:test-first",Name="Mes paramètres",Members=keys.Take(2).ToList()},new AppearanceCustomGroup{Key="custom:test-second",Name="Contrôle métier",Members=keys.Skip(2).ToList()}};
    profile.Layout.Groups=new List<string>{"custom:test-first","custom:test-second"};
    foreach(var g in profile.Layout.CustomGroups)profile.Rules.Add(new AppearanceRule{Kind="group",TargetKey=g.Key,Label=g.Name,Light=new PaletteColors{Text="#155C42",Background="#DCEEE3"}});
   }
   else if(command=="custom-rename")profile.Layout.CustomGroups[0].Name="Mes paramètres renommés";
   else if(command=="custom-empty"){profile.Layout.CustomGroups.Add(new AppearanceCustomGroup{Key="custom:test-empty",Name="Bandeau vide"});profile.Layout.Groups.Insert(0,"custom:test-empty");}
   else if(command=="custom-locked"){var p=context.Parameters.Single(t=>t.NativeParameterId==(long)BuiltInParameter.VIEW_SCALE);profile.Layout.CustomGroups[0].Members.Add(p.Key);}
   else if(command=="custom-transfer")
   {string member=profile.Layout.CustomGroups[0].Members[0];profile.Layout.CustomGroups[0].Members.Remove(member);profile.Layout.CustomGroups[1].Members.Add(member);}
   else if(command=="custom-delete")profile.Layout.CustomGroups.RemoveAt(0);
   else if(command=="custom-clear")profile.Layout=new AppearanceLayout{CustomGroups=new List<AppearanceCustomGroup>()};
   else if(command!="custom-state")throw new InvalidOperationException("Unknown custom group scenario");
   if(command!="custom-state"){PropertiesAppearanceService.Commit(new[]{profile},true);PropertiesAppearanceService.ProcessIdling(app);}
   var grid=NativePropertiesGrid.Find(app);context=AppearanceContext.Capture(app);
   var mappings=context.Parameters.Where(p=>p.NativeParameterId.HasValue).Select(p=>{
    int row=grid.FindParameterRow(p.NativeParameterId.Value);return new{key=p.Key,id=p.NativeParameterId,row,readOnly=p.ReadOnly||row<1||!grid.CanStyle((uint)row),expected=p.NativeName,actual=row>0?AppearancePreferences.Normalize(grid.Value((uint)row,1)):null,custom=row>0?PropertiesPaletteLayout.CustomGroupAt(grid,(uint)row):null};}).ToArray();
   Write("report-"+command+".json",new{command,time=DateTime.UtcNow,pid=System.Diagnostics.Process.GetCurrentProcess().Id,version=app.Application.VersionNumber,status=PropertiesAppearanceService.Status,
    requiresRebuild=PropertiesPaletteLayout.RequiresNativeRebuild,definitions=profile.Layout.CustomGroups,mappings,parameters=Snapshot(app),schema=Schema(app),
    rows=Enumerable.Range(1,(int)grid.RowCount).Select(row=>new{row,label=grid.Value((uint)row,1),header=grid.IsGroupHeader((uint)row),custom=PropertiesPaletteLayout.CustomGroupAt(grid,(uint)row)}).ToArray()});
   return true;
  }
 }
}
