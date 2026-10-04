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
        internal static bool IsSheetPass => _phase == 3;
        internal static bool HasSheetSelection(UIDocument uiDoc) => uiDoc != null &&
            string.Equals(uiDoc.Document.PathName, _documentPath, StringComparison.OrdinalIgnoreCase) &&
            uiDoc.Selection.GetElementIds().Count == 3 &&
            Sheets(uiDoc.Document).Take(3).Count() == 3 &&
            Sheets(uiDoc.Document).Take(3).All(sheet => uiDoc.Selection.GetElementIds().Contains(sheet.Id));
        private static readonly string[] SheetNames = { "BIMaestro - Feuille essai A", "BIMaestro - Feuille essai B", "BIMaestro - Feuille essai C", "BIMaestro - Feuille témoin" };
        internal static List<ViewSheet> Sheets(Document doc) => new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(sheet => SheetNames.Contains(sheet.Name))
            .OrderBy(sheet => Array.IndexOf(SheetNames, sheet.Name)).ToList();
        internal static void EnsureSheets(Document doc)
        {
            var existing = Sheets(doc);
            for (int i = 0; i < SheetNames.Length; i++)
            {
                if (existing.Any(sheet => sheet.Name == SheetNames[i])) continue;
                string number = (i + 3).ToString();
                if (new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Any(sheet => sheet.SheetNumber == number))
                    throw new InvalidOperationException("Le numéro de feuille " + number + " est déjà utilisé. Utilise une nouvelle maquette de formation.");
                ViewSheet sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
                sheet.Name = SheetNames[i];
                sheet.SheetNumber = number;
            }
        }


        internal static void Begin(Document document)
        {
            using (var tx = new Transaction(document, "BIMaestro - Feuilles d’essai Organisateur"))
            {
                tx.Start();
                EnsureSheets(document);
                var sheets = Sheets(document);
                if (new FilteredElementCollector(document).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Any(sheet => !sheets.Any(owned => owned.Id == sheet.Id) && new[] { "3", "4", "5", "6" }.Contains(sheet.SheetNumber)))
                    throw new InvalidOperationException("Les numéros 3 à 6 sont utilisés hors des feuilles d’essai. Utilise une nouvelle maquette de formation.");
                foreach (var sheet in sheets) sheet.SheetNumber = "BIMaestro-" + Guid.NewGuid().ToString("N");
                for (int i = 0; i < sheets.Count; i++) sheets[i].SheetNumber = (i + 3).ToString();
                tx.Commit();
            }
            _documentPath = document.PathName;
            _phase = 1;
            _firstPassNumbers = null;
        }

        internal static void ConfigureWindow(ElementRenamerWindow window, UIApplication app)
        {
            if (IsSheetPass)
            {
                var sheet = Sheets(app.ActiveUIDocument.Document).First();
                window.SelectedParameter = sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER).Definition.Name;
                window.Prefix = "";
                window.Suffix = "";
                window.SelectedNumberFormat = "1,2,3...";
                window.StartNumber = "1";
                window.IsSortByLevelEnabled = false;
                return;
            }
            if (_phase == 1 && app.ActiveUIDocument?.Document?.PathName == _documentPath &&
                app.ActiveUIDocument.ActiveView is View3D view)
            {
                BoundingBoxXYZ box = view.GetSectionBox();
                XYZ center = box.Transform.OfPoint((box.Min + box.Max) * 0.5);
                using (var transaction = new Transaction(app.ActiveUIDocument.Document,
                    "BIMaestro - Vue de dessus pour numéroter"))
                {
                    transaction.Start();
                    view.SetOrientation(new ViewOrientation3D(center + XYZ.BasisZ * 40,
                        XYZ.BasisY, -XYZ.BasisZ));
                    transaction.Commit();
                }
                app.ActiveUIDocument.RefreshActiveView();
            }
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
            if (IsSheetPass)
            {
                var sheets = Sheets(document);
                bool valid = sheets.Count == 4 && selectedIds.Count == 3 && sheets.Take(3).All(sheet => selectedIds.Contains(sheet.Id)) &&
                    sheets.Select(sheet => sheet.SheetNumber).SequenceEqual(new[] { "1", "2", "3", "6" });
                if (!valid)
                {
                    DemoTourMessage.Show(app.MainWindowHandle, "Vérifie les feuilles", "Sélectionne uniquement les trois feuilles d’essai A, B et C. Renumérote Numéro de feuille à partir de 1, sans préfixe ni suffixe. La feuille témoin doit rester 6.");
                    DemoOrganizerSheetGuide.Start(app);
                    return;
                }
                _phase = 0;
                DemoTourCompletion.Show(app.MainWindowHandle, "organizer", "Les feuilles A, B et C portent maintenant 1, 2 et 3. La feuille témoin reste 6 : seules les feuilles sélectionnées dans l’arborescence ont été modifiées.");
                return;
            }
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
                    "Sélectionne les huit places et numérote « CML_Numéros de place » de PK-001 à PK-008. Bulbizarre les a resélectionnées pour réessayer.");
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
                    DemoTourMessage.Show(app.MainWindowHandle, "Bravo ! Tri par niveau validé",
                        "Les quatre places du niveau de base portent PK-001 à PK-004 ; celles du niveau supérieur portent PK-005 à PK-008. Les numéros sont visibles sur les familles. Observe les deux rangées, puis laisse Bulbizarre tourner cette vue 3D de 90°.",
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
            _phase = 3;
            SelectAndFrame(uiDocument, places);
            string firstChange = changed[0].Key.Substring(ParkingPrefix.Length) + " : " +
                _firstPassNumbers[changed[0].Key] + " → " + changed[0].Value;
            DemoTourMessage.Show(app.MainWindowHandle, "Bravo ! Ordre de lecture comparé",
                "Après rotation de la vue et sans tri par niveau, " + changed.Count +
                " place(s) sur huit ont changé de numéro. Exemple, place " + firstChange +
                ". Passons aux feuilles : sélectionne les trois feuilles d’essai dans l’arborescence pour les renuméroter.");
            uiDocument.Selection.SetElementIds(new List<ElementId>());
            DemoOrganizerSheetGuide.Start(app);
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
            DemoTourMessage.Show(app.MainWindowHandle, "Bulbizarre vérifie les places", message);
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
    internal sealed class DemoOrganizerSheetGuide : IExternalEventHandler
    {
        private static DemoOrganizerSheetGuide _current;
        private readonly ExternalEvent _event;
        private readonly System.Windows.Window _card;
        private readonly List<System.Windows.Window> _outlines = new List<System.Windows.Window>();
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private readonly IntPtr _owner;
        private readonly System.Windows.Controls.TextBlock _hint;
        private bool _closed;
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        internal static void Start(UIApplication app)
        {
            _current?.Close();
            _current = new DemoOrganizerSheetGuide(app);
            _current._card.Show();
        }
        private DemoOrganizerSheetGuide(UIApplication app)
        {
            _owner = app.MainWindowHandle;
            _event = ExternalEvent.Create(this);
            _card = new System.Windows.Window { Title = "BIMaestro — Guide Organisateur", Width = 420, Height = 290,
                ResizeMode = System.Windows.ResizeMode.NoResize, ShowInTaskbar = false,
                Background = System.Windows.Media.Brushes.White,
                Left = System.Windows.SystemParameters.WorkArea.Right - 450,
                Top = System.Windows.SystemParameters.WorkArea.Bottom - 320 };
            new System.Windows.Interop.WindowInteropHelper(_card).Owner = _owner;
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(18) };
            _card.Content = panel;
            panel.Children.Add(new System.Windows.Controls.Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                Width = 34, Height = 34, HorizontalAlignment = System.Windows.HorizontalAlignment.Left });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Sélectionner les feuilles", FontSize = 18,
                Foreground = DemoTourPalette.Accent, Margin = new System.Windows.Thickness(0, 8, 0, 8) });
            _hint = new System.Windows.Controls.TextBlock { Text = "Déplie Feuilles dans l’arborescence. Avec Ctrl + clic, sélectionne les feuilles 3, 4 et 5 (essai A, B et C). Laisse la feuille témoin 6 hors sélection.",
                TextWrapping = System.Windows.TextWrapping.Wrap };
            panel.Children.Add(_hint);
            var next = new System.Windows.Controls.Button { Content = "Vérifier ma sélection", Background = DemoTourPalette.Accent,
                Foreground = System.Windows.Media.Brushes.White, Padding = new System.Windows.Thickness(8), Margin = new System.Windows.Thickness(0, 12, 0, 0) };
            next.Click += (_, __) => _event.Raise();
            panel.Children.Add(next);
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += (_, __) => OutlineSheets();
            _timer.Start();
            _card.Closed += (_, __) => Close();
        }
        private void OutlineSheets()
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(_owner);
                var items = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.TreeItem));
                var bounds = new List<System.Windows.Rect>();
                foreach (System.Windows.Automation.AutomationElement item in items)
                {
                    if (item.Current.IsOffscreen) continue;
                    string name = item.Current.Name ?? "";
                    if (!new[] { "BIMaestro - Feuille essai A", "BIMaestro - Feuille essai B", "BIMaestro - Feuille essai C" }
                        .Any(label => name.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    var rect = item.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0) bounds.Add(rect);
                }
                while (_outlines.Count < bounds.Count)
                {
                    var outline = new System.Windows.Window { WindowStyle = System.Windows.WindowStyle.None,
                        AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                        ShowActivated = false, ShowInTaskbar = false, Topmost = true, Width = 20, Height = 20,
                        Content = new System.Windows.Controls.Border { BorderBrush = DemoTourPalette.Accent,
                            BorderThickness = new System.Windows.Thickness(3), CornerRadius = new System.Windows.CornerRadius(4) } };
                    new System.Windows.Interop.WindowInteropHelper(outline).Owner = _owner;
                    outline.SourceInitialized += (_, __) => {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(outline).Handle;
                        SetWindowLongPtr(hwnd, -20, new IntPtr(GetWindowLongPtr(hwnd, -20).ToInt64() | 0x20 | 0x80 | 0x08000000));
                    };
                    _outlines.Add(outline);
                }
                for (int i = 0; i < _outlines.Count; i++)
                {
                    var window = _outlines[i];
                    if (i >= bounds.Count) { window.Hide(); continue; }
                    if (!window.IsVisible) window.Show();
                    var rect = bounds[i];
                    SetWindowPos(new System.Windows.Interop.WindowInteropHelper(window).Handle, new IntPtr(-1),
                        (int)rect.Left - 3, (int)rect.Top - 3, (int)rect.Width + 6, (int)rect.Height + 6, 0x10);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Guide feuilles : " + ex.Message); }
        }
        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;
            if (!DemoOrganizerExercise.HasSheetSelection(app.ActiveUIDocument))
            {
                _hint.Text = "Sélectionne uniquement les trois feuilles d’essai A, B et C avec Ctrl + clic dans l’arborescence, puis vérifie à nouveau.";
                return;
            }
            Close();
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "organizer");
        }
        public string GetName() => "BIMaestro - Sélection des feuilles de formation";
        private void Close()
        {
            if (_closed) return;
            _closed = true;
            _timer.Stop();
            foreach (var window in _outlines) window.Close();
            _card.Close();
        }
    }

}
