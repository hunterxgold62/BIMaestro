using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Visualisation
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PipeSystemColorsCommand : BaseTrackedCommand
    {
        private const string ManagedFilterPrefix = "BIMaestro_PipeSystemColor_";
        // V2 uses public write access. Revit 2025 can reject Vendor access when the
        // assembly is loaded through a deployment manifest whose vendor identity
        // differs from the development manifest. Schemas are immutable once loaded,
        // hence the new GUID instead of attempting to alter the V1 schema.
        private static readonly Guid MarkerSchemaGuid = new Guid("0A4F97EF-5974-46D1-A210-15FC8460EE50");

        protected override string ButtonId => "PipeSystemColors";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = data.Application.ActiveUIDocument;
            Document document = uiDocument?.Document;
            View view = uiDocument?.ActiveView;

            if (document == null || view == null)
            {
                TaskDialog.Show("BIMaestro", "Aucun document Revit actif.");
                return Result.Cancelled;
            }

            string unsupportedReason = GetUnsupportedViewReason(document, view);
            if (unsupportedReason != null)
            {
                TaskDialog.Show("Couleurs réseaux", unsupportedReason);
                return Result.Cancelled;
            }

            List<ParameterFilterElement> managedFilters = GetManagedFilters(document);
            HashSet<ElementId> managedIds = new HashSet<ElementId>(managedFilters.Select(filter => filter.Id));
            List<ElementId> filtersInView = view.GetFilters().Where(managedIds.Contains).ToList();

            if (filtersInView.Count > 0)
            {
                using (var transaction = new Transaction(document, "BIMaestro - Désactiver les couleurs réseaux"))
                {
                    transaction.Start();
                    foreach (ElementId filterId in filtersInView)
                    {
                        view.RemoveFilter(filterId);
                    }
                    transaction.Commit();
                }

                TaskDialog.Show("Couleurs réseaux", "Coloration désactivée dans la vue active.");
                return Result.Succeeded;
            }

            List<PipingSystemType> systemTypes = new FilteredElementCollector(document)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .OrderBy(systemType => systemType.Id.GetIdLongValue())
                .ToList();

            if (systemTypes.Count == 0)
            {
                TaskDialog.Show("Couleurs réseaux", "Aucun type de système de canalisation n'a été trouvé dans ce projet.");
                return Result.Cancelled;
            }

            ElementId solidFillId = FindSolidFillPatternId(document);
            if (solidFillId == ElementId.InvalidElementId)
            {
                TaskDialog.Show("Couleurs réseaux", "Le motif de remplissage uni (Solid Fill) est introuvable dans le projet.");
                return Result.Failed;
            }

            var categories = new List<ElementId>
            {
                new ElementId(BuiltInCategory.OST_PipeCurves),
                new ElementId(BuiltInCategory.OST_PipeFitting)
            };

            ElementId systemTypeParameterId = new ElementId(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            ICollection<ElementId> commonParameters = ParameterFilterUtilities.GetFilterableParametersInCommon(document, categories);
            if (!commonParameters.Contains(systemTypeParameterId))
            {
                TaskDialog.Show(
                    "Couleurs réseaux",
                    "Cette version de Revit ne permet pas d'utiliser « Type de système » dans un filtre commun aux canalisations et raccords.");
                return Result.Failed;
            }

            using (var transaction = new Transaction(document, "BIMaestro - Activer les couleurs réseaux"))
            {
                transaction.Start();
                Schema schema = GetOrCreateMarkerSchema();
                var filtersBySystemId = managedFilters
                    .Select(filter => new { Filter = filter, SystemId = ReadMarkedSystemId(filter, schema) })
                    .Where(item => item.SystemId.HasValue)
                    .GroupBy(item => item.SystemId.Value)
                    .ToDictionary(group => group.Key, group => group.First().Filter);

                foreach (PipingSystemType systemType in systemTypes)
                {
                    long systemId = systemType.Id.GetIdLongValue();
                    ElementFilter rule = CreateSystemTypeRule(systemTypeParameterId, systemType.Id);
                    ParameterFilterElement filter;

                    if (!filtersBySystemId.TryGetValue(systemId, out filter))
                    {
                        string filterName = GetAvailableFilterName(document, systemId);
                        filter = ParameterFilterElement.Create(document, filterName, categories, rule);
                        MarkAsManaged(filter, schema, systemId);
                    }
                    else
                    {
                        filter.SetCategories(categories);
                        filter.SetElementFilter(rule);
                    }

                    if (!view.GetFilters().Contains(filter.Id))
                    {
                        view.AddFilter(filter.Id);
                    }

                    view.SetFilterVisibility(filter.Id, true);
                    view.SetFilterOverrides(filter.Id, CreateOverrides(systemType.LineColor, solidFillId));
                }

                transaction.Commit();
            }

            TaskDialog.Show("Couleurs réseaux", "Coloration activée et synchronisée dans la vue active.");
            return Result.Succeeded;
        }

        private static ElementFilter CreateSystemTypeRule(ElementId parameterId, ElementId systemTypeId)
        {
            var provider = new ParameterValueProvider(parameterId);
            var evaluator = new FilterNumericEquals();
            var rule = new FilterElementIdRule(provider, evaluator, systemTypeId);
            return new ElementParameterFilter(rule);
        }

        private static OverrideGraphicSettings CreateOverrides(Color color, ElementId solidFillId)
        {
            var overrides = new OverrideGraphicSettings();
            overrides.SetProjectionLineColor(color);
            overrides.SetSurfaceForegroundPatternId(solidFillId);
            overrides.SetSurfaceForegroundPatternColor(color);
            overrides.SetCutLineColor(color);
            overrides.SetCutForegroundPatternId(solidFillId);
            overrides.SetCutForegroundPatternColor(color);
            return overrides;
        }

        private static ElementId FindSolidFillPatternId(Document document)
        {
            FillPatternElement solidFill = new FilteredElementCollector(document)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(element => element.GetFillPattern().IsSolidFill);
            return solidFill?.Id ?? ElementId.InvalidElementId;
        }

        private static string GetUnsupportedViewReason(Document document, View view)
        {
            if (view.IsTemplate)
            {
                return "La vue active est un gabarit. BIMaestro ne modifie pas les gabarits de vue.";
            }

            if (!view.AreGraphicsOverridesAllowed())
            {
                return "La vue active ne prend pas en charge les remplacements graphiques et les filtres.";
            }

            if (view.ViewTemplateId != ElementId.InvalidElementId)
            {
                View template = document.GetElement(view.ViewTemplateId) as View;
                ElementId filtersParameterId = new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS);
                if (template != null && !template.GetNonControlledTemplateParameterIds().Contains(filtersParameterId))
                {
                    return "Le gabarit appliqué à la vue contrôle les filtres de visibilité/graphisme. Libérez ce paramètre dans le gabarit ou appliquez la commande à une vue non verrouillée.";
                }
            }

            return null;
        }

        private static List<ParameterFilterElement> GetManagedFilters(Document document)
        {
            Schema schema = Schema.Lookup(MarkerSchemaGuid);
            if (schema == null)
            {
                return new List<ParameterFilterElement>();
            }

            return new FilteredElementCollector(document)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(filter => ReadMarkedSystemId(filter, schema).HasValue)
                .ToList();
        }

        private static Schema GetOrCreateMarkerSchema()
        {
            Schema schema = Schema.Lookup(MarkerSchemaGuid);
            if (schema != null)
            {
                return schema;
            }

            var builder = new SchemaBuilder(MarkerSchemaGuid);
            builder.SetSchemaName("BIMaestroPipeSystemColorFilter");
            builder.SetDocumentation("Identifie exclusivement les filtres de coloration des réseaux créés par BIMaestro.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("PipingSystemTypeId", typeof(long));
            return builder.Finish();
        }

        private static void MarkAsManaged(ParameterFilterElement filter, Schema schema, long systemTypeId)
        {
            var entity = new Entity(schema);
            entity.Set(schema.GetField("PipingSystemTypeId"), systemTypeId);
            filter.SetEntity(entity);
        }

        private static long? ReadMarkedSystemId(ParameterFilterElement filter, Schema schema)
        {
            try
            {
                Entity entity = filter.GetEntity(schema);
                if (!entity.IsValid())
                {
                    return null;
                }

                return entity.Get<long>(schema.GetField("PipingSystemTypeId"));
            }
            catch
            {
                return null;
            }
        }

        private static string GetAvailableFilterName(Document document, long systemTypeId)
        {
            string baseName = ManagedFilterPrefix + systemTypeId;
            var usedNames = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>()
                    .Select(filter => filter.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!usedNames.Contains(baseName))
            {
                return baseName;
            }

            int suffix = 2;
            while (usedNames.Contains(baseName + "_" + suffix))
            {
                suffix++;
            }

            return baseName + "_" + suffix;
        }
    }
}
