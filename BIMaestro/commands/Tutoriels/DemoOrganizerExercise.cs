using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Modification;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Tutorials
{
    internal static class DemoOrganizerExercise
    {
        private const string ParkingParameter = "CML_Numéros de place";
        private const string ParkingPrefix = DemoProjectBuilder.DemoPrefix + "ORGANISATEUR_";
        private static string _documentPath;
        private static int _phase;
        private static Dictionary<string, string> _firstPassNumbers;

        internal static bool IsRotatedPass => _phase == 2;

        internal static void Begin(Document document)
        {
            _documentPath = document.PathName;
            _phase = 1;
            _firstPassNumbers = null;
        }

        internal static void ConfigureWindow(ElementRenamerWindow window)
        {
            if (!IsRotatedPass) return;
            window.SelectedParameter = ParkingParameter;
            window.Prefix = "PK-";
            window.SelectedNumberFormat = "001,002,003...";
            window.StartNumber = "1";
            window.IsSortByLevelEnabled = false;
        }

        internal static void Verify(UIApplication app, ICollection<ElementId> selectedIds,
            bool sortByLevel, string selectedParameter)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            if (_phase == 0 || uiDocument == null ||
                !string.Equals(uiDocument.Document.PathName, _documentPath,
                    StringComparison.OrdinalIgnoreCase)) return;

            Document document = uiDocument.Document;
            var places = DemoExerciseElements.Find(document, "ORGANISATEUR_")
                .Select(id => document.GetElement(id) as FamilyInstance)
                .Where(place => place != null)
                .ToList();
            var numbers = places.GroupBy(ParkingMark).ToDictionary(group => group.Key,
                group => group.Last().LookupParameter(ParkingParameter)?.AsString() ?? "");
            var expectedNumbers = new HashSet<string>(Enumerable.Range(1, 8)
                .Select(number => "PK-" + number.ToString("D3")), StringComparer.Ordinal);
            bool allSelected = places.Count == 8 &&
                places.Select(ParkingIndex).OrderBy(index => index)
                    .SequenceEqual(Enumerable.Range(1, 8)) &&
                places.All(place => selectedIds.Contains(place.Id));
            bool numbersValid = numbers.Count == 8 &&
                new HashSet<string>(numbers.Values, StringComparer.Ordinal).SetEquals(expectedNumbers);
            if (!allSelected || !numbersValid || selectedParameter != ParkingParameter)
            {
                Retry(app, uiDocument,
                    "Sélectionne les huit places et numérote « CML_Numéros de place » de PK-001 à PK-008. Pikachu les a resélectionnées pour réessayer.");
                return;
            }

            if (_phase == 1)
            {
                bool baseFirst = places.Where(place => ParkingIndex(place) <= 4)
                    .All(place => ParseNumber(numbers[ParkingMark(place)]) <= 4);
                bool upperSecond = places.Where(place => ParkingIndex(place) >= 5)
                    .All(place => ParseNumber(numbers[ParkingMark(place)]) >= 5);
                var basePlaces = places.Where(place => ParkingIndex(place) <= 4).ToList();
                var upperPlaces = places.Where(place => ParkingIndex(place) >= 5).ToList();
                bool distinctLevels = basePlaces.Select(place => place.LevelId)
                    .Distinct().Count() == 1 &&
                    upperPlaces.Select(place => place.LevelId).Distinct().Count() == 1 &&
                    !basePlaces[0].LevelId.Equals(upperPlaces[0].LevelId);
                if (!sortByLevel || !baseFirst || !upperSecond || !distinctLevels)
                {
                    Retry(app, uiDocument,
                        "Pour ce premier passage, coche « Trier par niveau » : les quatre places basses doivent recevoir PK-001 à PK-004, puis les quatre places hautes PK-005 à PK-008.");
                    return;
                }
                try
                {
                    SynchronizeVisibleNumbers(document, places);
                    SelectAndFrame(uiDocument, places);
                    uiDocument.RefreshActiveView();
                    DemoTourMessage.Show(app.MainWindowHandle, "Pika ! Tri par niveau validé",
                        "Les quatre places du niveau de base portent PK-001 à PK-004 ; celles du niveau supérieur portent PK-005 à PK-008. Les numéros sont visibles sur les familles. Observe les deux rangées, puis laisse Pikachu tourner cette vue 3D de 90°.",
                        "Tourner la vue de 90°");
                    RotateOrganizerView(document);
                    uiDocument.RefreshActiveView();
                }
                catch (Exception ex)
                {
                    Retry(app, uiDocument, "La vue n'a pas pu tourner de 90° : " + ex.Message);
                    return;
                }
                _firstPassNumbers = new Dictionary<string, string>(numbers);
                _phase = 2;
                SelectAndFrame(uiDocument, places);
                Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "organizer");
                return;
            }

            if (sortByLevel || _firstPassNumbers == null)
            {
                Retry(app, uiDocument,
                    "Dans la vue tournée, laisse « Trier par niveau » décoché pour comparer l'ordre visuel à celui du premier passage.");
                return;
            }
            var changed = numbers.Where(pair => _firstPassNumbers.TryGetValue(pair.Key, out string oldValue) &&
                    oldValue != pair.Value)
                .OrderBy(pair => pair.Key)
                .ToList();
            if (changed.Count == 0)
            {
                Retry(app, uiDocument,
                    "Aucune place n'a changé de numéro. La vue a été tournée ; relance Organisateur avec « Trier par niveau » décoché et les huit places sélectionnées.");
                return;
            }
            try { SynchronizeVisibleNumbers(document, places); }
            catch (Exception ex)
            {
                Retry(app, uiDocument, "Les numéros sont calculés, mais leur texte visible n'a pas pu être mis à jour : " + ex.Message);
                return;
            }
            _phase = 0;
            SelectAndFrame(uiDocument, places);
            string firstChange = changed[0].Key.Substring(ParkingPrefix.Length) + " : " +
                _firstPassNumbers[changed[0].Key] + " → " + changed[0].Value;
            DemoTourMessage.Show(app.MainWindowHandle, "Pika ! Ordre de lecture comparé",
                "Après rotation de la vue et sans tri par niveau, " + changed.Count +
                " place(s) sur huit ont changé de numéro. Exemple, place " + firstChange +
                ". Les numéros sont inscrits dans CML_Numéros de place et recopiés dans Commentaires pour être lisibles sur la famille dans cette vue 3D.");
        }

        private static void SynchronizeVisibleNumbers(Document document, List<FamilyInstance> places)
        {
            using (var transaction = new Transaction(document, "BIMaestro - Afficher les numéros des parkings"))
            {
                transaction.Start();
                foreach (FamilyInstance place in places)
                {
                    Parameter number = place.LookupParameter(ParkingParameter);
                    Parameter visible = place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (number == null || visible == null || visible.IsReadOnly ||
                        visible.StorageType != StorageType.String)
                        throw new InvalidOperationException("La famille CML_Parking doit avoir un paramètre Commentaires modifiable pour afficher le numéro.");
                    string value = number.AsString() ?? "";
                    if (visible.AsString() != value) visible.Set(value);
                }
                transaction.Commit();
            }
        }

        private static void RotateOrganizerView(Document document)
        {
            View3D view = new FilteredElementCollector(document).OfClass(typeof(View3D))
                .Cast<View3D>().FirstOrDefault(candidate =>
                    candidate.Name == "BIMaestro - 05 Organisateur");
            if (view == null) throw new InvalidOperationException("La vue 05 Organisateur manque.");
            BoundingBoxXYZ box = view.GetSectionBox();
            XYZ center = box.Transform.OfPoint((box.Min + box.Max) * 0.5);
            Transform turn = Transform.CreateRotationAtPoint(XYZ.BasisZ, Math.PI / 2, center);
            ViewOrientation3D orientation = view.GetOrientation();
            using (var transaction = new Transaction(document, "BIMaestro - Tourner la vue Organisateur de 90 degrés"))
            {
                transaction.Start();
                view.SetOrientation(new ViewOrientation3D(
                    turn.OfPoint(orientation.EyePosition),
                    turn.OfVector(orientation.UpDirection),
                    turn.OfVector(orientation.ForwardDirection)));
                transaction.Commit();
            }
        }

        private static void Retry(UIApplication app, UIDocument uiDocument, string message)
        {
            var places = DemoExerciseElements.Find(uiDocument.Document, "ORGANISATEUR_")
                .Select(id => uiDocument.Document.GetElement(id) as FamilyInstance)
                .Where(place => place != null).ToList();
            SelectAndFrame(uiDocument, places);
            DemoTourMessage.Show(app.MainWindowHandle, "Pikachu vérifie les places", message);
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "organizer");
        }

        private static void SelectAndFrame(UIDocument uiDocument, List<FamilyInstance> places)
        {
            var ids = places.Select(place => place.Id).ToList();
            if (ids.Count == 0) return;
            uiDocument.Selection.SetElementIds(ids);
            try { uiDocument.ShowElements(ids); } catch { }
        }

        private static string ParkingMark(FamilyInstance place) =>
            place.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";

        private static int ParkingIndex(FamilyInstance place)
        {
            string mark = ParkingMark(place);
            return mark.StartsWith(ParkingPrefix, StringComparison.Ordinal) &&
                   int.TryParse(mark.Substring(ParkingPrefix.Length), out int index)
                ? index : 0;
        }

        private static int ParseNumber(string number) =>
            number.StartsWith("PK-", StringComparison.Ordinal) &&
            int.TryParse(number.Substring(3), out int value) ? value : 0;
    }
}
