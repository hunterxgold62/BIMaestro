using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    // The same native family parameters and associations used by the creation engine,
    // applied to selected existing elements without reconstructing the document.
    internal static class CodexFamilyConfiguration
    {
        internal static object Inspect(Document doc, JObject args)
        {
            CodexFamilyDesign.Keys(args, "document_key", "element_unique_id");
            Require(doc, CodexFamilyDesign.String(args, "document_key", 100));
            var element = doc.GetElement(CodexFamilyDesign.String(args, "element_unique_id", 100));
            if (element == null) throw new InvalidOperationException("Élément absent ; relire l'inventaire.");
            return new { unique_id = element.UniqueId, name = element.Name, parameters = ElementParameters(doc, element),
                dimension_label = element is Dimension dimension ? dimension.FamilyLabel?.Definition.Name : null };
        }
        internal static object[] ElementParameters(Document doc, Element element) => element.Parameters.Cast<Parameter>()
            .OrderBy(p => p.Id.ToString(), StringComparer.Ordinal).Select(p => {
                bool associable = doc.FamilyManager.CanElementParameterBeAssociated(p);
                var associated = associable ? doc.FamilyManager.GetAssociatedFamilyParameter(p) : null;
                return (object)new { parameter_id = p.Id.ToString(), name = p.Definition.Name, data_type = p.Definition.GetDataType().TypeId,
                    storage = p.StorageType.ToString(), read_only = p.IsReadOnly, associable,
                    associated_parameter = associated?.Definition.Name,
                    display_value = p.AsValueString() ?? (p.StorageType == StorageType.String ? p.AsString() : null) };
            }).ToArray();
        private static void Require(Document doc, string key)
        {
            if (!doc.IsFamilyDocument || doc.OwnerFamily.UniqueId != key)
                throw new InvalidOperationException("La famille active ne correspond pas à l'inventaire ; relire revit_inspect_family.");
        }
        private static ForgeTypeId DataType(string kind) => kind == "material" ? SpecTypeId.Reference.Material : CodexParameterBuilder.DataType(kind);
        private static ForgeTypeId Group(string name) => name == "visibility" ? GroupTypeId.Visibility : name == "constraints" ? GroupTypeId.Constraints :
            name == "identity" ? GroupTypeId.IdentityData : name == "data" ? GroupTypeId.Data : name == "materials" ? GroupTypeId.Materials : GroupTypeId.Geometry;
        internal static object Apply(Document doc, JObject args, Func<string, string, bool> confirm,
            Func<string, Transaction> transactionFactory, Action<Transaction> commit)
        {
            var request = FamilyConfigurationEdit.Parse(args); Require(doc, request.DocumentKey);
            var manager = doc.FamilyManager;
            // Resolve every target before changing parameters. Never infer target IDs from names.
            var targets = request.Bindings.Select(b => {
                var element = doc.GetElement(b.Element);
                if (element == null) throw new InvalidOperationException("Élément introuvable : " + b.Element);
                if (element.GroupId != ElementId.InvalidElementId) throw new InvalidOperationException("Éditer d'abord le groupe Revit de l'élément " + element.Id + ".");
                if (b.Property == "dimension_label")
                {
                    if (!(element is Dimension d)) throw new InvalidOperationException("dimension_label exige une cote Revit.");
                    return new BindingTarget { Spec = b, Element = element, Dimension = d, Previous = d.FamilyLabel };
                }
                var parameter = b.Property == "visibility" ? element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM) :
                    element.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.ToString() == b.Property);
                if (parameter == null || !manager.CanElementParameterBeAssociated(parameter))
                    throw new InvalidOperationException("Propriété non associable sur l'élément " + element.Id + " : " + b.Property + ". Lire revit_inspect_family_element.");
                return new BindingTarget { Spec = b, Element = element, Parameter = parameter, Previous = manager.GetAssociatedFamilyParameter(parameter) };
            }).ToArray();
            if (targets.GroupBy(t => t.Element.UniqueId + ":" + (t.Parameter?.Id.ToString() ?? "dimension_label")).Any(g => g.Count() > 1))
                throw new InvalidOperationException("La même propriété est ciblée plusieurs fois.");
            foreach (var target in targets)
                if (target.Previous != null && !CodexParameterBuilder.SameName(target.Previous.Definition.Name, target.Spec.FamilyParameter) && !request.ReplaceAssociations)
                    throw new InvalidOperationException("La propriété est déjà associée à « " + target.Previous.Definition.Name + " ». Conserver cette association ou demander explicitement son remplacement.");
            if (!confirm("Configurer la famille existante", request.Parameters.Count + " paramètres, " + targets.Length + " associations et " + request.TypeNames.Length + " types demandés. Même famille, un Ctrl+Z pour annuler le lot."))
                throw new InvalidOperationException("Modification refusée. Ne pas réessayer sans nouvelle demande.");
            using (var group = new TransactionGroup(doc, "Codex — configuration de famille"))
            {
                group.Start();
                using (var transaction = transactionFactory("Codex — paramètres et associations"))
                {
                    var originalType = manager.CurrentType;
                    foreach (string name in request.TypeNames)
                        if (!manager.Types.Cast<FamilyType>().Any(t => t.Name == name)) manager.NewType(name);
                    if (originalType != null) manager.CurrentType = originalType;
                    if (manager.CurrentType == null) manager.NewType("Standard");
                    originalType = manager.CurrentType;
                    var resolved = new Dictionary<FamilyParameterEdit, FamilyParameter>();
                    var created = new HashSet<FamilyParameterEdit>();
                    foreach (var spec in request.Parameters)
                    {
                        var parameter = CodexParameterBuilder.FindExisting(manager, spec.Name);
                        if (spec.Mode == "add" && parameter != null) throw new InvalidOperationException("Paramètre déjà existant : " + spec.Name + ". Utiliser reuse ou update.");
                        if (spec.Mode != "add" && parameter == null) throw new InvalidOperationException("Paramètre absent : " + spec.Name + ". Utiliser add.");
                        if (parameter == null)
                        {
                            parameter = string.IsNullOrEmpty(spec.SharedGuid) ? manager.AddParameter(spec.Name, Group(spec.Group), DataType(spec.Kind), spec.Instance) :
                                CodexParameterBuilder.AddShared(doc, new FamilyParameterSpec { Name = spec.Name, Kind = spec.Kind, Group = spec.Group, Instance = spec.Instance, SharedGuid = spec.SharedGuid, Description = "" });
                            created.Add(spec);
                        }
                        else
                        {
                            if (parameter.Definition.GetDataType() != DataType(spec.Kind) || parameter.IsReporting)
                                throw new InvalidOperationException("Type incompatible ou paramètre de rapport : " + spec.Name);
                            if (spec.SharedGuid.Length != 0 && (!parameter.IsShared || parameter.GUID != Guid.Parse(spec.SharedGuid)))
                                throw new InvalidOperationException("Identité partagée incompatible : " + spec.Name);
                            if (spec.Mode == "reuse" && parameter.IsInstance != spec.Instance)
                                throw new InvalidOperationException("Portée différente : utiliser update pour demander explicitement une conversion type/occurrence.");
                            if (spec.Mode == "update")
                            {
                                if (parameter.IsReadOnly) throw new InvalidOperationException("Paramètre en lecture seule : " + spec.Name);
                                if (parameter.IsInstance != spec.Instance) { if (spec.Instance) manager.MakeInstance(parameter); else manager.MakeType(parameter); }
                            }
                        }
                        if (resolved.Values.Any(p => p.Id == parameter.Id)) throw new InvalidOperationException("Deux définitions ciblent le même paramètre natif : " + spec.Name);
                        resolved.Add(spec, parameter);
                    }
                    // All names now exist, allowing formulas to reference another newly added parameter.
                    foreach (var pair in resolved.Where(p => p.Key.Mode != "reuse" && p.Key.Formula != null))
                        manager.SetFormula(pair.Value, pair.Key.Formula.Length == 0 ? null : pair.Key.Formula);
                    foreach (var pair in resolved.Where(p => p.Key.Mode != "reuse" && p.Key.Value.Type != JTokenType.Null))
                    {
                        if (!string.IsNullOrEmpty(pair.Value.Formula)) throw new InvalidOperationException("Fournir value=null pour le paramètre calculé " + pair.Key.Name + ".");
                        // A new instance switch needs a defined default in EVERY existing type.
                        foreach (var type in created.Contains(pair.Key) ? manager.Types.Cast<FamilyType>().ToArray() : new[] { originalType })
                        { manager.CurrentType = type; Set(doc, pair.Value, pair.Key); }
                    }
                    manager.CurrentType = originalType;
                    foreach (var target in targets)
                    {
                        var parameter = target.Spec.FamilyParameter.Length == 0 ? null : CodexParameterBuilder.FindExisting(manager, target.Spec.FamilyParameter);
                        if (parameter == null && target.Spec.FamilyParameter.Length != 0) throw new InvalidOperationException("Paramètre d'association absent : " + target.Spec.FamilyParameter);
                        if (target.Dimension != null) target.Dimension.FamilyLabel = parameter;
                        else manager.AssociateElementParameterToFamilyParameter(target.Parameter, parameter);
                    }
                    doc.Regenerate(); commit(transaction);
                }
                // Check after commit as native constraint solving can occur during commit.
                foreach (var target in targets)
                {
                    var actual = target.Dimension != null ? target.Dimension.FamilyLabel : manager.GetAssociatedFamilyParameter(target.Parameter);
                    if (!CodexParameterBuilder.SameName(actual?.Definition.Name ?? "", target.Spec.FamilyParameter))
                        throw new InvalidOperationException("Association non conservée après régénération ; lot annulé.");
                }
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Configuration annulée.");
            }
            return new { saved = false, undo = "Ctrl+Z", modified_element_ids = targets.Select(t => t.Element.UniqueId).Distinct().ToArray(),
                bindings = targets.Select(t => new { element_unique_id = t.Element.UniqueId, property = t.Spec.Property, family_parameter = t.Spec.FamilyParameter }).ToArray(),
                state = CodexFamilyTools.Read(doc), note = "Même famille, sans reconstruction. Les paramètres d'occurrence sont réglables séparément sur chaque instance après chargement dans le projet. Le projet n'a pas été rechargé automatiquement." };
        }
        private static void Set(Document doc, FamilyParameter parameter, FamilyParameterEdit spec)
        {
            var manager = doc.FamilyManager;
            if (spec.Kind == "text") manager.Set(parameter, (string)spec.Value);
            else if (spec.Kind == "yesno") manager.Set(parameter, (bool)spec.Value ? 1 : 0);
            else if (spec.Kind == "integer") manager.Set(parameter, (int)spec.Value);
            else if (spec.Kind == "material")
            {
                var material = doc.GetElement((string)spec.Value) as Material;
                if (material == null) throw new InvalidOperationException("Matériau absent : utiliser le unique_id lu dans la famille.");
                manager.Set(parameter, material.Id);
            }
            else manager.Set(parameter, (double)spec.Value * (spec.Kind == "length" ? 1 / 304.8 : spec.Kind == "angle" ? Math.PI / 180 : 1));
        }
        private sealed class BindingTarget
        {
            internal FamilyBindingEdit Spec; internal Element Element; internal Parameter Parameter; internal Dimension Dimension; internal FamilyParameter Previous;
        }
    }
}
