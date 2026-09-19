using Newtonsoft.Json.Linq;
using System;

namespace BIMaestro.Codex
{
    // Requests are validated independently of Revit, before any transaction.
    internal sealed class FamilyRepresentationEdit
    {
        internal string DocumentKey, Group, Action;
        internal FamilyRepresentationSpec Representation;
        internal static JObject Properties() => new JObject {
            ["document_key"] = new JObject { ["type"] = "string", ["maxLength"] = 100 },
            ["group_name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 70 },
            ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("add", "replace", "remove") },
            ["representation_2d"] = FamilyRepresentationSpec.Schema() };
        internal static FamilyRepresentationEdit Parse(JObject args)
        {
            CodexFamilyDesign.Keys(args, "document_key", "group_name", "action", "representation_2d");
            var result = new FamilyRepresentationEdit {
                DocumentKey = CodexFamilyDesign.String(args, "document_key", 100),
                Group = CodexFamilyDesign.String(args, "group_name", 70),
                Action = CodexFamilyDesign.String(args, "action", 10),
                Representation = FamilyRepresentationSpec.Parse(args["representation_2d"]) };
            if (result.Action != "add" && result.Action != "replace" && result.Action != "remove")
                throw new InvalidOperationException("Action de représentation inconnue.");
            if ((result.Action == "remove") != (result.Representation == null))
                throw new InvalidOperationException("representation_2d doit être null uniquement pour remove.");
            if (result.Representation != null && result.Representation.HideModelIn.Length != 0)
                throw new InvalidOperationException("En édition, hide_model_in doit être [] : la visibilité 3D existante est conservée.");
            return result;
        }
    }
    internal sealed class FamilyExtrusionEdit
    {
        internal string DocumentKey, UniqueId;
        internal double Start, End;
        internal static JObject Properties() => new JObject {
            ["document_key"] = new JObject { ["type"] = "string", ["maxLength"] = 100 },
            ["element_unique_id"] = new JObject { ["type"] = "string", ["maxLength"] = 100 },
            ["start_mm"] = new JObject { ["type"] = "number", ["minimum"] = -100000, ["maximum"] = 100000 },
            ["end_mm"] = new JObject { ["type"] = "number", ["minimum"] = -100000, ["maximum"] = 100000 } };
        internal static FamilyExtrusionEdit Parse(JObject args)
        {
            CodexFamilyDesign.Keys(args, "document_key", "element_unique_id", "start_mm", "end_mm");
            var result = new FamilyExtrusionEdit {
                DocumentKey = CodexFamilyDesign.String(args, "document_key", 100),
                UniqueId = CodexFamilyDesign.String(args, "element_unique_id", 100),
                Start = CodexFamilyDesign.Scalar(args["start_mm"], "start_mm", -100000, 100000),
                End = CodexFamilyDesign.Scalar(args["end_mm"], "end_mm", -100000, 100000) };
            if (result.End - result.Start < 1) throw new InvalidOperationException("La profondeur d'extrusion doit être au moins égale à 1 mm.");
            return result;
        }
    }
}
