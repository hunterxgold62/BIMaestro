using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class CodexParameterBuilder
    {
        private readonly Document doc;
        private readonly CodexFamilyParameters spec;
        internal readonly Dictionary<string, FamilyParameter> Parameters = new Dictionary<string, FamilyParameter>();
        internal CodexParameterBuilder(Document document, CodexFamilyParameters registry)
        {
            doc = document; spec = registry;
            var manager = doc.FamilyManager;
            foreach (var p in spec.Parameters)
            {
                var native = string.IsNullOrEmpty(p.SharedGuid) ? GetOrAdd(manager, p.Name, Group(p.Group), DataType(p.Kind), p.Instance) : AddShared(doc, p);
                if (Parameters.Values.Any(v => v.Id == native.Id)) throw new InvalidOperationException("Deux réglages désignent le même paramètre du gabarit : " + p.Name);
                Describe(manager, native, p.Description); Parameters.Add(p.Name, native);
            }
            var initial = spec.Initial;
            foreach (var p in spec.Parameters) Set(Parameters[p.Name], initial[p.Name]);
            foreach (var p in spec.Parameters.Where(p => p.Formula != null)) manager.SetFormula(Parameters[p.Name], NativeFormula(p.Formula.Revit(), Parameters));
            if (!string.Equals(manager.CurrentType.Name, spec.Types[0].Name, StringComparison.Ordinal))
                manager.RenameCurrentType(spec.Types[0].Name);
        }
        internal static FamilyParameter GetOrAdd(FamilyManager manager, string name, ForgeTypeId group, ForgeTypeId kind, bool instance)
        {
            var existing = FindExisting(manager, name);
            if (existing == null) return manager.AddParameter(name, group, kind, instance);
            ValidateExisting(existing, kind, name);
            if (existing.IsShared) throw new InvalidOperationException("Le paramètre « " + existing.Definition.Name + " » est partagé. Réutiliser son shared_guid=" + existing.GUID + " dans family_options, ou choisir un autre nom.");
            SetScope(manager, existing, instance);
            return existing;
        }
        internal static FamilyParameter FindExisting(FamilyManager manager, string name)
        {
            var matches = manager.Parameters.Cast<FamilyParameter>().Where(p => SameName(p.Definition.Name, name)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Plusieurs paramètres correspondent à « " + name + " ». Le gabarit doit être désambiguïsé avant création.");
            return matches.SingleOrDefault();
        }
        internal static bool SameName(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        private static void ValidateExisting(FamilyParameter existing, ForgeTypeId kind, string requested)
        {
            if (existing.Definition.GetDataType() != kind || existing.IsReadOnly || existing.IsReporting || !string.IsNullOrEmpty(existing.Formula))
                throw new InvalidOperationException("Paramètre existant « " + existing.Definition.Name + " » demandé comme « " + requested + " » : type incompatible, lecture seule, cote de rapport ou formule existante. Choisir un nom distinct ; le paramètre du gabarit est conservé.");
        }
        private static void SetScope(FamilyManager manager, FamilyParameter existing, bool instance)
        {
            if (existing.IsInstance != instance)
            {
                try { if (instance) manager.MakeInstance(existing); else manager.MakeType(existing); }
                catch (Exception ex) { throw new InvalidOperationException("Le paramètre « " + existing.Definition.Name + " » ne peut pas devenir " + (instance ? "d'occurrence" : "de type") + ". Conserver sa portée ou choisir un autre nom.", ex); }
            }
        }
        internal static FamilyParameter NewInternal(FamilyManager manager, string name, ForgeTypeId group, ForgeTypeId kind, bool instance)
        {
            string candidate = name; int suffix = 1;
            while (manager.Parameters.Cast<FamilyParameter>().Any(p => SameName(p.Definition.Name, candidate))) candidate = name + "_" + (++suffix);
            return manager.AddParameter(candidate, group, kind, instance);
        }
        internal static void Describe(FamilyManager manager, FamilyParameter parameter, string description)
        {
            if (parameter.Id.IntegerValue > 0 && !parameter.IsShared) manager.SetDescription(parameter, description);
        }
        internal static string NativeFormula(string formula, IDictionary<string, FamilyParameter> bindings) =>
            FamilyFormula.RewriteParameterNames(formula, bindings.ToDictionary(p => p.Key, p => p.Value.Definition.Name));

        internal static ForgeTypeId DataType(string kind)
        {
            switch (kind)
            {
                case "length": return SpecTypeId.Length;
                case "angle": return SpecTypeId.Angle;
                case "integer": return SpecTypeId.Int.Integer;
                case "number": return SpecTypeId.Number;
                case "yesno": return SpecTypeId.Boolean.YesNo;
                case "text": return SpecTypeId.String.Text;
                case "material": return SpecTypeId.Reference.Material;
                default: throw new InvalidOperationException("Type de paramètre non pris en charge.");
            }
        }
        private static ForgeTypeId Group(string name) => name == "visibility" ? GroupTypeId.Visibility : name == "constraints" ? GroupTypeId.Constraints : name == "identity" ? GroupTypeId.IdentityData : name == "data" ? GroupTypeId.Data : name == "materials" ? GroupTypeId.Materials : GroupTypeId.Geometry;
        internal static FamilyParameter AddShared(Document doc, FamilyParameterSpec p)
        {
            var manager = doc.FamilyManager;
            var guid = Guid.Parse(p.SharedGuid);
            var existing = manager.Parameters.Cast<FamilyParameter>().FirstOrDefault(v => v.IsShared && v.GUID == guid);
            var byName = FindExisting(manager, p.Name);
            if (existing != null)
            {
                if (byName != null && byName.Id != existing.Id) throw new InvalidOperationException("Le nom « " + p.Name + " » et son GUID désignent deux paramètres différents.");
                ValidateExisting(existing, DataType(p.Kind), p.Name); SetScope(manager, existing, p.Instance);
                return existing;
            }
            if (byName != null) throw new InvalidOperationException("Le nom « " + p.Name + " » existe avec une autre identité. Ne pas remplacer son GUID : utiliser son identité existante ou choisir un nom distinct.");
            // GUID is explicit in the user's contract; never invent an identity for an existing enterprise parameter.
            string previous = doc.Application.SharedParametersFilename;
            string temporary = Path.Combine(Path.GetTempPath(), "BIMaestro-parameters-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(temporary, ""); doc.Application.SharedParametersFilename = temporary;
                var file = doc.Application.OpenSharedParameterFile() ?? throw new InvalidOperationException("Impossible de préparer les paramètres partagés.");
                var group = file.Groups.Create("BIMaestro");
                using (var options = new ExternalDefinitionCreationOptions(p.Name, DataType(p.Kind)) { GUID = Guid.Parse(p.SharedGuid), Description = p.Description })
                    return doc.FamilyManager.AddParameter((ExternalDefinition)group.Definitions.Create(options), Group(p.Group), p.Instance);
            }
            finally { doc.Application.SharedParametersFilename = previous; if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private void Set(FamilyParameter parameter, FamilyValue value)
        {
            if (value.Kind == "text") doc.FamilyManager.Set(parameter, value.Text);
            else if (value.Kind == "yesno") doc.FamilyManager.Set(parameter, value.Boolean ? 1 : 0);
            else if (parameter.StorageType == StorageType.Integer) doc.FamilyManager.Set(parameter, checked((int)value.Number));
            else doc.FamilyManager.Set(parameter, value.LengthPower == 1 ? value.Number / 304.8 : value.AnglePower == 1 ? value.Number * Math.PI / 180 : value.Number);
        }
        internal void Apply(FamilyCase test)
        {
            var manager = doc.FamilyManager;
            var type = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == test.TypeName);
            manager.CurrentType = type ?? manager.NewType(test.TypeName);
            foreach (var p in spec.Parameters.Where(p => p.Formula == null)) Set(Parameters[p.Name], test.Values[p.Name]);
        }
        internal void Check(FamilyCase test)
        {
            foreach (var p in spec.Parameters)
            {
                var expected = test.Values[p.Name]; var parameter = Parameters[p.Name]; var type = doc.FamilyManager.CurrentType;
                bool valid;
                if (p.Kind == "text") valid = type.AsString(parameter) == expected.Text;
                else if (p.Kind == "yesno") valid = type.AsInteger(parameter) == (expected.Boolean ? 1 : 0);
                else if (p.Kind == "integer") valid = type.AsInteger(parameter) == expected.Number;
                else
                {
                    double actual = type.AsDouble(parameter) ?? double.NaN;
                    if (p.Kind == "length") actual *= 304.8; else if (p.Kind == "angle") actual *= 180 / Math.PI;
                    valid = Math.Abs(actual - expected.Number) <= Math.Max(1e-6, Math.Abs(expected.Number) * 1e-8);
                }
                if (!valid) throw new InvalidOperationException("Le paramètre natif « " + p.Name + " » ne suit pas sa valeur/formule dans " + test.Name);
            }
        }
        internal static void SetDisplay(Document doc, GenericForm element, FamilyDisplaySpec display)
        {
            if (display == null) return;
            using (var visibility = new FamilyElementVisibility(FamilyElementVisibilityType.Model)
            { IsShownInCoarse = display.Coarse, IsShownInMedium = display.Medium, IsShownInFine = display.Fine,
                IsShownInTopBottom = display.Plan, IsShownInFrontBack = display.Front, IsShownInLeftRight = display.Side }) element.SetVisibility(visibility);
            if (!string.IsNullOrWhiteSpace(display.Subcategory))
            {
                var category = doc.OwnerFamily.FamilyCategory;
                var name = CodexFamilyBuilder.SafeName(display.Subcategory);
                element.Subcategory = category.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == name) ?? doc.Settings.Categories.NewSubcategory(category, name);
            }
        }
        internal void Display(GenericForm element, string component)
        {
            var display = spec.Displays.FirstOrDefault(d => d.Component == component);
            SetDisplay(doc, element, display);
            if (!string.IsNullOrEmpty(display?.VisibleParameter))
                doc.FamilyManager.AssociateElementParameterToFamilyParameter(element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), Parameters[display.VisibleParameter]);
        }
        internal void CheckDisplay(GenericForm element, string component, Dictionary<string, double> values)
        {
            var display = spec.Displays.FirstOrDefault(d => d.Component == component);
            if (display == null) return;
            using (var actual = element.GetVisibility())
                if (actual.IsShownInCoarse != display.Coarse || actual.IsShownInMedium != display.Medium || actual.IsShownInFine != display.Fine ||
                    actual.IsShownInTopBottom != display.Plan || actual.IsShownInFrontBack != display.Front || actual.IsShownInLeftRight != display.Side)
                    throw new InvalidOperationException("Niveaux de détail ou vues incorrects : " + component);
            if (!string.IsNullOrEmpty(display.VisibleParameter) && element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM).AsInteger() != (int)values[display.VisibleParameter])
                throw new InvalidOperationException("La visibilité ne suit pas sa condition : " + component);
        }
    }
}
