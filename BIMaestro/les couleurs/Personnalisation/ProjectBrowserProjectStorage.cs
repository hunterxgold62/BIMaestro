using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Couleur
{
    /// <summary>
    /// Stores the shared Project Browser appearance in the RVT/RTE itself.
    /// Personal ribbon colors deliberately remain outside the model.
    /// </summary>
    public static class ProjectBrowserProjectStorage
    {
        private static readonly Guid SchemaId =
            new Guid("52B1D963-87AF-4C3E-A8E8-A64D4B72D916");
        private const string SchemaName = "BIMaestroProjectBrowserAppearance";
        private const string PayloadField = "AppearanceJson";
        private static string _activeDocumentKey;
        private static string _activePayload;

        public static void Save(
            Document document,
            ProjectBrowserColorSettings browser,
            BrowserIconSettings icons)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (document.IsReadOnly)
                throw new InvalidOperationException("La maquette est en lecture seule.");
            if (document.IsFamilyDocument)
                throw new InvalidOperationException("Cette option est réservée aux projets et aux gabarits de projet.");

            string json = AppearancePackFile.Serialize(new AppearancePack
            {
                RibbonEnabled = false,
                FullPanels = false,
                Ribbon = new Dictionary<string, RibbonPanelColorScheme>(),
                Browser = ProjectBrowserColorPreferences.Clone(browser),
                Icons = icons
            });

            using (var transaction = new Transaction(
                document,
                "BIMaestro - Arborescence du projet"))
            {
                transaction.Start();
                Schema schema = GetOrCreateSchema();
                DataStorage storage = FindStorage(document) ??
                    DataStorage.Create(document);
                var entity = new Entity(schema);
                entity.Set(schema.GetField(PayloadField), json);
                storage.SetEntity(entity);
                transaction.Commit();
            }

            _activeDocumentKey = null;
            _activePayload = null;
        }

        public static bool HasAppearance(Document document) =>
            !string.IsNullOrWhiteSpace(TryRead(document));

        public static void Clear(Document document)
        {
            if (document == null || document.IsFamilyDocument || document.IsReadOnly)
                throw new InvalidOperationException("La configuration partagée ne peut pas être retirée de ce projet.");

            Schema schema = Schema.Lookup(SchemaId);
            DataStorage storage = FindStorage(document);
            if (schema == null || storage == null) return;

            using (var transaction = new Transaction(document, "BIMaestro - Retirer l’apparence partagée"))
            {
                transaction.Start();
                var entity = new Entity(schema);
                entity.Set(schema.GetField(PayloadField), string.Empty);
                storage.SetEntity(entity);
                transaction.Commit();
            }
            _activeDocumentKey = null;
            _activePayload = null;
            Activate(document);
        }

        public static bool Activate(Document document)
        {
            string documentKey = GetDocumentKey(document);
            string json = TryRead(document);
            if (string.Equals(_activeDocumentKey, documentKey, StringComparison.Ordinal) &&
                string.Equals(_activePayload, json, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                ProjectBrowserColorPreferences.SetProjectOverride(null);
                ProjectBrowserIcons.SetProjectOverride(null);
            }
            else
            {
                AppearancePack pack = AppearancePackFile.Deserialize(json);
                ProjectBrowserColorPreferences.SetProjectOverride(pack.Browser);
                ProjectBrowserIcons.SetProjectOverride(pack.Icons);
            }

            _activeDocumentKey = documentKey;
            _activePayload = json;
            return true;
        }

        public static bool Forget(Document document)
        {
            if (!string.Equals(
                    _activeDocumentKey,
                    GetDocumentKey(document),
                    StringComparison.Ordinal))
            {
                return false;
            }

            _activeDocumentKey = null;
            _activePayload = null;
            ProjectBrowserColorPreferences.SetProjectOverride(null);
            ProjectBrowserIcons.SetProjectOverride(null);
            return true;
        }

        private static string TryRead(Document document)
        {
            if (document == null || document.IsFamilyDocument)
                return null;

            Schema schema = Schema.Lookup(SchemaId);
            if (schema == null)
                return null;

            DataStorage storage = FindStorage(document);
            if (storage == null)
                return null;

            Entity entity = storage.GetEntity(schema);
            return entity.IsValid()
                ? entity.Get<string>(schema.GetField(PayloadField))
                : null;
        }

        private static DataStorage FindStorage(Document document)
        {
            Schema schema = Schema.Lookup(SchemaId);
            if (schema == null)
                return null;

            return new FilteredElementCollector(document)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(item =>
                    item.GetEntitySchemaGuids().Contains(SchemaId));
        }

        private static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaId);
            if (existing != null)
                return existing;

            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName(SchemaName);
            builder.SetDocumentation(
                "Configuration partagée de l'arborescence BIMaestro.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(PayloadField, typeof(string));
            return builder.Finish();
        }

        private static string GetDocumentKey(Document document)
        {
            if (document == null)
                return string.Empty;

            try
            {
                return document.GetHashCode() + "|" + document.PathName;
            }
            catch
            {
                return document.GetHashCode().ToString();
            }
        }
    }
}
