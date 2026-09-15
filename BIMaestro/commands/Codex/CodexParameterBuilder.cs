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
                var native = string.IsNullOrEmpty(p.SharedGuid) ? manager.AddParameter(p.Name, Group(p.Group), DataType(p.Kind), p.Instance) : AddShared(p);
                manager.SetDescription(native, p.Description); Parameters.Add(p.Name, native);
            }
            var initial = spec.Initial;
            foreach (var p in spec.Parameters) Set(Parameters[p.Name], initial[p.Name]);
            foreach (var p in spec.Parameters.Where(p => p.Formula != null)) manager.SetFormula(Parameters[p.Name], p.Formula.Revit());
            if (!string.Equals(manager.CurrentType.Name, spec.Types[0].Name, StringComparison.Ordinal))
                manager.RenameCurrentType(spec.Types[0].Name);
        }
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
                default: throw new InvalidOperationException("Type de paramètre non pris en charge.");
            }
        }
        private static ForgeTypeId Group(string name) => name == "visibility" ? GroupTypeId.Visibility : name == "constraints" ? GroupTypeId.Constraints : name == "identity" ? GroupTypeId.IdentityData : name == "data" ? GroupTypeId.Data : GroupTypeId.Geometry;
        private FamilyParameter AddShared(FamilyParameterSpec p)
        {
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
