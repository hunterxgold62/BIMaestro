using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;

internal static class FamilyEditTests
{
    internal static void Run()
    {
        var request = JObject.Parse("{document_key:'family',group_name:'Plan',action:'add',representation_2d:{hide_model_in:[],drawings:[{name:'Ouverture',mode:'symbolic',plane:'xy',offset_mm:0,curves:[{kind:'line',points_mm:[[0,0],[100,0]],radius_mm:0}],rgb:[0,0,0],fill_pattern:'solid'}]}}");
        FamilyRepresentationEdit.Parse(request);
        Reject(request, x => x["representation_2d"]["drawings"][0]["curves"][0]["points_mm"][1][0] = 0.5);
        Reject(request, x => x["representation_2d"]["hide_model_in"] = new JArray("xy"));
        Reject(request, x => x["action"] = "remove");
        Reject(request, x => x["representation_2d"] = JValue.CreateNull());
        Reject(request, x => x["action"] = "unknown");
        Reject(request, x => x["group_name"] = " ");
        Reject(request, x => x.Remove("document_key"));
        request["action"] = "replace"; FamilyRepresentationEdit.Parse(request);
        request["action"] = "remove"; request["representation_2d"] = JValue.CreateNull(); FamilyRepresentationEdit.Parse(request);
        var extrusion = JObject.Parse("{document_key:'family',element_unique_id:'element',start_mm:-10,end_mm:-9}");
        FamilyExtrusionEdit.Parse(extrusion);
        foreach (double end in new[] { -10.0, -9.5, -11.0, double.NaN, double.PositiveInfinity })
        {
            extrusion["end_mm"] = end;
            ExpectReject(() => FamilyExtrusionEdit.Parse(extrusion));
        }
        Console.WriteLine("PASS: family edit contracts, null/remove semantics, 1 mm threshold, finite extrusion limits and preserved visibility");
    }
    private static void Reject(JObject source, Action<JObject> change)
    { var copy = (JObject)source.DeepClone(); change(copy); ExpectReject(() => FamilyRepresentationEdit.Parse(copy)); }
    private static void ExpectReject(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Invalid family edit accepted"); }
}
