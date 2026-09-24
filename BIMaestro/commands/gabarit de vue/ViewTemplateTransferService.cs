using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace BIMaestro.ViewTemplates
{
    internal static class ViewTemplateTransferService
    {
        private const int MaximumWarnings = 30;

        public static ViewTemplatePackage Capture(
            Document document,
            View activeView,
            bool includeFilters,
            string revitVersion)
        {
            View template = activeView.ViewTemplateId != ElementId.InvalidElementId
                ? document.GetElement(activeView.ViewTemplateId) as View
                : null;
            View source = template ?? activeView;

            var package = new ViewTemplatePackage
            {
                RevitVersion = revitVersion ?? string.Empty,
                SourceDocument = document.Title,
                SourceView = activeView.Name,
                SourceTemplate = template?.Name,
                SuggestedName = template?.Name ?? activeView.Name,
                ViewType = source.ViewType.ToString(),
                IncludesFilters = includeFilters
            };

            package.Parameters = CaptureParameters(document, source);
            package.NonControlledTemplateParameters = CaptureNonControlledParameters(document, source);
            package.Categories = CaptureCategories(document, source);
            package.Worksets = CaptureWorksets(document, source);
            if (includeFilters)
            {
                package.Filters = CaptureFilters(document, source);
            }

            return package;
        }

        public static ViewTemplateImportReport Apply(
            Document document,
            View activeView,
            ViewTemplatePackage package,
            bool createTemplate,
            string requestedTemplateName)
        {
            if (package == null || !string.Equals(package.Format, "BIMaestro.ViewTemplate", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Ce fichier n’est pas un gabarit de vue BIMaestro valide.");
            }

            if (package.FormatVersion > ViewTemplatePackage.CurrentFormatVersion)
            {
                throw new InvalidOperationException(
                    "Ce fichier a été créé avec une version plus récente de BIMaestro. Mettez BIMaestro à jour avant de l’importer.");
            }

            var report = new ViewTemplateImportReport();
            using (var transaction = new Transaction(document, "BIMaestro - Importer un gabarit de vue"))
            {
                transaction.Start();
                View target = activeView;

                if (createTemplate)
                {
                    target = FindOrCreateTemplate(document, activeView, requestedTemplateName, report);
                    report.CreatedTemplate = true;
                    report.TargetName = target.Name;
                }
                else
                {
                    if (activeView.ViewTemplateId != ElementId.InvalidElementId)
                    {
                        activeView.ViewTemplateId = ElementId.InvalidElementId;
                    }
                    report.TargetName = activeView.Name;
                }

                ApplyParameters(document, target, package.Parameters, report);
                ApplyCategories(document, target, package.Categories, report);
                ApplyWorksets(document, target, package.Worksets, report);
                if (package.IncludesFilters)
                {
                    ApplyFilters(document, target, package, report);
                }

                if (createTemplate)
                {
                    ApplyNonControlledParameters(document, target, package.NonControlledTemplateParameters, report);
                    if (!activeView.IsValidViewTemplate(target.Id))
                    {
                        throw new InvalidOperationException(
                            "Le gabarit importé n’est pas compatible avec le type de la vue active (" + activeView.ViewType + ").");
                    }
                    activeView.ViewTemplateId = target.Id;
                }

                transaction.Commit();
            }

            return report;
        }

        private static View FindOrCreateTemplate(
            Document document,
            View activeView,
            string requestedName,
            ViewTemplateImportReport report)
        {
            string name = string.IsNullOrWhiteSpace(requestedName)
                ? "Gabarit BIMaestro"
                : requestedName.Trim();
            View existing = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(view => view.IsTemplate &&
                    string.Equals(view.Name, name, StringComparison.CurrentCultureIgnoreCase));

            if (existing != null)
            {
                if (!activeView.IsValidViewTemplate(existing.Id))
                {
                    throw new InvalidOperationException(
                        "Un gabarit nommé « " + name + " » existe déjà, mais il n’est pas compatible avec cette vue.");
                }

                return existing;
            }

            if (!activeView.IsViewValidForTemplateCreation())
            {
                throw new InvalidOperationException(
                    "Revit ne permet pas de créer un gabarit à partir de cette vue. Utilisez l’import direct des graphismes.");
            }

            View created = activeView.CreateViewTemplate();
            created.Name = GetUniqueViewName(document, name);
            if (!string.Equals(created.Name, name, StringComparison.CurrentCulture))
            {
                Warn(report, "Le nom demandé existait déjà ; le gabarit a été nommé « " + created.Name + " ».");
            }
            return created;
        }

        private static string GetUniqueViewName(Document document, string baseName)
        {
            var names = new HashSet<string>(
                new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Select(view => view.Name),
                StringComparer.CurrentCultureIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            int suffix = 2;
            while (names.Contains(baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")")) suffix++;
            return baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static List<ParameterSnapshot> CaptureParameters(Document document, View source)
        {
            var result = new List<ParameterSnapshot>();
            foreach (Parameter parameter in source.Parameters)
            {
                try
                {
                    long id = parameter.Id.GetIdLongValue();
                    if (parameter.IsReadOnly || !parameter.HasValue ||
                        id == (long)BuiltInParameter.VIEW_TEMPLATE ||
                        id == (long)BuiltInParameter.VIEW_NAME ||
                        id == (long)BuiltInParameter.ELEM_TYPE_PARAM)
                    {
                        continue;
                    }

                    var snapshot = new ParameterSnapshot
                    {
                        Parameter = CaptureParameterReference(document, parameter.Id, parameter.Definition?.Name, parameter),
                        StorageType = parameter.StorageType.ToString(),
                        HasValue = parameter.HasValue
                    };
                    switch (parameter.StorageType)
                    {
                        case StorageType.Integer:
                            snapshot.IntegerValue = parameter.AsInteger();
                            break;
                        case StorageType.Double:
                            snapshot.DoubleValue = parameter.AsDouble();
                            break;
                        case StorageType.String:
                            snapshot.StringValue = parameter.AsString();
                            break;
                        case StorageType.ElementId:
                            snapshot.ElementValue = CaptureElementReference(document, parameter.AsElementId());
                            break;
                        default:
                            continue;
                    }
                    result.Add(snapshot);
                }
                catch
                {
                    // Certains paramètres internes ne peuvent pas être lus selon le type de vue.
                }
            }
            return result;
        }

        private static List<ParameterReference> CaptureNonControlledParameters(Document document, View source)
        {
            if (!source.IsTemplate) return new List<ParameterReference>();
            try
            {
                return source.GetNonControlledTemplateParameterIds()
                    .Select(id => CaptureParameterReference(document, id, null, null))
                    .Where(reference => reference != null)
                    .ToList();
            }
            catch
            {
                return new List<ParameterReference>();
            }
        }

        private static List<CategorySnapshot> CaptureCategories(Document document, View source)
        {
            var result = new List<CategorySnapshot>();
            foreach (Category category in EnumerateCategories(document.Settings.Categories))
            {
                try
                {
                    if (!source.IsCategoryOverridable(category.Id) && !source.CanCategoryBeHidden(category.Id)) continue;
                    result.Add(new CategorySnapshot
                    {
                        Category = CaptureCategoryReference(category),
                        Hidden = source.CanCategoryBeHidden(category.Id) && source.GetCategoryHidden(category.Id),
                        Overrides = source.IsCategoryOverridable(category.Id)
                            ? CaptureOverrides(document, source.GetCategoryOverrides(category.Id))
                            : null
                    });
                }
                catch
                {
                    // Les catégories internes ne sont pas toutes disponibles dans chaque type de vue.
                }
            }
            return result;
        }

        private static IEnumerable<Category> EnumerateCategories(Categories categories)
        {
            foreach (Category category in categories)
            {
                yield return category;
                foreach (Category child in EnumerateSubCategories(category)) yield return child;
            }
        }

        private static IEnumerable<Category> EnumerateSubCategories(Category parent)
        {
            foreach (Category child in parent.SubCategories)
            {
                yield return child;
                foreach (Category nested in EnumerateSubCategories(child)) yield return nested;
            }
        }

        private static List<WorksetSnapshot> CaptureWorksets(Document document, View source)
        {
            if (!document.IsWorkshared) return new List<WorksetSnapshot>();
            var result = new List<WorksetSnapshot>();
            foreach (Workset workset in new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset))
            {
                try
                {
                    result.Add(new WorksetSnapshot
                    {
                        Name = workset.Name,
                        Visibility = source.GetWorksetVisibility(workset.Id).ToString()
                    });
                }
                catch
                {
                    // Non pris en charge par ce type de vue.
                }
            }
            return result;
        }

        private static List<ViewFilterSnapshot> CaptureFilters(Document document, View source)
        {
            var result = new List<ViewFilterSnapshot>();
            IEnumerable<ElementId> orderedIds;
            try { orderedIds = source.GetOrderedFilters(); }
            catch { orderedIds = source.GetFilters(); }

            foreach (ElementId id in orderedIds)
            {
                Element element = document.GetElement(id);
                if (element == null) continue;
                var snapshot = new ViewFilterSnapshot
                {
                    Name = element.Name,
                    Visible = SafeGet(() => source.GetFilterVisibility(id), true),
                    Enabled = SafeGet(() => source.GetIsFilterEnabled(id), true),
                    Overrides = CaptureOverrides(document, source.GetFilterOverrides(id))
                };

                if (element is ParameterFilterElement parameterFilter)
                {
                    snapshot.Kind = "Parameter";
                    snapshot.Categories = parameterFilter.GetCategories()
                        .Select(categoryId => Category.GetCategory(document, categoryId))
                        .Where(category => category != null)
                        .Select(CaptureCategoryReference)
                        .ToList();
                    snapshot.RuleTree = CaptureFilterNode(document, parameterFilter.GetElementFilter());
                }
                else if (element is SelectionFilterElement selectionFilter)
                {
                    snapshot.Kind = "Selection";
                    snapshot.SelectedElementUniqueIds = selectionFilter.GetElementIds()
                        .Select(document.GetElement)
                        .Where(selected => selected != null)
                        .Select(selected => selected.UniqueId)
                        .ToList();
                }
                else
                {
                    continue;
                }
                result.Add(snapshot);
            }
            return result;
        }

        private static FilterNodeSnapshot CaptureFilterNode(Document document, ElementFilter filter)
        {
            if (filter is LogicalAndFilter andFilter)
            {
                return new FilterNodeSnapshot
                {
                    Kind = "And",
                    Inverted = andFilter.Inverted,
                    Children = andFilter.GetFilters().Select(child => CaptureFilterNode(document, child)).ToList()
                };
            }
            if (filter is LogicalOrFilter orFilter)
            {
                return new FilterNodeSnapshot
                {
                    Kind = "Or",
                    Inverted = orFilter.Inverted,
                    Children = orFilter.GetFilters().Select(child => CaptureFilterNode(document, child)).ToList()
                };
            }
            if (filter is ElementParameterFilter parameterFilter)
            {
                return new FilterNodeSnapshot
                {
                    Kind = "Parameters",
                    Inverted = parameterFilter.Inverted,
                    Rules = parameterFilter.GetRules().Select(rule => CaptureRule(document, rule)).ToList()
                };
            }

            return new FilterNodeSnapshot { Kind = "Unsupported:" + filter.GetType().Name };
        }

        private static FilterRuleSnapshot CaptureRule(Document document, FilterRule rule)
        {
            if (rule is FilterInverseRule inverse)
            {
                return new FilterRuleSnapshot { Kind = "Inverse", InnerRule = CaptureRule(document, inverse.GetInnerRule()) };
            }

            var snapshot = new FilterRuleSnapshot
            {
                Parameter = CaptureParameterReference(document, rule.GetRuleParameter(), null, null)
            };
            if (rule is FilterStringRule stringRule)
            {
                snapshot.Kind = "String";
                snapshot.Evaluator = stringRule.GetEvaluator().GetType().Name;
                snapshot.StringValue = stringRule.RuleString;
            }
            else if (rule is FilterDoubleRule doubleRule)
            {
                snapshot.Kind = "Double";
                snapshot.Evaluator = doubleRule.GetEvaluator().GetType().Name;
                snapshot.DoubleValue = doubleRule.RuleValue;
                snapshot.Epsilon = doubleRule.Epsilon;
            }
            else if (rule is FilterIntegerRule integerRule)
            {
                snapshot.Kind = "Integer";
                snapshot.Evaluator = integerRule.GetEvaluator().GetType().Name;
                snapshot.IntegerValue = integerRule.RuleValue;
            }
            else if (rule is FilterElementIdRule elementRule)
            {
                snapshot.Kind = "ElementId";
                snapshot.Evaluator = elementRule.GetEvaluator().GetType().Name;
                snapshot.ElementValue = CaptureElementReference(document, elementRule.RuleValue);
            }
            else if (rule.GetType().Name == "FilterHasValueRule")
            {
                snapshot.Kind = "HasValue";
            }
            else if (rule.GetType().Name == "FilterHasNoValueRule")
            {
                snapshot.Kind = "HasNoValue";
            }
            else
            {
                snapshot.Kind = "Unsupported";
                snapshot.UnsupportedType = rule.GetType().Name;
            }
            return snapshot;
        }

        private static GraphicOverrideSnapshot CaptureOverrides(Document document, OverrideGraphicSettings settings)
        {
            if (settings == null) return null;
            return new GraphicOverrideSnapshot
            {
                ProjectionLineColor = CaptureColor(settings.ProjectionLineColor),
                ProjectionLinePattern = CaptureElementReference(document, settings.ProjectionLinePatternId),
                ProjectionLineWeight = settings.ProjectionLineWeight,
                CutLineColor = CaptureColor(settings.CutLineColor),
                CutLinePattern = CaptureElementReference(document, settings.CutLinePatternId),
                CutLineWeight = settings.CutLineWeight,
                SurfaceForegroundPatternColor = CaptureColor(settings.SurfaceForegroundPatternColor),
                SurfaceForegroundPattern = CaptureElementReference(document, settings.SurfaceForegroundPatternId),
                SurfaceForegroundPatternVisible = settings.IsSurfaceForegroundPatternVisible,
                SurfaceBackgroundPatternColor = CaptureColor(settings.SurfaceBackgroundPatternColor),
                SurfaceBackgroundPattern = CaptureElementReference(document, settings.SurfaceBackgroundPatternId),
                SurfaceBackgroundPatternVisible = settings.IsSurfaceBackgroundPatternVisible,
                CutForegroundPatternColor = CaptureColor(settings.CutForegroundPatternColor),
                CutForegroundPattern = CaptureElementReference(document, settings.CutForegroundPatternId),
                CutForegroundPatternVisible = settings.IsCutForegroundPatternVisible,
                CutBackgroundPatternColor = CaptureColor(settings.CutBackgroundPatternColor),
                CutBackgroundPattern = CaptureElementReference(document, settings.CutBackgroundPatternId),
                CutBackgroundPatternVisible = settings.IsCutBackgroundPatternVisible,
                Transparency = settings.Transparency,
                Halftone = settings.Halftone,
                DetailLevel = settings.DetailLevel.ToString()
            };
        }

        private static ColorSnapshot CaptureColor(Color color)
        {
            if (color == null || !color.IsValid) return null;
            return new ColorSnapshot { Red = color.Red, Green = color.Green, Blue = color.Blue };
        }

        private static CategoryReference CaptureCategoryReference(Category category)
        {
            long value = category.Id.GetIdLongValue();
            return new CategoryReference
            {
                BuiltInId = value < 0 ? value : (long?)null,
                Path = GetCategoryPath(category)
            };
        }

        private static string GetCategoryPath(Category category)
        {
            var names = new List<string>();
            Category current = category;
            while (current != null)
            {
                names.Add(current.Name);
                current = current.Parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static ParameterReference CaptureParameterReference(
            Document document,
            ElementId parameterId,
            string fallbackName,
            Parameter parameter)
        {
            if (parameterId == null) return null;
            long value = parameterId.GetIdLongValue();
            var reference = new ParameterReference
            {
                BuiltInId = value < 0 ? value : (long?)null,
                Name = fallbackName
            };
            try
            {
                if (parameter != null && parameter.IsShared)
                {
                    reference.SharedGuid = parameter.GUID.ToString("D");
                }
                else if (document.GetElement(parameterId) is SharedParameterElement shared)
                {
                    reference.SharedGuid = shared.GuidValue.ToString("D");
                    reference.Name = shared.Name;
                }
                else if (document.GetElement(parameterId) is ParameterElement parameterElement)
                {
                    reference.Name = parameterElement.Name;
                }
            }
            catch
            {
                // Le nom de repli reste suffisant pour les paramètres non partagés.
            }
            return reference;
        }

        private static ElementReference CaptureElementReference(Document document, ElementId id)
        {
            if (id == null) return null;
            long value = id.GetIdLongValue();
            if (value < 0)
            {
                return new ElementReference { BuiltInId = value };
            }
            Element element = document.GetElement(id);
            if (element == null) return null;
            string auxiliary = null;
            if (element is FillPatternElement fill)
            {
                auxiliary = fill.GetFillPattern().Target.ToString();
            }
            return new ElementReference
            {
                UniqueId = element.UniqueId,
                Name = SafeGetElementName(element),
                ClassName = element.GetType().Name,
                Auxiliary = auxiliary
            };
        }

        private static void ApplyParameters(
            Document document,
            View target,
            IEnumerable<ParameterSnapshot> snapshots,
            ViewTemplateImportReport report)
        {
            foreach (ParameterSnapshot snapshot in snapshots ?? Enumerable.Empty<ParameterSnapshot>())
            {
                try
                {
                    Parameter parameter = ResolveParameter(document, target, snapshot.Parameter);
                    if (parameter == null || parameter.IsReadOnly) continue;
                    bool changed = false;
                    switch (snapshot.StorageType)
                    {
                        case "Integer" when snapshot.IntegerValue.HasValue:
                            changed = parameter.Set(snapshot.IntegerValue.Value);
                            break;
                        case "Double" when snapshot.DoubleValue.HasValue:
                            changed = parameter.Set(snapshot.DoubleValue.Value);
                            break;
                        case "String":
                            changed = parameter.Set(snapshot.StringValue);
                            break;
                        case "ElementId":
                            ElementId value = ResolveElementReference(document, snapshot.ElementValue);
                            if (value != null) changed = parameter.Set(value);
                            break;
                    }
                    if (changed) report.ParametersApplied++;
                }
                catch (Exception ex)
                {
                    Warn(report, "Paramètre « " + (snapshot.Parameter?.Name ?? "inconnu") + " » ignoré : " + ex.Message);
                }
            }
        }

        private static void ApplyNonControlledParameters(
            Document document,
            View target,
            IEnumerable<ParameterReference> references,
            ViewTemplateImportReport report)
        {
            if (!target.IsTemplate) return;
            try
            {
                var ids = (references ?? Enumerable.Empty<ParameterReference>())
                    .Select(reference => ResolveParameterId(document, reference))
                    .Where(id => id != null && id != ElementId.InvalidElementId)
                    .ToList();
                target.SetNonControlledTemplateParameterIds(ids);
            }
            catch (Exception ex)
            {
                Warn(report, "Paramètres d’inclusion du gabarit non restaurés : " + ex.Message);
            }
        }

        private static void ApplyCategories(
            Document document,
            View target,
            IEnumerable<CategorySnapshot> snapshots,
            ViewTemplateImportReport report)
        {
            foreach (CategorySnapshot snapshot in snapshots ?? Enumerable.Empty<CategorySnapshot>())
            {
                Category category = ResolveCategory(document, snapshot.Category);
                if (category == null) continue;
                bool applied = false;
                try
                {
                    if (target.CanCategoryBeHidden(category.Id))
                    {
                        target.SetCategoryHidden(category.Id, snapshot.Hidden);
                        applied = true;
                    }
                    if (snapshot.Overrides != null && target.IsCategoryOverridable(category.Id))
                    {
                        target.SetCategoryOverrides(category.Id, BuildOverrides(document, snapshot.Overrides));
                        applied = true;
                    }
                    if (applied) report.CategoriesApplied++;
                }
                catch (Exception ex)
                {
                    Warn(report, "Catégorie « " + snapshot.Category?.Path + " » ignorée : " + ex.Message);
                }
            }
        }

        private static void ApplyWorksets(
            Document document,
            View target,
            IEnumerable<WorksetSnapshot> snapshots,
            ViewTemplateImportReport report)
        {
            if (!document.IsWorkshared) return;
            var targetWorksets = new FilteredWorksetCollector(document)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .ToDictionary(workset => workset.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (WorksetSnapshot snapshot in snapshots ?? Enumerable.Empty<WorksetSnapshot>())
            {
                if (!targetWorksets.TryGetValue(snapshot.Name ?? string.Empty, out Workset workset)) continue;
                if (!Enum.TryParse(snapshot.Visibility, out WorksetVisibility visibility)) continue;
                try
                {
                    target.SetWorksetVisibility(workset.Id, visibility);
                    report.WorksetsApplied++;
                }
                catch (Exception ex)
                {
                    Warn(report, "Sous-projet « " + snapshot.Name + " » ignoré : " + ex.Message);
                }
            }
        }

        private static void ApplyFilters(
            Document document,
            View target,
            ViewTemplatePackage package,
            ViewTemplateImportReport report)
        {
            foreach (ElementId existingId in target.GetFilters().ToList())
            {
                try { target.RemoveFilter(existingId); }
                catch { }
            }

            foreach (ViewFilterSnapshot snapshot in package.Filters ?? new List<ViewFilterSnapshot>())
            {
                try
                {
                    Element filter = CreateOrUpdatePortableFilter(document, package, snapshot, report);
                    if (filter == null) continue;
                    target.AddFilter(filter.Id);
                    target.SetFilterVisibility(filter.Id, snapshot.Visible);
                    target.SetFilterOverrides(filter.Id, BuildOverrides(document, snapshot.Overrides));
                    try { target.SetIsFilterEnabled(filter.Id, snapshot.Enabled); }
                    catch { }
                    report.FiltersApplied++;
                }
                catch (Exception ex)
                {
                    Warn(report, "Filtre « " + snapshot.Name + " » ignoré : " + ex.Message);
                }
            }
        }

        private static Element CreateOrUpdatePortableFilter(
            Document document,
            ViewTemplatePackage package,
            ViewFilterSnapshot snapshot,
            ViewTemplateImportReport report)
        {
            string managedName = BuildManagedFilterName(snapshot.Name, package.SuggestedName);
            if (string.Equals(snapshot.Kind, "Parameter", StringComparison.Ordinal))
            {
                List<ElementId> categories = snapshot.Categories
                    .Select(reference => ResolveCategory(document, reference)?.Id)
                    .Where(id => id != null)
                    .Distinct()
                    .ToList();
                ElementFilter ruleTree = BuildFilterNode(document, snapshot.RuleTree, report);
                if (categories.Count == 0 || ruleTree == null)
                {
                    Warn(report, "Filtre « " + snapshot.Name + " » non recréé : catégories ou règles indisponibles.");
                    return null;
                }

                ParameterFilterElement existing = new FilteredElementCollector(document)
                    .OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                    .FirstOrDefault(filter => string.Equals(filter.Name, managedName, StringComparison.CurrentCultureIgnoreCase));
                if (existing == null)
                {
                    return ParameterFilterElement.Create(document, managedName, categories, ruleTree);
                }
                existing.SetCategories(categories);
                existing.SetElementFilter(ruleTree);
                return existing;
            }

            if (string.Equals(snapshot.Kind, "Selection", StringComparison.Ordinal))
            {
                SelectionFilterElement existing = new FilteredElementCollector(document)
                    .OfClass(typeof(SelectionFilterElement)).Cast<SelectionFilterElement>()
                    .FirstOrDefault(filter => string.Equals(filter.Name, managedName, StringComparison.CurrentCultureIgnoreCase));
                if (existing == null) existing = SelectionFilterElement.Create(document, managedName);
                var ids = snapshot.SelectedElementUniqueIds
                    .Select(uniqueId => SafeGet(() => document.GetElement(uniqueId)?.Id, null))
                    .Where(id => id != null)
                    .ToList();
                existing.SetElementIds(ids);
                if (ids.Count == 0 && snapshot.SelectedElementUniqueIds.Count > 0)
                {
                    Warn(report, "Le filtre de sélection « " + snapshot.Name + " » est vide dans ce projet.");
                }
                return existing;
            }
            return null;
        }

        private static string BuildManagedFilterName(string filterName, string packageName)
        {
            string source = string.IsNullOrWhiteSpace(packageName) ? "Import" : packageName.Trim();
            string name = (filterName ?? "Filtre") + " [BIMaestro - " + source + "]";
            return name.Length <= 240 ? name : name.Substring(0, 240);
        }

        private static ElementFilter BuildFilterNode(
            Document document,
            FilterNodeSnapshot snapshot,
            ViewTemplateImportReport report)
        {
            if (snapshot == null) return null;
            if (snapshot.Kind == "And" || snapshot.Kind == "Or")
            {
                var children = snapshot.Children
                    .Select(child => BuildFilterNode(document, child, report))
                    .Where(child => child != null)
                    .ToList();
                if (children.Count != snapshot.Children.Count || children.Count == 0) return null;
                ElementFilter result = children.Count == 1
                    ? children[0]
                    : snapshot.Kind == "And"
                        ? (ElementFilter)new LogicalAndFilter(children)
                        : new LogicalOrFilter(children);
                if (!snapshot.Inverted || children.Count == 1) return result;
                // Les filtres logiques Revit exportés sont rarement inversés. Une inversion
                // non reconstructible est refusée pour éviter de changer silencieusement le sens.
                Warn(report, "Une inversion logique de filtre n’est pas portable dans cette version de Revit.");
                return null;
            }
            if (snapshot.Kind == "Parameters")
            {
                var rules = snapshot.Rules.Select(rule => BuildRule(document, rule)).ToList();
                if (rules.Any(rule => rule == null) || rules.Count == 0) return null;
                return new ElementParameterFilter(rules, snapshot.Inverted);
            }
            return null;
        }

        private static FilterRule BuildRule(Document document, FilterRuleSnapshot snapshot)
        {
            if (snapshot == null) return null;
            if (snapshot.Kind == "Inverse")
            {
                FilterRule inner = BuildRule(document, snapshot.InnerRule);
                return inner == null ? null : new FilterInverseRule(inner);
            }

            ElementId parameterId = ResolveParameterId(document, snapshot.Parameter);
            if (parameterId == null) return null;
            switch (snapshot.Kind)
            {
                case "HasValue":
                    return InvokeRuleFactory("CreateHasValueParameterRule", parameterId);
                case "HasNoValue":
                    return InvokeRuleFactory("CreateHasNoValueParameterRule", parameterId);
                case "String":
                    return InvokeRuleFactory(EvaluatorToFactory(snapshot.Evaluator), parameterId, snapshot.StringValue ?? string.Empty);
                case "Integer" when snapshot.IntegerValue.HasValue:
                    return InvokeRuleFactory(EvaluatorToFactory(snapshot.Evaluator), parameterId, snapshot.IntegerValue.Value);
                case "Double" when snapshot.DoubleValue.HasValue:
                    return InvokeRuleFactory(
                        EvaluatorToFactory(snapshot.Evaluator),
                        parameterId,
                        snapshot.DoubleValue.Value,
                        snapshot.Epsilon ?? 1e-9);
                case "ElementId":
                    ElementId value = ResolveElementReference(document, snapshot.ElementValue);
                    return value == null ? null : InvokeRuleFactory(EvaluatorToFactory(snapshot.Evaluator), parameterId, value);
                default:
                    return null;
            }
        }

        private static string EvaluatorToFactory(string evaluator)
        {
            switch (evaluator)
            {
                case "FilterStringBeginsWith": return "CreateBeginsWithRule";
                case "FilterStringContains": return "CreateContainsRule";
                case "FilterStringEndsWith": return "CreateEndsWithRule";
                case "FilterNumericGreater":
                case "FilterStringGreater": return "CreateGreaterRule";
                case "FilterNumericGreaterOrEqual":
                case "FilterStringGreaterOrEqual": return "CreateGreaterOrEqualRule";
                case "FilterNumericLess":
                case "FilterStringLess": return "CreateLessRule";
                case "FilterNumericLessOrEqual":
                case "FilterStringLessOrEqual": return "CreateLessOrEqualRule";
                case "FilterNumericEquals":
                case "FilterStringEquals": return "CreateEqualsRule";
                default: return null;
            }
        }

        private static FilterRule InvokeRuleFactory(string methodName, params object[] arguments)
        {
            if (string.IsNullOrWhiteSpace(methodName)) return null;
            foreach (MethodInfo method in typeof(ParameterFilterRuleFactory).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(candidate => candidate.Name == methodName && candidate.GetParameters().Length == arguments.Length))
            {
                ParameterInfo[] parameters = method.GetParameters();
                bool compatible = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (arguments[index] != null && !parameters[index].ParameterType.IsInstanceOfType(arguments[index]))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (compatible) return method.Invoke(null, arguments) as FilterRule;
            }
            return null;
        }

        private static OverrideGraphicSettings BuildOverrides(Document document, GraphicOverrideSnapshot snapshot)
        {
            var settings = new OverrideGraphicSettings();
            if (snapshot == null) return settings;

            SetColor(snapshot.ProjectionLineColor, settings.SetProjectionLineColor);
            SetElementReference(document, snapshot.ProjectionLinePattern, settings.SetProjectionLinePatternId);
            if (snapshot.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber)
                settings.SetProjectionLineWeight(snapshot.ProjectionLineWeight);
            SetColor(snapshot.CutLineColor, settings.SetCutLineColor);
            SetElementReference(document, snapshot.CutLinePattern, settings.SetCutLinePatternId);
            if (snapshot.CutLineWeight != OverrideGraphicSettings.InvalidPenNumber)
                settings.SetCutLineWeight(snapshot.CutLineWeight);

            SetColor(snapshot.SurfaceForegroundPatternColor, settings.SetSurfaceForegroundPatternColor);
            SetElementReference(document, snapshot.SurfaceForegroundPattern, settings.SetSurfaceForegroundPatternId);
            settings.SetSurfaceForegroundPatternVisible(snapshot.SurfaceForegroundPatternVisible);
            SetColor(snapshot.SurfaceBackgroundPatternColor, settings.SetSurfaceBackgroundPatternColor);
            SetElementReference(document, snapshot.SurfaceBackgroundPattern, settings.SetSurfaceBackgroundPatternId);
            settings.SetSurfaceBackgroundPatternVisible(snapshot.SurfaceBackgroundPatternVisible);
            SetColor(snapshot.CutForegroundPatternColor, settings.SetCutForegroundPatternColor);
            SetElementReference(document, snapshot.CutForegroundPattern, settings.SetCutForegroundPatternId);
            settings.SetCutForegroundPatternVisible(snapshot.CutForegroundPatternVisible);
            SetColor(snapshot.CutBackgroundPatternColor, settings.SetCutBackgroundPatternColor);
            SetElementReference(document, snapshot.CutBackgroundPattern, settings.SetCutBackgroundPatternId);
            settings.SetCutBackgroundPatternVisible(snapshot.CutBackgroundPatternVisible);
            settings.SetSurfaceTransparency(Math.Max(0, Math.Min(100, snapshot.Transparency)));
            settings.SetHalftone(snapshot.Halftone);
            if (Enum.TryParse(snapshot.DetailLevel, out ViewDetailLevel detailLevel) &&
                detailLevel != ViewDetailLevel.Undefined)
            {
                settings.SetDetailLevel(detailLevel);
            }
            return settings;
        }

        private static void SetColor(ColorSnapshot snapshot, Func<Color, OverrideGraphicSettings> setter)
        {
            if (snapshot != null) setter(new Color(snapshot.Red, snapshot.Green, snapshot.Blue));
        }

        private static void SetElementReference(
            Document document,
            ElementReference reference,
            Func<ElementId, OverrideGraphicSettings> setter)
        {
            ElementId id = ResolveElementReference(document, reference);
            if (id != null) setter(id);
        }

        private static Parameter ResolveParameter(Document document, Element element, ParameterReference reference)
        {
            if (reference == null) return null;
            if (reference.BuiltInId.HasValue && reference.BuiltInId.Value >= int.MinValue && reference.BuiltInId.Value <= int.MaxValue)
            {
                try { return element.get_Parameter((BuiltInParameter)(int)reference.BuiltInId.Value); }
                catch { }
            }
            if (Guid.TryParse(reference.SharedGuid, out Guid guid))
            {
                try { return element.get_Parameter(guid); }
                catch { }
            }
            return string.IsNullOrWhiteSpace(reference.Name) ? null : element.LookupParameter(reference.Name);
        }

        private static ElementId ResolveParameterId(Document document, ParameterReference reference)
        {
            if (reference == null) return null;
            if (reference.BuiltInId.HasValue) return ElementIdExtensions.CreateElementId(reference.BuiltInId.Value);
            Guid sharedGuid;
            bool hasGuid = Guid.TryParse(reference.SharedGuid, out sharedGuid);
            ParameterElement match = new FilteredElementCollector(document)
                .OfClass(typeof(ParameterElement))
                .Cast<ParameterElement>()
                .FirstOrDefault(parameter =>
                    (hasGuid && parameter is SharedParameterElement shared && shared.GuidValue == sharedGuid) ||
                    (!string.IsNullOrWhiteSpace(reference.Name) &&
                     string.Equals(parameter.Name, reference.Name, StringComparison.CurrentCultureIgnoreCase)));
            return match?.Id;
        }

        private static Category ResolveCategory(Document document, CategoryReference reference)
        {
            if (reference == null) return null;
            if (reference.BuiltInId.HasValue)
            {
                try
                {
                    Category builtIn = Category.GetCategory(document, ElementIdExtensions.CreateElementId(reference.BuiltInId.Value));
                    if (builtIn != null) return builtIn;
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(reference.Path)) return null;
            return EnumerateCategories(document.Settings.Categories)
                .FirstOrDefault(category => string.Equals(
                    GetCategoryPath(category), reference.Path, StringComparison.CurrentCultureIgnoreCase));
        }

        private static ElementId ResolveElementReference(Document document, ElementReference reference)
        {
            if (reference == null) return null;
            if (reference.BuiltInId.HasValue) return ElementIdExtensions.CreateElementId(reference.BuiltInId.Value);
            if (!string.IsNullOrWhiteSpace(reference.UniqueId))
            {
                Element sameProject = SafeGet(() => document.GetElement(reference.UniqueId), null);
                if (sameProject != null && ElementMatches(sameProject, reference)) return sameProject.Id;
            }

            IEnumerable<Element> candidates;
            switch (reference.ClassName)
            {
                case nameof(FillPatternElement):
                    candidates = new FilteredElementCollector(document).OfClass(typeof(FillPatternElement)).ToElements();
                    break;
                case nameof(LinePatternElement):
                    candidates = new FilteredElementCollector(document).OfClass(typeof(LinePatternElement)).ToElements();
                    break;
                case nameof(Level):
                    candidates = new FilteredElementCollector(document).OfClass(typeof(Level)).ToElements();
                    break;
                case nameof(Phase):
                    candidates = new FilteredElementCollector(document).OfClass(typeof(Phase)).ToElements();
                    break;
                case nameof(Material):
                    candidates = new FilteredElementCollector(document).OfClass(typeof(Material)).ToElements();
                    break;
                default:
                    candidates = new FilteredElementCollector(document).WhereElementIsElementType().ToElements();
                    break;
            }
            Element match = candidates.FirstOrDefault(element => ElementMatches(element, reference));
            return match?.Id;
        }

        private static bool ElementMatches(Element element, ElementReference reference)
        {
            if (element == null || !string.Equals(SafeGetElementName(element), reference.Name, StringComparison.CurrentCultureIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(reference.ClassName) &&
                !string.Equals(element.GetType().Name, reference.ClassName, StringComparison.Ordinal))
                return false;
            if (element is FillPatternElement fill && !string.IsNullOrWhiteSpace(reference.Auxiliary))
                return string.Equals(fill.GetFillPattern().Target.ToString(), reference.Auxiliary, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static string SafeGetElementName(Element element)
        {
            try { return element.Name; }
            catch { return string.Empty; }
        }

        private static T SafeGet<T>(Func<T> getter, T fallback)
        {
            try { return getter(); }
            catch { return fallback; }
        }

        private static void Warn(ViewTemplateImportReport report, string warning)
        {
            if (report.Warnings.Count < MaximumWarnings) report.Warnings.Add(warning);
        }
    }
}
