using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;

internal static class FamilyEditTests
{
    internal static void Run()
    {
        var configuration = JObject.Parse("{document_key:'family',replace_associations:false,type_names:[],parameters:[{name:'Service de table',mode:'add',kind:'yesno',instance:true,group:'visibility',shared_guid:'',formula:null,value:true}],bindings:[{element_unique_id:'plate',property:'visibility',family_parameter:'Service de table'}]}");
        FamilyConfigurationEdit.Parse(configuration);
        void RejectConfiguration(Action<JObject> change) { var copy = (JObject)configuration.DeepClone(); change(copy); ExpectReject(() => FamilyConfigurationEdit.Parse(copy)); }
        RejectConfiguration(x => x["parameters"][0]["value"] = "true");
        RejectConfiguration(x => x["parameters"][0]["value"] = JValue.CreateNull());
        RejectConfiguration(x => x["parameters"][0]["mode"] = "reuse");
        RejectConfiguration(x => x["parameters"][0]["formula"] = "1 = 1");
        RejectConfiguration(x => x["bindings"][0]["family_parameter"] = "");
        RejectConfiguration(x => ((JArray)x["bindings"]).Add(x["bindings"][0].DeepClone()));
        RejectConfiguration(x => x["parameters"][0]["shared_guid"] = "invalid");
        Console.WriteLine("PASS: existing-family configuration contract, typed defaults, association conflicts and duplicate targets");
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
