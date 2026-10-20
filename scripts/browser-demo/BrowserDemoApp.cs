using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using DataStorage = Autodesk.Revit.DB.ExtensibleStorage.DataStorage;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

public class BrowserDemoApp : IExternalApplication
{
    static string Root => Path.GetDirectoryName(typeof(BrowserDemoApp).Assembly.Location);
    static string ProjectRoot => Path.GetFullPath(Path.Combine(Root, "../browser-linkedin-demo-final"));
    Assembly plugin;
    ExternalEvent request;
    int step;
    readonly string[] names = { "01-Atelier-Sobre", "02-Campus-Disciplines", "03-Renovation-Phases", "04-Hotel-Zones", "05-Studio-Creatif", "06-Dossier-Livraison" };
    readonly string[] modes = { "Verre dépoli", "Bulles pastel", "Plan d'architecte", "Encre dans l'eau", "Bulles pastel", "Verre dépoli" };
    readonly string[][] groups = {
        new[] { "00 - Présentation", "01 - Rez-de-chaussée", "02 - Étage", "03 - Toiture" },
        new[] { "Architecture", "Structure", "Ventilation", "Plomberie", "Électricité" },
        new[] { "01 - Existant", "02 - Démolition", "03 - Projet", "04 - Synthèse" },
        new[] { "Bâtiment A - Hall", "Bâtiment A - Chambres", "Bâtiment B - Restaurant", "Extérieurs - Jardin" },
        new[] { "Inspiration", "Architecture", "Coordination", "À présenter" },
        new[] { "01 - Études", "02 - Validation", "03 - Publication", "04 - Archives" }
    };
    readonly string[] backgrounds = { "#F5F4F0", "#EDF5FA", "#11253F", "#F1EAF7", "#FFF0F6", "#EEF5F1" };
    readonly string[] accents = { "#52645B", "#176B91", "#83C9F4", "#7D58AA", "#C94484", "#24765B" };
    readonly string[] colors = { "#CCE3F4", "#D5EBD8", "#F6DFC2", "#E1D5F1", "#F5D2DE" };
    public Result OnStartup(UIControlledApplication app) { app.Idling += Run; return Result.Succeeded; }
    public Result OnShutdown(UIControlledApplication app) { app.Idling -= Run; return Result.Succeeded; }
    object Call(string type, string method, params object[] args) => plugin.GetType("Couleur." + type, true).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
    object New(string type) => Activator.CreateInstance(plugin.GetType("Couleur." + type, true));
    void Set(object obj, string property, object value) => obj.GetType().GetProperty(property).SetValue(obj, value);
    static Color C(string value) => (Color)ColorConverter.ConvertFromString(value);
    void Log(string value) => File.AppendAllText(Path.Combine(Root, "generation.log"), DateTime.Now.ToString("O") + " " + value + Environment.NewLine);
    sealed class Handler : IExternalEventHandler
    {
        readonly BrowserDemoApp owner;
        public Handler(BrowserDemoApp value) { owner = value; }
        public string GetName() => "BIMaestro - Projets de présentation";
        public void Execute(UIApplication ui) => owner.Execute(ui);
    }
    void Run(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if (!File.Exists(Path.Combine(Root, "run.flag"))) return;
        if (request == null) request = ExternalEvent.Create(new Handler(this));
        request.Raise();
    }
    void Execute(UIApplication ui)
    {
        if (!File.Exists(Path.Combine(Root, "run.flag"))) return;
        try
        {
            if (step == 0)
            {
                File.Delete(Path.Combine(Root, "run.flag"));
                File.WriteAllText(Path.Combine(Root, "started.txt"), "Started");
                if (ui.Application.Documents.Size != 0) throw new Exception("Une instance vide est requise.");
                plugin = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "BIMaestro");
                if (File.Exists(Path.Combine(Root, "verify.flag"))) step = names.Length;
                Log("Plugin " + plugin.Location);
                // The flag is kept only for the duration of this explicitly launched generation.
                File.WriteAllText(Path.Combine(Root, "run.flag"), "running");
            }
            if (step < names.Length) { Create(ui, step++); return; }
            if (step < names.Length * 2)
            {
                int i = step++ - names.Length;
                var doc = ui.OpenAndActivateDocument(Path.Combine(ProjectRoot, names[i] + ".rvt")).Document;
                Verify(doc, i);
                Log("SWITCH PASS " + names[i]);
                return;
            }
            // Return to the first project after cycling through all six.
            Verify(ui.OpenAndActivateDocument(Path.Combine(ProjectRoot, names[0] + ".rvt")).Document, 0);
            Log("DONE - six projets créés, rouverts et activés");
            File.WriteAllText(Path.Combine(Root, "done.txt"), "PASS");
            File.Delete(Path.Combine(Root, "run.flag"));
        }
        catch (Exception ex)
        {
            Log("FAIL " + ex);
            File.WriteAllText(Path.Combine(Root, "failed.txt"), ex.ToString());
            if (File.Exists(Path.Combine(Root, "run.flag"))) File.Delete(Path.Combine(Root, "run.flag"));
        }
    }
    void Create(UIApplication ui, int i)
    {
        string path = Path.Combine(ProjectRoot, names[i] + ".rvt");
        if (File.Exists(path)) { Update(ui, i, path); return; }
        var doc = ui.Application.NewProjectDocument("C:/ProgramData/Autodesk/RVT 2024/Templates/Default_M_FRA.rte");
        try
        {
            using (var tx = new Transaction(doc, "Créer les vues de présentation"))
            {
                tx.Start();
                doc.ProjectInformation.Name = names[i];
                doc.ProjectInformation.Number = "BIM-DEMO-" + (i + 1).ToString("00");
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
                var floor = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.FloorPlan);
                var draft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.Drafting);
                for (int g = 0; g < groups[i].Length; g++)
                {
                    var type = (ViewFamilyType)floor.Duplicate(groups[i][g]);
                    foreach (var label in new[] { "Implantation", "Aménagement", "Repérage", "Détails" })
                    {
                        var view = ViewPlan.Create(doc, type.Id, level.Id);
                        view.Name = groups[i][g] + " - " + label;
                        if (g == 0 && label == "Implantation") StartingViewSettings.GetStartingViewSettings(doc).ViewId = view.Id;
                        view.Scale = label == "Détails" ? 20 : 100;
                        // A few schematic detail lines make the empty views usable in a recording.
                        foreach (var line in new[] { Line.CreateBound(new XYZ(0,0,0),new XYZ(30,0,0)), Line.CreateBound(new XYZ(30,0,0),new XYZ(30,20,0)), Line.CreateBound(new XYZ(30,20,0),new XYZ(0,20,0)), Line.CreateBound(new XYZ(0,20,0),new XYZ(0,0,0)) }) doc.Create.NewDetailCurve(view, line);
                    }
                    var detail = ViewDrafting.Create(doc, draft.Id);
                    detail.Name = groups[i][g] + " - Carnet de détails";
                    var sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
                    sheet.SheetNumber = "D" + (i+1) + "-" + (g+1).ToString("00");
                    sheet.Name = groups[i][g] + " - Planche";
                }
                var threeD = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                View3D.CreateIsometric(doc, threeD.Id).Name = "00 - Vue d'ensemble";
                tx.Commit();
            }
            object browser = Call("ProjectBrowserColorPreferences", "GetDefaults");
            Set(browser, "IsEnabled", true);
            Set(browser, "BackgroundMode", modes[i]);
            Set(browser, "BackgroundColor", C(backgrounds[i]));
            Set(browser, "TextColor", C(i == 2 ? "#E2EFFC" : "#29323D"));
            Set(browser, "AccentColor", C(accents[i]));
            Set(browser, "BackgroundIntensity", i == 4 ? 45.0 : 30.0);
            Set(browser, "BackgroundAnimated", i == 3 || i == 4);
            Set(browser, "IsCategoryColoringEnabled", true);
            Set(browser, "IsViewTypeColoringEnabled", i == 1);
            Set(browser, "IsActiveViewParentHighlightEnabled", true);
            Set(browser, "ActiveViewParentColor", C(accents[i]));
            var rules = (IList)browser.GetType().GetProperty("CategoryColorRules").GetValue(browser);
            for (int g = 0; g < groups[i].Length; g++)
            {
                foreach (string effect in i == 0 ? new[] { "Texte" } : i == 4 ? new[] { "Dégradé", "Gras", "Pastille" } : i == 5 ? new[] { "Fond", "Bordure" } : new[] { "Fond", "Gras" })
                {
                    object rule = New("ProjectBrowserCategoryColorRule");
                    Set(rule, "CategoryName", "Plans d'étage (" + groups[i][g] + ")");
                    Set(rule, "Color", C(i == 0 ? accents[i] : colors[g % colors.Length]));
                    Set(rule, "Effect", effect);
                    Set(rule, "Scope", "Branche");
                    rules.Add(rule);
                }
            }
            object icons = Call("ProjectBrowserIcons", "Defaults");
            Set(icons, "Enabled", i != 0);
            var iconRules = (IList)icons.GetType().GetProperty("Rules").GetValue(icons);
            string[] ids = { "architecture", "structure", "ventilation", "plumbing", "power" };
            // Embed built-in bitmap assets as custom assets too, so their pixels travel with the RVT.
            var assets = (IList)icons.GetType().GetProperty("CustomAssets").GetValue(icons);
            var builtins = ((IEnumerable)Call("ProjectBrowserIcons", "Assets", icons)).Cast<object>().ToArray();
            for (int g = 0; g < groups[i].Length; g++)
            {
                string id = ids[g % ids.Length];
                object source = builtins.FirstOrDefault(a => (string)a.GetType().GetProperty("Id").GetValue(a) == id);
                string useId = id;
                if (source != null)
                {
                    var asset = New("BrowserIconAsset"); useId = "demo-" + id;
                    Set(asset, "Id", useId); Set(asset, "Name", groups[i][g]);
                    Set(asset, "Data", source.GetType().GetProperty("Data").GetValue(source)); assets.Add(asset);
                }
                object rule = New("BrowserIconRule");
                Set(rule, "Name", "Plans d'étage (" + groups[i][g] + ")"); Set(rule, "IconId", useId); iconRules.Add(rule);
            }
            CorrectContrast(browser, i);
            Call("ProjectBrowserProjectStorage", "Save", doc, browser, icons);
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            Log("CREATED " + names[i] + " views=" + new FilteredElementCollector(doc).OfClass(typeof(View)).GetElementCount() + " assets=" + assets.Count);
        }
        finally { doc.Close(false); }
    }
    void Verify(Document doc, int i)
    {
        if (!(bool)Call("ProjectBrowserProjectStorage", "HasAppearance", doc)) throw new Exception("Stockage absent");
        // The real ViewActivated handler must have loaded the correct project before this check.
        object settings = Call("ProjectBrowserColorPreferences", "Load");
        string actual = (string)settings.GetType().GetProperty("BackgroundMode").GetValue(settings);
        var color = (Color)settings.GetType().GetProperty("BackgroundColor").GetValue(settings);
        if (actual != modes[i] || color != C(backgrounds[i])) throw new Exception("Apparence active incorrecte pour " + names[i] + ": " + actual + " " + color);
        ValidateContrast(settings, i);
    }
    object Get(object obj, string property) => obj.GetType().GetProperty(property).GetValue(obj);
    void Update(UIApplication ui, int i, string path)
    {
        var doc = ui.Application.OpenDocumentFile(path);
        try
        {
            var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(new Guid("52B1D963-87AF-4C3E-A8E8-A64D4B72D916"));
            var storage = new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>().First(s => s.GetEntitySchemaGuids().Contains(schema.GUID));
            string json = storage.GetEntity(schema).Get<string>(schema.GetField("AppearanceJson"));
            object pack = Call("AppearancePackFile", "Deserialize", json);
            object browser = Get(pack, "Browser");
            CorrectContrast(browser, i);
            ValidateContrast(browser, i);
            object icons = Get(pack, "Icons");
            ConfigureIcons(icons, i);
            Call("ProjectBrowserProjectStorage", "Save", doc, browser, icons);
            doc.Save();
            Log("UPDATED CONTRAST PASS " + names[i]);
        }
        finally { doc.Close(false); }
    }
    void CorrectContrast(object browser, int i)
    {
        Color text = C(i == 2 ? "#E2EFFC" : "#29323D");
        Set(browser, "TextColor", text);
        // Active parent captions must also remain readable on colored branches.
        Set(browser, "ActiveViewParentColor", C(i == 2 ? "#9AD7FF" : "#17466A"));
        string[] dark = { "#173B55", "#254B3C", "#543B24", "#403253", "#542F43" };
        string[] ink = { "#17466A", "#275C3F", "#913219", "#633886", "#952653" };
        string[] lightInk = { "#9AD7FF", "#B1E9C2", "#FFD09A", "#D8B8FF", "#FFB1D1" };
        var rules = (IList)Get(browser, "CategoryColorRules");
        rules.Clear();
        for (int group = 0; group < groups[i].Length; group++)
        {
            string[] effects = i == 0 ? new[] { "Texte", group == 0 ? "Souligné" : "Pastille" }
                : i == 1 ? new[] { "Fond", "Texte", group == 0 ? "Gras" : "Bordure" }
                : i == 2 ? new[] { "Fond", "Texte", group == 3 ? "Badge" : "Pastille" }
                : i == 3 ? new[] { "Fond", "Texte", group == 2 ? "Italique" : "Bordure" }
                : i == 4 ? new[] { "Dégradé", "Texte", new[] { "Badge", "Italique", "Souligné", "Pastille" }[group] }
                : new[] { "Fond", "Texte", new[] { "Souligné", "Bordure", "Badge", "Italique" }[group] };
            foreach (string effect in effects)
            {
                object rule = New("ProjectBrowserCategoryColorRule");
                Set(rule, "CategoryName", "Plans d'étage (" + groups[i][group] + ")");
                Set(rule, "Scope", "Branche"); Set(rule, "Effect", effect);
                bool fill = effect == "Fond" || effect == "Dégradé";
                Set(rule, "Color", C(fill ? (i == 2 ? dark[group % 5] : colors[group % 5]) : (i == 2 ? lightInk[group % 5] : ink[(group + (i == 4 ? 3 : 0)) % 5])));
                rules.Add(rule);
            }
        }
    }
    void ConfigureIcons(object icons, int i)
    {
        string[][] ids = {
            new[] { "presentation", "levels", "room", "roof" },
            new[] { "architecture", "structure", "ventilation", "plumbing", "power" },
            new[] { "existing", "demolition", "new-project", "coordination" },
            new[] { "reception", "room", "restaurant", "garden" },
            new[] { "study", "architecture", "coordination", "presentation" },
            new[] { "study", "validation", "publication", "archive" }
        };
        Set(icons, "Enabled", true);
        var available = ((IEnumerable)Call("ProjectBrowserIcons", "Assets", icons)).Cast<object>().ToArray();
        var rules = (IList)Get(icons, "Rules"); rules.Clear();
        var assets = (IList)Get(icons, "CustomAssets"); assets.Clear();
        for (int g = 0; g < groups[i].Length; g++)
        {
            string id = ids[i][g];
            object source = available.First(a => (string)Get(a, "Id") == id);
            object asset = New("BrowserIconAsset");
            Set(asset, "Id", "demo-" + id); Set(asset, "Name", groups[i][g]); Set(asset, "Data", Get(source, "Data")); assets.Add(asset);
            object rule = New("BrowserIconRule");
            Set(rule, "Name", "Plans d'étage (" + groups[i][g] + ")"); Set(rule, "IconId", "demo-" + id); rules.Add(rule);
        }
        Log("ICONS " + names[i] + " " + string.Join(", ", ids[i]));
    }
    static double Luminance(Color color)
    {
        Func<byte, double> linear = b => { double v = b / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); };
        return .2126 * linear(color.R) + .7152 * linear(color.G) + .0722 * linear(color.B);
    }
    void Check(Color foreground, Color background, string label)
    {
        double a = Luminance(foreground), b = Luminance(background);
        double ratio = (Math.Max(a,b) + .05) / (Math.Min(a,b) + .05);
        if (ratio < 4.5) throw new Exception("Contraste insuffisant " + label + ": " + ratio.ToString("F2"));
    }
    void ValidateContrast(object browser, int i)
    {
        Color text = (Color)Get(browser, "TextColor"), background = (Color)Get(browser, "BackgroundColor");
        Color active = (Color)Get(browser, "ActiveViewParentColor");
        Check(text, background, names[i] + " fond général");
        Check(active, background, names[i] + " parent actif");
        var rules = ((IEnumerable)Get(browser, "CategoryColorRules")).Cast<object>().ToArray();
        foreach (var branch in rules.GroupBy(r => (string)Get(r, "CategoryName")))
        {
            Color fill = background, caption = text;
            foreach (object rule in branch)
            {
                string effect = (string)Get(rule, "Effect");
                if (effect == "Fond" || effect == "Dégradé") fill = (Color)Get(rule, "Color");
                if (new[] { "Texte", "Gras", "Italique", "Souligné", "Barré" }.Contains(effect)) caption = (Color)Get(rule, "Color");
            }
            Check(caption, fill, branch.Key);
            Check(active, fill, branch.Key + " parent actif");
            // A gradient also exposes the general background at its transparent end.
            if (branch.Any(r => (string)Get(r,"Effect") == "Dégradé")) Check(caption, background, branch.Key + " fin de dégradé");
        }
        if ((bool)Get(browser, "IsViewTypeColoringEnabled"))
            foreach (string property in new[] { "PlanViewColor", "SectionViewColor", "ThreeDViewColor", "ElevationViewColor", "ScheduleViewColor", "OtherViewColor" })
            { Check(text, (Color)Get(browser, property), property); Check(active, (Color)Get(browser, property), property + " actif"); }
        Log("CONTRAST >= 4.5 PASS " + names[i]);
    }
}
