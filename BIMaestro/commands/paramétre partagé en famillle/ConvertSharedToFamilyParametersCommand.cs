using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Famille
{
    [Transaction(TransactionMode.Manual)]
    public class ConvertSharedToFamilyParametersCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ConvertSharedToFamilyParameters";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "Aucun document actif.";
                return Result.Failed;
            }

            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show("BIMaestro", "Cette commande doit être lancée dans l'éditeur de familles (.rfa).");
                return Result.Cancelled;
            }

            var familyManager = doc.FamilyManager;
            var sharedParameters = familyManager
                .GetParameters()
                .Where(p => p != null && p.IsShared)
                .ToList();

            if (sharedParameters.Count == 0)
            {
                TaskDialog.Show("BIMaestro", "Aucun paramètre partagé à convertir.");
                return Result.Succeeded;
            }

            int converted = 0;
            var failed = new List<string>();

            using (var tx = new Transaction(doc, "Convertir paramètres partagés en paramètres de famille"))
            {
                tx.Start();

                bool tempTypeCreated;
                EnsureCurrentTypeExists(familyManager, out tempTypeCreated);

                foreach (var parameter in sharedParameters)
                {
                    string error;
                    if (TryReplaceSharedParameter(doc, familyManager, parameter, out error))
                    {
                        converted++;
                    }
                    else
                    {
                        string name = ParameterDisplayName(parameter);
                        failed.Add(string.IsNullOrWhiteSpace(error) ? name : $"{name} — {error}");
                    }
                }

                if (tempTypeCreated)
                    TryCleanupTemporaryType(familyManager);

                tx.Commit();
            }

            string report = $"Paramètres partagés détectés : {sharedParameters.Count}\n" +
                            $"Convertis en paramètres de famille : {converted}\n" +
                            $"Échecs : {failed.Count}";

            if (failed.Count > 0)
                report += "\n\nDétails des échecs :\n- " + string.Join("\n- ", failed);

            TaskDialog.Show("Conversion terminée", report);
            return Result.Succeeded;
        }

        private static string ParameterDisplayName(FamilyParameter parameter)
        {
            string n = parameter?.Definition?.Name;
            if (!string.IsNullOrWhiteSpace(n)) return n;
            return $"<sans nom / id:{parameter?.Id.IntegerValue}>";
        }

        private static void EnsureCurrentTypeExists(FamilyManager familyManager, out bool tempTypeCreated)
        {
            tempTypeCreated = false;
            if (familyManager.CurrentType != null)
                return;

            string baseName = "__BIMaestro_TempType__";
            string candidate = baseName;
            int suffix = 1;

            while (familyManager.Types.Cast<FamilyType>().Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = $"{baseName}_{suffix}";
                suffix++;
            }

            familyManager.NewType(candidate);
            tempTypeCreated = true;
        }

        private static void TryCleanupTemporaryType(FamilyManager familyManager)
        {
            var temp = familyManager.Types
                .Cast<FamilyType>()
                .FirstOrDefault(t => t.Name.StartsWith("__BIMaestro_TempType__", StringComparison.OrdinalIgnoreCase));

            if (temp == null)
                return;

            try
            {
                familyManager.CurrentType = temp;
                familyManager.DeleteCurrentType();
            }
            catch
            {
                // Non bloquant.
            }
        }

        private static bool TryReplaceSharedParameter(Document doc, FamilyManager familyManager, FamilyParameter parameter, out string error)
        {
            error = null;

            if (parameter == null || !parameter.IsShared)
            {
                error = "Paramètre non partagé";
                return false;
            }

            if (parameter.IsReadOnly)
            {
                error = "Paramètre en lecture seule";
                return false;
            }

            var definition = parameter.Definition;
            if (definition == null)
            {
                error = "Définition introuvable";
                return false;
            }

            var invocationErrors = new List<string>();

            // 1) Chemin principal: ReplaceParameter
            if (TryReplaceViaReplaceParameter(doc, familyManager, parameter, definition, invocationErrors))
                return true;

            // 2) Fallback: recréation d'un paramètre famille simple (utile quand ReplaceParameter échoue sur paramètres vides/simples)
            string fallbackError;
            if (TryFallbackByRecreate(doc, familyManager, parameter, definition, out fallbackError))
                return true;

            if (!string.IsNullOrWhiteSpace(fallbackError))
                invocationErrors.Add(fallbackError);

            error = invocationErrors.Count == 0
                ? "Aucune stratégie de conversion n'a abouti"
                : string.Join(" | ", invocationErrors.Distinct().Take(3));

            return false;
        }

        private static bool TryReplaceViaReplaceParameter(Document doc, FamilyManager familyManager, FamilyParameter parameter, Definition definition, List<string> errors)
        {
            var replaceMethods = typeof(FamilyManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "ReplaceParameter")
                .Select(m => new { Method = m, Args = m.GetParameters() })
                .Where(x => x.Args.Length == 4
                            && x.Args[0].ParameterType == typeof(FamilyParameter)
                            && x.Args[1].ParameterType == typeof(string)
                            && x.Args[3].ParameterType == typeof(bool))
                .ToList();

            if (replaceMethods.Count == 0)
            {
                errors.Add("Aucune surcharge ReplaceParameter(FamilyParameter, string, ..., bool) trouvée");
                return false;
            }

            foreach (var item in replaceMethods)
            {
                var groupCandidates = BuildGroupCandidates(item.Args[2].ParameterType, definition).ToList();
                if (groupCandidates.Count == 0)
                    continue;

                foreach (var group in groupCandidates)
                {
                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            item.Method.Invoke(familyManager, new object[]
                            {
                                parameter,
                                definition.Name,
                                group,
                                parameter.IsInstance
                            });

                            sub.Commit();
                            return true;
                        }
                        catch (TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            sub.RollBack();
                            errors.Add(FormatException(tie.InnerException));
                        }
                        catch (Exception ex)
                        {
                            sub.RollBack();
                            errors.Add(FormatException(ex));
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryFallbackByRecreate(Document doc, FamilyManager familyManager, FamilyParameter oldParameter, Definition oldDefinition, out string error)
        {
            error = null;

            if (oldParameter == null || oldDefinition == null)
            {
                error = "Fallback impossible: paramètre/définition invalide";
                return false;
            }

            string originalName = oldDefinition.Name;
            if (string.IsNullOrWhiteSpace(originalName))
            {
                error = "Fallback impossible: nom de paramètre vide";
                return false;
            }

            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    // Important : renommer un paramètre partagé peut échouer selon les familles/versions.
                    // Stratégie robuste : créer d'abord un nouveau paramètre de famille avec un nom temporaire.
                    string tempNewName = BuildUniqueParameterName(familyManager, "__new_family__" + originalName);

                    FamilyParameter newParameter;
                    if (!TryAddFamilyParameter(familyManager, oldDefinition, tempNewName, oldParameter.IsInstance, out newParameter))
                        throw new InvalidOperationException("Impossible de créer le paramètre de famille de remplacement");

                    // Optionnel : copie des valeurs simples sur le type courant (si possible).
                    TryCopySimpleValueOnCurrentType(familyManager, oldParameter, newParameter);

                    // Migration des usages (cotes + formules) pour permettre la suppression du paramètre partagé.
                    var labeledDims = GetLabeledDimensionsReferencing(doc, oldParameter).ToList();
                    foreach (var dim in labeledDims)
                        dim.FamilyLabel = newParameter;

                    var formulasToMove = GetFormulasReferencingParameter(familyManager, oldParameter).ToList();
                    foreach (var item in formulasToMove)
                    {
                        string updated = ReplaceParameterNameToken(item.Formula, originalName, tempNewName);
                        familyManager.SetFormula(item.Parameter, updated);
                    }

                    if (!string.IsNullOrWhiteSpace(oldParameter.Formula))
                    {
                        familyManager.SetFormula(newParameter, oldParameter.Formula);
                    }

                    familyManager.RemoveParameter(oldParameter);
                    familyManager.RenameParameter(newParameter, originalName);

                    // Sécurise les formules si Revit n'a pas automatiquement propagé le renommage.
                    var remainingTempRefs = GetFormulasReferencingName(familyManager, tempNewName).ToList();
                    foreach (var item in remainingTempRefs)
                    {
                        string updated = ReplaceParameterNameToken(item.Formula, tempNewName, originalName);
                        familyManager.SetFormula(item.Parameter, updated);
                    }

                    sub.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    error = "Fallback recréation: " + FormatException(ex);
                    return false;
                }
            }
        }

        private static IEnumerable<Dimension> GetLabeledDimensionsReferencing(Document doc, FamilyParameter parameter)
        {
            foreach (Dimension d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)))
            {
                FamilyParameter labeled = null;
                try { labeled = d.FamilyLabel; } catch { }

                if (labeled != null && labeled.Id == parameter.Id)
                    yield return d;
            }
        }

        private static IEnumerable<(FamilyParameter Parameter, string Formula)> GetFormulasReferencingParameter(FamilyManager familyManager, FamilyParameter parameter)
        {
            string name = parameter?.Definition?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                yield break;

            foreach (var item in GetFormulasReferencingName(familyManager, name))
                yield return item;
        }

        private static IEnumerable<(FamilyParameter Parameter, string Formula)> GetFormulasReferencingName(FamilyManager familyManager, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                yield break;

            foreach (var other in familyManager.GetParameters())
            {
                if (other == null || string.IsNullOrWhiteSpace(other.Formula))
                    continue;

                if (ContainsParameterNameToken(other.Formula, parameterName))
                    yield return (other, other.Formula);
            }
        }

        private static bool ContainsParameterNameToken(string formula, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(formula) || string.IsNullOrWhiteSpace(parameterName))
                return false;

            return Regex.IsMatch(formula, BuildParameterNameTokenPattern(parameterName));
        }

        private static string ReplaceParameterNameToken(string formula, string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(formula) || string.IsNullOrWhiteSpace(oldName))
                return formula;

            return Regex.Replace(formula, BuildParameterNameTokenPattern(oldName), newName ?? string.Empty);
        }

        private static string BuildParameterNameTokenPattern(string parameterName)
        {
            // Évite de remplacer des sous-chaînes involontaires dans les formules.
            return $@"(?<![A-Za-z0-9_]){Regex.Escape(parameterName)}(?![A-Za-z0-9_])";
        }

        private static string BuildUniqueParameterName(FamilyManager familyManager, string baseName)
        {
            var allNames = new HashSet<string>(familyManager.GetParameters().Select(p => p.Definition?.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            if (!allNames.Contains(baseName))
                return baseName;

            int i = 1;
            while (allNames.Contains(baseName + "_" + i)) i++;
            return baseName + "_" + i;
        }

        private static bool TryAddFamilyParameter(FamilyManager familyManager, Definition oldDefinition, string targetName, bool isInstance, out FamilyParameter created)
        {
            created = null;

            var addMethods = typeof(FamilyManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "AddParameter")
                .Select(m => new { Method = m, Args = m.GetParameters() })
                .Where(x => x.Args.Length == 4
                            && x.Args[0].ParameterType == typeof(string)
                            && x.Args[3].ParameterType == typeof(bool))
                .ToList();

            foreach (var m in addMethods)
            {
                var groupCandidates = BuildGroupCandidates(m.Args[1].ParameterType, oldDefinition).ToList();
                var dataTypeCandidates = BuildDataTypeCandidates(m.Args[2].ParameterType, oldDefinition).ToList();

                if (groupCandidates.Count == 0 || dataTypeCandidates.Count == 0)
                    continue;

                foreach (var g in groupCandidates)
                {
                    foreach (var d in dataTypeCandidates)
                    {
                        try
                        {
                            var result = m.Method.Invoke(familyManager, new[] { (object)targetName, g, d, isInstance });
                            created = result as FamilyParameter;
                            if (created != null)
                                return true;
                        }
                        catch
                        {
                            // tenter autre combinaison
                        }
                    }
                }
            }

            return false;
        }

        private static void TryCopySimpleValueOnCurrentType(FamilyManager familyManager, FamilyParameter oldParameter, FamilyParameter newParameter)
        {
            try
            {
                if (familyManager.CurrentType == null || oldParameter == null || newParameter == null)
                    return;

                if (oldParameter.StorageType != newParameter.StorageType)
                    return;

                switch (oldParameter.StorageType)
                {
                    case StorageType.Double:
                        familyManager.Set(newParameter, (double)familyManager.CurrentType.AsDouble(oldParameter));
                        break;
                    case StorageType.Integer:
                        familyManager.Set(newParameter, (int)familyManager.CurrentType.AsInteger(oldParameter));
                        break;
                    case StorageType.String:
                        familyManager.Set(newParameter, familyManager.CurrentType.AsString(oldParameter));
                        break;
                    case StorageType.ElementId:
                        familyManager.Set(newParameter, familyManager.CurrentType.AsElementId(oldParameter));
                        break;
                }
            }
            catch
            {
                // Non bloquant : si la copie de valeur échoue, on garde la conversion structurelle.
            }
        }

        private static IEnumerable<object> BuildDataTypeCandidates(Type dataTypeArgType, Definition definition)
        {
            // API ancienne: ParameterType
            var paramTypeProp = definition.GetType().GetProperty("ParameterType", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (paramTypeProp != null)
            {
                var val = paramTypeProp.GetValue(definition);
                if (val != null && dataTypeArgType.IsInstanceOfType(val))
                    yield return val;
            }

            // API récente: ForgeTypeId (spec)
            var getDataType = definition.GetType().GetMethod("GetDataType", BindingFlags.Public | BindingFlags.Instance);
            if (getDataType != null)
            {
                var val = getDataType.Invoke(definition, null);
                if (val != null && dataTypeArgType.IsInstanceOfType(val))
                    yield return val;
            }
        }

        private static string FormatException(Exception ex)
        {
            if (ex == null) return "Erreur inconnue";
            if (!string.IsNullOrWhiteSpace(ex.Message)) return ex.Message;
            return ex.GetType().Name;
        }

        private static IEnumerable<object> BuildGroupCandidates(Type groupArgumentType, Definition definition)
        {
            if (groupArgumentType == typeof(BuiltInParameterGroup))
            {
                foreach (var candidate in BuildBuiltInGroupCandidates(definition))
                    yield return candidate;
                yield break;
            }

            if (groupArgumentType.Name == nameof(ForgeTypeId))
            {
                foreach (var candidate in BuildForgeGroupCandidates(definition, groupArgumentType))
                    yield return candidate;
            }
        }

        private static IEnumerable<BuiltInParameterGroup> BuildBuiltInGroupCandidates(Definition definition)
        {
            var seen = new HashSet<BuiltInParameterGroup>();

            var parameterGroupProperty = definition.GetType().GetProperty("ParameterGroup", BindingFlags.Public | BindingFlags.Instance);
            if (parameterGroupProperty != null)
            {
                var rawValue = parameterGroupProperty.GetValue(definition);
                if (rawValue is BuiltInParameterGroup fromDefinition && seen.Add(fromDefinition))
                    yield return fromDefinition;
            }

            foreach (BuiltInParameterGroup group in Enum.GetValues(typeof(BuiltInParameterGroup)))
            {
                if (group == BuiltInParameterGroup.INVALID)
                    continue;

                if (seen.Add(group))
                    yield return group;
            }
        }

        private static IEnumerable<object> BuildForgeGroupCandidates(Definition definition, Type forgeType)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var getGroupTypeId = definition.GetType().GetMethod("GetGroupTypeId", BindingFlags.Public | BindingFlags.Instance);
            if (getGroupTypeId != null)
            {
                var value = getGroupTypeId.Invoke(definition, null);
                if (value != null && forgeType.IsInstanceOfType(value) && seen.Add(value.ToString()))
                    yield return value;
            }

            var groupTypeIdType = typeof(ForgeTypeId).Assembly.GetType("Autodesk.Revit.DB.GroupTypeId");
            if (groupTypeIdType != null)
            {
                foreach (var prop in groupTypeIdType.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!forgeType.IsAssignableFrom(prop.PropertyType))
                        continue;

                    var candidate = prop.GetValue(null);
                    if (candidate != null && seen.Add(candidate.ToString()))
                        yield return candidate;
                }
            }
        }
    }
}
