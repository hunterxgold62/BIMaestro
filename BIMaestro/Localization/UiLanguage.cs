using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace BIMaestro.Localization
{
    public enum UiLanguageChoice
    {
        Automatic,
        French,
        English
    }

    public sealed class UiLanguageOption
    {
        public UiLanguageOption(UiLanguageChoice value, string label)
        {
            Value = value;
            Label = label;
        }

        public UiLanguageChoice Value { get; }
        public string Label { get; }
    }

    public static class UiLanguage
    {
        private const string SettingsFileName = "ui-language.json";
        private static bool _windowHookInstalled;
        private static bool _automaticIsEnglish = !string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "fr",
            StringComparison.OrdinalIgnoreCase);

        public static UiLanguageChoice Choice { get; private set; } = LoadChoice();

        public static bool IsEnglish
        {
            get
            {
                if (Choice == UiLanguageChoice.English) return true;
                if (Choice == UiLanguageChoice.French) return false;
                return _automaticIsEnglish;
            }
        }

        public static IReadOnlyList<UiLanguageOption> Options => new[]
        {
            new UiLanguageOption(UiLanguageChoice.Automatic, T("Automatique (langue de Revit)", "Automatic (Revit language)")),
            new UiLanguageOption(UiLanguageChoice.French, "Français"),
            new UiLanguageOption(UiLanguageChoice.English, "English")
        };

        public static void Initialize(string revitLanguage = null)
        {
            if (!string.IsNullOrWhiteSpace(revitLanguage))
                _automaticIsEnglish = !string.Equals(revitLanguage, "French", StringComparison.OrdinalIgnoreCase);
            if (_windowHookInstalled) return;
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded),
                handledEventsToo: true);
            EventManager.RegisterClassHandler(
                typeof(FrameworkElement),
                FrameworkElement.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(OnContextMenuOpening),
                handledEventsToo: true);
            _windowHookInstalled = true;
        }

        public static bool SetChoice(UiLanguageChoice choice)
        {
            UiLanguageChoice previousChoice = Choice;
            Choice = choice;
            if (SaveChoice(choice)) return true;
            Choice = previousChoice;
            return false;
        }

        public static string T(string french, string english)
        {
            return IsEnglish ? english : french;
        }

        public static string T(string text)
        {
            if (!IsEnglish || string.IsNullOrWhiteSpace(text)) return text;
            return UiTextCatalog.TryGetEnglish(text, out var english) ? english : text;
        }

        public static void LocalizeWindow(Window window)
        {
            if (window == null || !IsEnglish || !IsBIMaestroWindow(window)) return;

            try
            {
                window.Title = T(window.Title);
            }
            catch (Exception ex)
            {
                LogLocalizationError(window, ex);
            }

            LocalizeElement(window);
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // This is a process-wide WPF class handler. Never inspect or modify a
            // Revit/third-party window, and never let localization escape the
            // Loaded event: an unhandled exception here terminates Revit.
            if (sender is not Window window || !IsBIMaestroWindow(window)) return;

            try
            {
                LocalizeWindow(window);
                window.Dispatcher.BeginInvoke(
                    new Action(() => LocalizeWindow(window)),
                    DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                LogLocalizationError(window, ex);
            }
        }

        private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!IsEnglish || !(sender is FrameworkElement element)) return;

            try
            {
                if (element.ContextMenu != null)
                    LocalizeElement(element.ContextMenu);
            }
            catch (Exception ex)
            {
                LogLocalizationError(element, ex);
            }
        }

        private static void LocalizeElement(DependencyObject element)
        {
            if (element == null) return;

            var pending = new Stack<DependencyObject>();
            var visited = new HashSet<DependencyObject>();
            pending.Push(element);

            while (pending.Count > 0)
            {
                DependencyObject current = pending.Pop();
                if (current == null || !visited.Add(current)) continue;

                TryLocalizeCurrentElement(current);

                int count;
                try
                {
                    count = VisualTreeHelper.GetChildrenCount(current);
                }
                catch (Exception ex)
                {
                    LogLocalizationError(current, ex);
                    count = 0;
                }

                for (int i = count - 1; i >= 0; i--)
                {
                    try
                    {
                        DependencyObject child = VisualTreeHelper.GetChild(current, i);
                        if (child != null) pending.Push(child);
                    }
                    catch (Exception ex)
                    {
                        LogLocalizationError(current, ex);
                    }
                }

                try
                {
                    foreach (object child in LogicalTreeHelper.GetChildren(current))
                    {
                        if (child is DependencyObject dependencyChild)
                            pending.Push(dependencyChild);
                    }
                }
                catch (Exception ex)
                {
                    LogLocalizationError(current, ex);
                }

                if (current is Popup popup && popup.Child != null)
                    pending.Push(popup.Child);
            }
        }

        private static void TryLocalizeCurrentElement(DependencyObject element)
        {
            try
            {
                if (element is TextBlock textBlock)
                {
                    if (textBlock.Inlines.Count == 0)
                        textBlock.Text = T(textBlock.Text);
                    else
                    {
                        // Snapshot the collection before changing Run.Text. Some
                        // custom controls invalidate their inline enumerator when
                        // a Run changes during Loaded.
                        var runs = new List<Run>();
                        foreach (Inline inline in textBlock.Inlines)
                            if (inline is Run run) runs.Add(run);

                        foreach (Run run in runs)
                            run.Text = T(run.Text);
                    }
                }

                // ComboBox item text is often also used as a legacy business value.
                // Translate it explicitly when that window has been migrated to stable values.
                if (element is ContentControl contentControl &&
                    contentControl is not ComboBoxItem &&
                    contentControl.Content is string content)
                    contentControl.Content = T(content);

                if (element is HeaderedContentControl headered && headered.Header is string header)
                    headered.Header = T(header);

                if (element is HeaderedItemsControl headeredItems && headeredItems.Header is string itemHeader)
                    headeredItems.Header = T(itemHeader);

                if (element is DataGrid dataGrid)
                {
                    foreach (DataGridColumn column in dataGrid.Columns)
                    {
                        if (column.Header is string columnHeader)
                            column.Header = T(columnHeader);
                    }
                }

                if (element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string toolTip)
                    frameworkElement.ToolTip = T(toolTip);
            }
            catch (Exception ex)
            {
                // A single custom control must not prevent the rest of the
                // window from being translated or bring down the Revit process.
                LogLocalizationError(element, ex);
            }
        }

        private static bool IsBIMaestroWindow(Window window)
        {
            return window?.GetType().Assembly == typeof(UiLanguage).Assembly;
        }

        private static void LogLocalizationError(object source, Exception ex)
        {
            string sourceType = source?.GetType().FullName ?? "unknown";
            Debug.WriteLine($"[UiLanguage] Localization skipped for '{sourceType}': {ex}");
        }

        private static UiLanguageChoice LoadChoice()
        {
            try
            {
                string path = GetSettingsPath();
                if (!File.Exists(path)) return UiLanguageChoice.Automatic;
                var settings = JsonConvert.DeserializeObject<LanguageSettings>(File.ReadAllText(path));
                return settings?.Language ?? UiLanguageChoice.Automatic;
            }
            catch
            {
                return UiLanguageChoice.Automatic;
            }
        }

        private static bool SaveChoice(UiLanguageChoice choice)
        {
            try
            {
                string path = GetSettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(
                    new LanguageSettings { Language = choice }, Formatting.Indented));
                return true;
            }
            catch
            {
                // A language preference must never prevent BIMaestro from starting.
                return false;
            }
        }

        private static string GetSettingsPath()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "SauvegardePréférence");
            return Path.Combine(directory, SettingsFileName);
        }

        private sealed class LanguageSettings
        {
            public UiLanguageChoice Language { get; set; }
        }
    }

    internal static class UiTextCatalog
    {
        private static readonly Dictionary<string, string> English =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Options BIMaestro"] = "BIMaestro Options",
                ["Configuration BIMaestro"] = "BIMaestro settings",
                ["Personnalisez l’ordre des panneaux et boutons du ruban, puis renseignez vos informations de contact si besoin."] = "Customize the ribbon panel and button order, then enter your contact details if needed.",
                ["Ouvrir l’aide en ligne"] = "Open online help",
                ["Choisissez la langue, organisez le ruban et gérez vos informations générales."] = "Choose the language, organize the ribbon, and manage your general information.",
                ["Langue"] = "Language",
                ["Général"] = "General",
                ["Ruban"] = "Ribbon",
                ["Panneaux"] = "Panels",
                ["Boutons du panneau sélectionné"] = "Buttons in the selected panel",
                ["Les modifications seront appliquées au prochain démarrage de Revit."] = "Changes will be applied the next time Revit starts.",
                ["Informations (optionnel)"] = "Information (optional)",
                ["Renseigne ton email si tu veux recevoir les infos importantes. Pour me contacter directement, passe par LinkedIn."] = "Enter your email if you would like to receive important news. To contact me directly, use LinkedIn.",
                ["Email (optionnel)"] = "Email (optional)",
                ["Prénom (optionnel)"] = "First name (optional)",
                ["Nom (optionnel)"] = "Last name (optional)",
                ["Ces informations restent modifiables à tout moment."] = "You can change this information at any time.",
                ["Une question, un bug ou une idée ?"] = "A question, bug, or idea?",
                ["BIMaestro est développé par une seule personne. Tes retours m’aident énormément à repérer ce qui ne fonctionne pas, à améliorer les outils existants et à choisir les prochaines évolutions."] = "BIMaestro is developed by one person. Your feedback helps me find issues, improve existing tools, and decide what to build next.",
                ["Même un message rapide peut faire la différence, alors n’hésite pas à me contacter :"] = "Even a quick message can make a difference, so feel free to contact me:",
                ["Annuler"] = "Cancel",
                ["Enregistrer"] = "Save",
                ["Automatique"] = "Automatic",
                ["Contact"] = "Contact",
                ["Langue de l’interface"] = "Interface language",
                ["Choisissez la langue de BIMaestro. Le changement sera appliqué au prochain démarrage de Revit."] = "Choose the BIMaestro language. The change will be applied the next time Revit starts.",

                ["Outils de Visualisation"] = "Visualization Tools",
                ["Modification"] = "Modify",
                ["Outils IA"] = "AI Tools",
                ["Analyse"] = "Analysis",
                ["Spécifique aux familles"] = "Family Tools",
                ["Couleur et information"] = "Colors and Information",
                ["Sélection d'éléments"] = "Select Elements",
                ["Sélection\nd'éléments"] = "Select\nElements",
                ["Ouvrir la vue du Plan"] = "Open View or Sheet",
                [" Ouvrir \nla vue"] = " Open\nview",
                ["Export Nomenclature"] = "Export Schedule",
                ["Export de\nNomenclature"] = "Export\nSchedule",
                ["Sélection d'objet"] = "Select Similar",
                ["Sélection\nd'objet"] = "Select\nSimilar",
                ["Boutons de Visualisation"] = "Visualization Buttons",
                ["Face 3D"] = "Orient 3D",
                ["Peinture"] = "Paint",
                ["Maquette MEP"] = "MEP Model",
                ["Maquette\nMEP"] = "MEP\nModel",
                ["Auto Réservation"] = "Auto Openings",
                [" Auto \nRéservation"] = " Auto\nOpenings",
                ["Bride auto"] = "Auto Flanges",
                ["Bride\nauto"] = "Auto\nFlanges",
                ["Choix\nbride"] = "Choose\nflange",
                ["Suppression\nde brides"] = "Remove\nflanges",
                ["Dynamo Auto"] = "Dynamo Auto",
                ["Gestion Excel"] = "Excel Manager",
                ["Gestion\nExcel"] = "Excel\nManager",
                ["Phases rapides"] = "Quick Phases",
                ["Phases\nrapides"] = "Quick\nPhases",
                ["Outils rapides"] = "Quick Tools",
                ["Surcharges"] = "Overrides",
                ["Organisateur"] = "Organizer",
                ["Purge"] = "Cleanup",
                ["Chatbot + élément"] = "Chatbot + Element",
                ["Chatbot\n+ élément"] = "Chatbot\n+ Element",
                ["Correction de texte IA"] = "AI Text Editing",
                ["Correction \nde texte IA"] = "AI Text\nEditing",
                ["Audit texte IA"] = "AI Text Audit",
                ["Audit texte\nIA"] = "AI Text\nAudit",
                ["Rendu plan IA"] = "AI View Rendering",
                ["Rendu\nplan IA"] = "AI View\nRendering",
                ["Calcul des canalisations"] = "Pipe Calculation",
                ["Calcul des\ncanalisations"] = "Pipe\nCalculation",
                ["Qui a fait ça ??"] = "Who Did This?",
                ["Qui a\nfait ça ??"] = "Who Did\nThis?",
                ["Analyse de Poids"] = "Model Size Analysis",
                ["Analyse\nde Poids"] = "Model Size\nAnalysis",
                ["Temps par projet"] = "Time per Project",
                ["Temps par\nprojet"] = "Time per\nProject",
                ["Suivi\nmaquette"] = "Model\nTracking",
                ["Navigateur de Familles"] = "Family Browser",
                ["Navigateur\nde Familles"] = "Family\nBrowser",
                ["Convertir les paramètres partagés"] = "Convert Shared Parameters",
                ["Convertir\nparamètres"] = "Convert\nParameters",
                ["Outils familles"] = "Family Tools",
                ["Trad.IA"] = "AI Trans.",
                ["Traduction\nde vues IA"] = "AI View\nTranslation",
                ["Unités"] = "Units",
                ["Import\nd'unité"] = "Import\nUnits",
                ["Changement de couleur"] = "Color Controls",
                ["Couleurs"] = "Colors",
                ["Clic : ouvre la personnalisation des couleurs. Double-clic : active ou désactive les panneaux colorés."] = "Click: opens color customization. Double-click: enables or disables colored panels.",
                ["Impossible d’ouvrir la personnalisation des couleurs : "] = "Unable to Open Color Customization: ",
                ["couleur\nOui/Non"] = "Colors\nOn/Off",
                ["Couleur\nOui/Non"] = "Colors\nOn/Off",
                ["Couleur reset"] = "Reset Colors",
                ["papa\nNoël"] = "Christmas\nMode",
                ["Personnaliser les couleurs"] = "Customize Colors",
                ["Panneaux colorés actifs"] = "Colored Panels Enabled",
                ["Colorer le panneau complet"] = "Color the Full Panel",
                ["Décoché : bandeau de titre seulement. L’activation générale correspond au double-clic sur Couleurs."] = "Unchecked: title bar only. The main enable switch matches double-clicking Colors.",
                ["Palette"] = "Palette",
                ["Exemple"] = "Example",
                ["Infos empilées"] = "Information Buttons",
                ["Note"] = "News",
                ["Option"] = "Options",
                ["Rosace\nBoutons"] = "Button\nWheel",
                ["Soutenir"] = "Support",

                ["Met en évidence et filtre les éléments de catégories choisies.\r\nRegroupe automatiquement les éléments similaires pour accélérer la sélection et les actions répétitives."] = "Highlights and filters elements from selected categories.\r\nAutomatically groups similar elements to speed up selection and repeated actions.",
                ["Passe rapidement de la vue active à la feuille associée (et inversement).\r\nPermet aussi d'ouvrir une vue directement depuis un viewport sélectionné sur une feuille."] = "Quickly switches between the active view and its sheet.\r\nYou can also open a view directly from a viewport selected on a sheet.",
                ["Exporte les nomenclatures Revit sélectionnées en fichier Excel ou PDF."] = "Exports selected Revit schedules to Excel or PDF.",
                ["Génère automatiquement les miniatures manquantes ou obsolètes du projet, une vue à la fois. Affiche la progression et permet de mettre en pause, reprendre ou arrêter le traitement sans changer la vue active."] = "Automatically generates missing or outdated project thumbnails, one view at a time. Shows progress and lets you pause, resume, or stop processing without changing the active view.",
                ["Déplace précisément les objets entre deux points sans les dissocier ni les recréer.\r\nAnnule intégralement l'opération si Revit détecte une contrainte ou un risque pour une étiquette ou une cotation."] = "Moves elements precisely between two points without disconnecting or recreating them.\r\nFully cancels the operation if Revit detects a constraint or a risk to a tag or dimension.",
                ["Ouvre un assistant IA conversationnel connecté à votre contexte Revit.\r\nAnalyse les éléments sélectionnés et répond selon le profil choisi (Basique, Revit, BIM Manager)."] = "Opens a conversational AI assistant connected to your Revit context.\r\nAnalyzes selected elements and responds according to the chosen profile (Basic, Revit, BIM Manager).",
                ["Corrige et reformule les textes Revit sélectionnés avec l'IA.\r\nPropose plusieurs styles et laisse valider, modifier ou ignorer chaque suggestion."] = "Corrects and rewrites selected Revit text with AI.\r\nOffers several styles and lets you accept, edit, or ignore each suggestion.",
                ["Analyse les textes des vues/feuilles sélectionnées pour détecter les fautes d'orthographe, de grammaire et de ponctuation. \r\n\r\nPourquoi ce bouton est utile :\r\n- évite les oublis avant envoi client,\r\n- classe les anomalies par gravité (Mineur / Erreur),\r\n- propose des corrections détaillées ligne par ligne.\r\n\r\nConseil : sélectionne seulement les vues/feuilles à contrôler pour accélérer l'analyse."] = "Analyzes text in selected views/sheets to detect spelling, grammar, and punctuation errors. \r\n\r\nWhy this tool is useful:\r\n- prevents omissions before sending to a client,\r\n- classifies issues by severity (Minor / Error),\r\n- provides detailed line-by-line corrections.\r\n\r\nTip: select only the views/sheets you want to check to speed up the analysis.",
                ["Génère un rendu réaliste à partir d'une vue Plan/Coupe/3D via gpt-image-2.\r\n\r\nCe que fait le bouton :\r\n- conserve le cadrage et la géométrie de la vue source,\r\n- optimise l'image avant envoi ,\r\n- crée une variante visuelle rapide pour présentation client.\r\n\r\nConseil : lancez-le sur une vue propre (annotations masquées) pour obtenir un résultat plus lisible."] = "Generates a realistic render from a plan/section/3D view using gpt-image-2.\r\n\r\nWhat this tool does:\r\n- preserves the framing and geometry of the source view,\r\n- optimizes the image before sending it,\r\n- creates a quick visual variant for client presentations.\r\n\r\nTip: run it on a clean view with annotations hidden for a clearer result.",
                ["Description :\r\n- Calcule les longueurs des canalisations et gaines par diamètre (DN ou dimensions).\r\n- Compte les accessoires de type coudes et tés par diamètre.\r\n- Estime les volumes d'eau par diamètre intérieur.\r\n- Intègre un filtre par type de système pour une analyse précise.\r\n- Permet d'inclure ou non les gaines dans les calculs.\r\n- Exporte les résultats sous forme de tableau Excel détaillé.\r\n\r\nUtilité :\r\nOptimisez votre gestion des systèmes MEP en obtenant rapidement une analyse précise des longueurs, volumes et accessoires, avec possibilité d'exportation."] = "Description:\r\n- Calculates pipe and duct lengths by diameter (DN or dimensions).\r\n- Counts elbows and tees by diameter.\r\n- Estimates water volume by internal diameter.\r\n- Includes a system type filter for precise analysis.\r\n- Lets you include or exclude ducts from calculations.\r\n- Exports results as a detailed Excel table.\r\n\r\nUse:\r\nQuickly analyze MEP system lengths, volumes, and accessories, with export support.",
                ["Fonctionnalités principales :\r\n1. **Analyse des Familles** :\r\n   - Taille de chaque famille (Mo).\r\n   - Nombre d'instances pour chaque famille.\r\n   - Classement par taille décroissante.\r\n\r\n2. **Analyse des Imports CAO** :\r\n   - Taille des imports (Mo).\r\n   - Types d'éléments analysés : Imports CAO, Lien Revit/IFC.\r\n\r\n3. **Export des Résultats** :\r\n   - Export vers un fichier Excel (RevitLogs/TailleFamilleRevit).\r\n   - Organisation claire par nom, type, taille et nombre d'instances.\r\n\r\nUtilité :\r\n- Identifier les éléments volumineux dans votre projet.\r\n- Optimiser la performance du modèle en réduisant les familles et les imports inutiles."] = "Main features:\r\n1. **Family Analysis**:\r\n   - Size of each family (MB).\r\n   - Number of instances for each family.\r\n   - Ranking by descending size.\r\n\r\n2. **CAD Import Analysis**:\r\n   - Import sizes (MB).\r\n   - Analyzed element types: CAD imports, Revit/IFC links.\r\n\r\n3. **Results Export**:\r\n   - Export to an Excel file (RevitLogs/TailleFamilleRevit).\r\n   - Clear organization by name, type, size, and instance count.\r\n\r\nUse:\r\n- Identify oversized elements in your project.\r\n- Improve model performance by reducing unnecessary families and imports.",
                ["Sélectionne des éléments similaires dans le projet"] = "Selects similar elements in the project.",
                ["Permet de réorienter une vue 3D active en fonction de la géométrie d'une face sélectionnée."] = "Reorients the active 3D view from the geometry of a selected face.",
                ["Exporte automatiquement plusieurs vues ou feuilles en DWG, en nommant chaque fichier selon le projet et la vue comme pour les PDF."] = "Exports multiple views or sheets to DWG and names each file from the project and view, like PDF exports.",
                ["Liste les matériaux (y compris peinture) appliqués à un élément."] = "Lists materials, including paint, applied to an element.",
                ["Crée des réservations automatiques"] = "Automatically creates openings.",
                ["Ajoute automatiquement des brides aux extrémités sélectionnées"] = "Automatically adds flanges to selected ends.",
                ["Permet de choisir la bride par défaut"] = "Lets you choose the default flange.",
                ["Permet de supprimer les brides"] = "Removes flanges and reconnects the network.",
                ["Configure les paramètres Dynamo"] = "Configures Dynamo settings.",
                ["Exporter ou importer une nomenclature au format Excel"] = "Exports or imports a schedule in Excel format.",
                ["Exporter ou importer une nomenclature au format Excel."] = "Exports or imports a schedule in Excel format.",
                ["Applique ou réinitialise rapidement la demi-teinte, la transparence ou le masquage des éléments sélectionnés dans les vues choisies. Si une feuille est sélectionnée, BIMaestro applique l’action aux vues placées sur cette feuille."] = "Quickly applies or resets halftone, transparency, or hiding for selected elements in the chosen views. If a sheet is selected, BIMaestro applies the action to the views placed on that sheet.",
                ["Renomme les éléments sélectionnés avec préfixes, suffixes et numérotation.\r\nSur une feuille, numérote les fenêtres de vue de haut en bas puis de gauche à droite.\r\nTrie aussi les éléments par niveau/emplacement et peut réinitialiser le paramètre texte ciblé."] = "Renames selected elements with prefixes, suffixes, and numbering.\r\nOn a sheet, numbers viewports from top to bottom, then left to right.\r\nIt can also sort elements by level/location and reset the targeted text parameter.",
                ["Supprime les vues non placées, les familles et les nomenclatures inutilisées afin d'alléger le projet.\r\nUne fenêtre permet de choisir précisément les éléments à purger avant exécution.\r\n"] = "Removes unplaced views, unused families, and unused schedules to reduce project bloat.\r\nA window lets you choose exactly what to clean before execution.\r\n",
                ["Modifie rapidement la phase de creation et la phase de demolition des objets selectionnes."] = "Quickly changes the created and demolished phases of selected objects.",
                ["Affiche le temps passé par projet."] = "Shows time spent per project.",
                ["Vérifie les éléments 3D sélectionnés pour détecter les incohérences."] = "Checks selected 3D elements for inconsistencies.",
                ["Parcourt vos dossiers de familles Revit et charge les contenus en quelques clics.\r\nInclut aperçu visuel, favoris, recherche et options d'affichage pour accélérer le travail."] = "Browses your Revit family folders and loads content in a few clicks.\r\nIncludes previews, favorites, search, and display options.",
                ["Convertit tous les paramètres partagés modifiables de la famille en paramètres de famille (même nom, même groupe et même type instance/type)."] = "Converts all editable shared parameters in a family to family parameters while preserving name, group, and instance/type settings.",
                ["Supprime les paramètres inutilisés d'une famille Revit après vérification des dépendances.\r\nCrée automatiquement une sauvegarde avant nettoyage."] = "Removes unused parameters from a Revit family after checking dependencies.\r\nAutomatically creates a backup before cleanup.",
                ["Active ou désactive les couleurs du projet (simple ou double clic)"] = "Enables or disables project colors (single or double click).",
                ["Réinitialise les couleurs appliquées"] = "Resets applied colors.",
                ["Choisit une couleur unie, un dégradé ou un thème prédéfini, ainsi que la couleur du texte de chaque panneau du ruban BIMaestro."] = "Chooses a solid color, gradient, or preset theme, plus the text color of each BIMaestro ribbon panel.",
                ["Page d'information sur le plugin"] = "Plugin information page.",
                ["Page de mise à jour"] = "Update notes.",
                ["Configurer le ruban BIMaestro et les paramètres utilisateur."] = "Configures the BIMaestro ribbon and user settings.",
                ["Ouvre le LinkedIn de Paul Lemert pour envoyer un retour, signaler un bouton qui bloque ou proposer une idée."] = "Opens Paul Lemert's LinkedIn page to send feedback, report an issue, or suggest an idea.",
                ["Rosace des 16 derniers boutons BIMaestro utilisés."] = "Wheel containing the 16 most recently used BIMaestro buttons.",
                ["Vous appréciez BIMaestro ? Ouvrez la page Ko-fi pour soutenir volontairement son développement autour d’un petit café."] = "Enjoying BIMaestro? Open the Ko-fi page to support its development with a coffee.",
                ["Exécuter {0}"] = "Run {0}",
                ["BIMaestro - Ajouter une commande"] = "BIMaestro - Add a Command",
                ["BIMaestro - Analyse en cours"] = "BIMaestro - Analysis in Progress",
                ["BIMaestro - Aperçu 3D famille"] = "BIMaestro - 3D Family Preview",
                ["BIMaestro — Bienvenue"] = "BIMaestro — Welcome",
                ["BIMaestro - Choisir la bride par défaut"] = "BIMaestro - Choose Default Flange",
                ["BIMaestro - Clash 3D"] = "BIMaestro - 3D Clash Check",
                ["BIMaestro - Configurer un bouton Dynamo"] = "BIMaestro - Configure a Dynamo Button",
                ["BIMaestro - Correction / Reformulation de Texte"] = "BIMaestro - Text Editing",
                ["BIMaestro - Créer des réservations"] = "BIMaestro - Create Openings",
                ["BIMaestro - Créer des réservations V2"] = "BIMaestro - Create Openings V2",
                ["BIMaestro - Export DWG par feuille"] = "BIMaestro - DWG Sheet Export",
                ["BIMaestro — Maquette BIM"] = "BIMaestro — BIM Model",
                ["BIMaestro - Mise à jour disponible"] = "BIMaestro - Update Available",
                ["BIMaestro - Mises à jour"] = "BIMaestro - Updates",
                ["BIMaestro - Mots-clés de toute la bibliothèque"] = "BIMaestro - Library Keywords",
                ["BIMaestro - Navigateur de Familles"] = "BIMaestro - Family Browser",
                ["BIMaestro - Nettoyage du projet"] = "BIMaestro - Project Cleanup",
                ["BIMaestro - Nouveautés"] = "BIMaestro - What's New",
                ["BIMaestro - Organisateur d'Éléments"] = "BIMaestro - Element Organizer",
                ["BIMaestro - Réservations Auto (V3)"] = "BIMaestro - Automatic Openings (V3)",
                ["BIMaestro - Résultat du Scan de Textes et Corrections (IA)"] = "BIMaestro - AI Text Audit Results",
                ["BIMaestro - Résultats de l'Analyse"] = "BIMaestro - Analysis Results",
                ["BIMaestro - Sélection de Vues/Feuilles"] = "BIMaestro - Select Views/Sheets",
                ["BIMaestro - Sélection des Familles"] = "BIMaestro - Select Families",
                ["BIMaestro - Sélection du Profil"] = "BIMaestro - Select Profile",
                ["BIMaestro - Sélectionner les paramètres à supprimer"] = "BIMaestro - Select Parameters to Remove",
                ["BIMaestro - Surcharges vues"] = "BIMaestro - View Overrides",
                ["BIMaestro — Temps par type de document"] = "BIMaestro — Time by Document Type",
                ["Calcul des longueurs par diamètre"] = "Lengths by Diameter",
                ["Classement Jeux"] = "Game Leaderboard",
                ["Couleurs du ruban BIMaestro"] = "BIMaestro Ribbon Colors",
                ["Encore + · Couleurs de Revit"] = "Encore + · Revit Colors",
                ["Interaction réseaux - Calcul des canalisations"] = "Network Interaction - Pipe Calculation",
                ["Suivi maquettes collaboratif"] = "Collaborative Model Tracking",
                ["BIMaestro - Miniatures des vues"] = "BIMaestro - View Thumbnails",
                ["Miniature"] = "Thumbnail",
                ["Miniatures des vues"] = "View Thumbnails",
                ["Vues à traiter"] = "Views to Process",
                ["Miniatures manquantes et obsolètes"] = "Missing and Outdated Thumbnails",
                ["Miniatures manquantes uniquement"] = "Missing Thumbnails Only",
                ["Toutes les miniatures"] = "All Thumbnails",
                ["Dans ce dossier"] = "In this folder",
                ["Trouvé dans"] = "Found in",
                ["Autre dossier"] = "Other folder",
                ["Les gabarits, vues internes, nomenclatures non graphiques et vues non imprimables sont ignorés."] = "Templates, internal views, non-graphical schedules, and non-printable views are ignored.",
                ["Démarrer"] = "Start",
                ["Prêt à démarrer."] = "Ready to start.",
                ["Vue : —"] = "View: —",
                ["Écoulé : —"] = "Elapsed: —",
                ["Restant estimé : —"] = "Estimated remaining: —",
                ["Échecs : 0"] = "Failures: 0",
                ["Une vue est exportée à la fois pendant l’inactivité de Revit. Pause et arrêt sont pris en compte après la capture en cours. Les miniatures déjà terminées restent enregistrées."] = "One view is exported at a time while Revit is idle. Pause and stop take effect after the current capture. Completed thumbnails remain saved.",
                ["Pause"] = "Pause",
                ["Reprendre"] = "Resume",
                ["Créer des réservations"] = "Create Openings",
                ["Créer des réservations V2"] = "Create Openings V2",
                ["Choisissez le type d’objet et la famille ; les options avancées restent discrètes."] = "Choose the object type and family; advanced options remain unobtrusive.",
                ["Choisissez le type d’objet et la famille V2 (rectangulaire ou circulaire)."] = "Choose the V2 object type and family (rectangular or round).",
                ["Choisissez la famille utilisée pour créer les réservations."] = "Choose the family used to create openings.",
                ["Choisissez la famille V2 (rectangulaire ou circulaire)."] = "Choose the V2 family (rectangular or round).",
                ["Choisissez si la réservation est créée dans un mur ou un sol."] = "Choose whether the opening is created in a wall or floor.",
                ["Choisissez si les canalisations à sélectionner sont dans la maquette ou dans un lien."] = "Choose whether the pipes to select are in the model or a link.",
                ["Support :"] = "Host:",
                ["Support"] = "Host",
                ["Source canalisations :"] = "Pipe source:",
                ["Famille de réservation :"] = "Opening family:",
                ["Famille V2 :"] = "V2 family:",
                ["Sélectionnez le type d’objet concerné."] = "Select the object type.",
                ["Appliquer la norme (incréments de 50 mm)"] = "Apply the standard (50 mm increments)",
                ["Appliquer la norme (arrondis)"] = "Apply the standard (rounding)",
                ["Arrondit les dimensions à des pas de 50 mm."] = "Rounds dimensions to 50 mm increments.",
                ["Exécuter le script Dynamo automatiquement"] = "Run the Dynamo script automatically",
                ["Lance automatiquement le script Dynamo associé."] = "Automatically runs the associated Dynamo script.",
                ["Multi-sélection canalisations (rectangle)"] = "Multi-select pipes (rectangle)",
                ["Uniquement pour les familles rectangulaires (comme V1)."] = "Only for rectangular families (as in V1).",
                ["Mur = familles verticales / Sol = familles horizontales."] = "Wall = vertical families / Floor = horizontal families.",
                ["Activation du plugin"] = "Plugin Activation",
                ["Veuillez entrer votre clé d'activation :"] = "Enter your activation key:",
                ["Valider"] = "Validate",
                ["Choisir une couleur"] = "Choose a color",
                ["Couleurs rapides"] = "Quick Colors",
                ["Erreur"] = "Error",
                ["Exception"] = "Exception",
                ["Terminé"] = "Completed",
                ["Sélection"] = "Selection",
                ["Dashboard"] = "Dashboard",
                ["Brides"] = "Flanges",
                ["Brides manquantes"] = "Missing Flanges",
                ["Bride incompatible"] = "Incompatible Flange",
                ["Retrait de brides"] = "Remove Flanges",
                ["Importer unités"] = "Import Units",
                ["Erreur JSON"] = "JSON Error",
                ["Export Excel"] = "Excel Export",
                ["Import Excel"] = "Excel Import",
                ["Matériaux de l’élément"] = "Element Materials",
                ["Matériaux de l'élément"] = "Element Materials",
                ["Fermer"] = "Close",
                ["Appliquer"] = "Apply",
                ["Réinitialiser"] = "Reset",
                ["Supprimer"] = "Delete",
                ["Ajouter"] = "Add",
                ["Continuer"] = "Continue",
                ["Retour"] = "Back",
                ["Suivant"] = "Next",
                ["Tout sélectionner"] = "Select All",
                ["Tout désélectionner"] = "Clear Selection",
                ["Tout décocher"] = "Clear All",
                ["Parcourir…"] = "Browse…",
                ["Ouvrir le dossier"] = "Open Folder",
                ["Ouvrir dans le navigateur"] = "Open in Browser",
                ["Copier tout"] = "Copy All",
                ["Recherche"] = "Search",
                ["Détails"] = "Details",
                ["Données"] = "Data",
                ["Actions"] = "Actions",
                ["Action"] = "Action",
                ["Statut"] = "Status",
                ["Gravité"] = "Severity",
                ["Niveau"] = "Level",
                ["Catégorie"] = "Category",
                ["Famille"] = "Family",
                ["Maquette"] = "Model",
                ["Élément"] = "Element",
                ["Utilisateur"] = "User",
                ["Texte"] = "Text",
                ["Type d’objet :"] = "Object type:",
                ["Options avancées"] = "Advanced Options",
                ["Mode automatique"] = "Automatic Mode",
                ["Tout"] = "All",
                ["Aucun"] = "None",
                ["Autre"] = "Other",
                ["Choisir…"] = "Choose…",
                ["Forme"] = "Shape",
                ["Circulaire"] = "Round",
                ["Rectangulaire"] = "Rectangular",
                ["Canalisation"] = "Pipe",
                ["Gaine"] = "Duct",
                ["Mur"] = "Wall",
                ["Sol"] = "Floor",
                ["Porte"] = "Door",
                ["Réseau"] = "Network",
                ["Vannes"] = "Valves",
                ["Diamètre"] = "Diameter",
                ["Couleur"] = "Color",
                ["Légende"] = "Legend",
                ["Documentation"] = "Documentation",
                ["Charger la dernière version"] = "Download Latest Version",
                ["Sélection des vues et feuilles"] = "View and Sheet Selection",
                ["Temps par type de document"] = "Time by Document Type",
                ["Aperçu du panneau"] = "Panel Preview",
                ["Ajouter à la collection active"] = "Add to Active Collection",
                ["Mots-clés de recherche…"] = "Search keywords…",
                ["Arborescence du projet"] = "Project Browser",
                ["Rentrer dans la famille"] = "Open Family",
                ["Réinitialiser les filtres"] = "Reset Filters",
                ["Charger"] = "Load",
                ["Afficher"] = "Show",
                ["Masquer"] = "Hide",
                ["Bienvenue dans BIMaestro"] = "Welcome to BIMaestro",
                ["Un plugin Revit qui avance avec les vrais retours du terrain."] = "A Revit plugin shaped by real-world feedback.",
                ["EN ÉVOLUTION"] = "ALWAYS",
                ["continue"] = "EVOLVING",
                ["Ce que BIMaestro veut t'apporter"] = "What BIMaestro aims to give you",
                ["Moins de clics inutiles, des outils plus lisibles, et des corrections qui arrivent quand quelque chose bloque vraiment."] = "Fewer unnecessary clicks, clearer tools, and fixes focused on the issues that actually block your work.",
                ["Gagner du temps dans Revit"] = "Save time in Revit",
                ["Des commandes pratiques pour enlever les tâches répétitives du quotidien."] = "Practical commands that remove repetitive everyday tasks.",
                ["Comprendre vite les boutons"] = "Understand every tool quickly",
                ["Le guide permet de retrouver l'idée de chaque outil sans perdre du temps à deviner."] = "The guide explains what each tool does without making you guess.",
                ["Tes retours font avancer BIMaestro"] = "Your feedback moves BIMaestro forward",
                ["Je développe seul BIMaestro. Tes retours sont essentiels pour savoir ce qui fonctionne, ce qui manque, ce qui pourrait être amélioré et quelles idées te seraient vraiment utiles."] = "I develop BIMaestro on my own. Your feedback is essential for understanding what works, what is missing, what could be improved, and which ideas would truly help you.",
                ["Rester dans la boucle"] = "Stay in the loop",
                ["Laisse ton email si tu veux recevoir les infos importantes : correctifs, changements utiles, ou demande de retour sur une nouvelle idée. Pour me contacter directement, passe plutôt par LinkedIn."] = "Leave your email if you would like important news about fixes, useful changes, or new ideas. To contact me directly, LinkedIn is best.",
                ["Tu peux modifier ces infos plus tard depuis Option. Aucun souci si tu préfères passer."] = "You can change this information later from Options. It is completely fine to skip this step.",
                ["Contact direct"] = "Direct Contact",
                ["Merci d'aider BIMaestro à devenir plus utile."] = "Thank you for helping make BIMaestro more useful.",
                ["Non merci"] = "No Thanks",
                ["Plus tard"] = "Later",
                ["Oui, me tenir au courant"] = "Yes, Keep Me Updated",
                ["Jeu de feuilles :"] = "Sheet set:",
                ["Dossier d’export :"] = "Export folder:",
                ["Parcourir"] = "Browse",
                ["Exporter"] = "Export",
                ["Modifier les phases des objets sélectionnés"] = "Change the phases of selected objects",
                ["Phase de création"] = "Created phase",
                ["Phase de démolition"] = "Demolished phase",
                ["Les éléments incompatibles seront ignorés."] = "Incompatible elements will be skipped.",
                ["Nettoyage du projet"] = "Project Cleanup",
                ["Sélectionnez les opérations à réaliser. Une question proposera de créer « NomDuFichier - Purger.rvt » avant exécution."] = "Select the operations to run. You will be offered a safety copy named 'FileName - Cleanup.rvt' before execution.",
                ["Opérations de nettoyage"] = "Cleanup Operations",
                ["Astuce : commencez par les suppressions “safe”, puis terminez par la purge."] = "Tip: start with the safe deletion steps, then finish with the purge.",
                ["Étapes recommandées"] = "Recommended Steps",
                ["Supprimer les vues non placées sur feuille"] = "Delete Views Not Placed on Sheets",
                ["Supprime les vues (plans, coupes, 3D, détails, rendus) non placées et différentes de la vue active."] = "Deletes unplaced views (plans, sections, 3D, details, and renderings), except the active view.",
                ["Supprimer les nomenclatures inutilisées"] = "Delete Unused Schedules",
                ["Supprime les nomenclatures jamais placées qui contiennent « Interne » dans leur nom."] = "Deletes schedules that were never placed and contain 'Interne' in their name.",
                ["Purger les familles inutilisées (méthode douce)"] = "Purge Unused Families (Safe Method)",
                ["Liste et purge via la commande native Revit « Purger les éléments inutilisés »."] = "Lists and purges items through Revit's native Purge Unused command.",
                ["⚠ Méthode forte — à exécuter en dernier"] = "⚠ Aggressive Method — Run Last",
                ["Essaie de supprimer de force les familles chargées non utilisées (hors familles système usuelles). Peut provoquer des avertissements ou des échecs selon les dépendances."] = "Attempts to forcibly delete unused loaded families, excluding common system families. Dependencies may cause warnings or failures.",
                ["Purger les familles inutilisées (méthode forte)"] = "Purge Unused Families (Aggressive Method)",
                ["Supprime agressivement les familles non instanciées. À utiliser uniquement après les autres opérations."] = "Aggressively deletes families with no instances. Use only after the other operations.",
                ["Recommandé : lancez la méthode forte seulement après les étapes ci-dessus."] = "Recommended: run the aggressive method only after the steps above.",
                ["Lancer"] = "Run",
                ["Désélectionner tout"] = "Clear Selection",
                ["Exporter les résultats vers Excel"] = "Export Results to Excel",
                ["Filtrer par Type de système"] = "Filter by System Type",
                ["Inclure les gaines"] = "Include Ducts",
                ["Options de calcul"] = "Calculation Options",
                ["Sélectionnez les Types de système :"] = "Select System Types:",
                ["Réservations automatiques"] = "Automatic Openings",
                ["Si coché : exécution en mode automatique. Sinon : mode manuel."] = "If checked: run in automatic mode. Otherwise: manual mode.",
                ["Si coché : exécution en mode automatique (murs uniquement). Sinon : mode manuel."] = "If checked: run in automatic mode (walls only). Otherwise: manual mode.",
                ["Disponible pour canalisations (ou 'Autre') avec famille rectangulaire."] = "Available for pipes (or 'Other') with a rectangular family.",
                ["Choisissez le cas à configurer. Les familles BIMaestro restent disponibles en solution de secours."] = "Choose the case to configure. BIMaestro families remain available as a fallback.",
                ["Cibles"] = "Targets",
                ["Canalisation / gaine"] = "Pipe / Duct",
                ["Double lien"] = "Two Linked Models",
                ["Lien IFC/RVT : clique soit une cana/gaine locale, soit une cana/gaine du lien. Le mur à sélectionner sera automatiquement de l'autre maquette. Double lien limite les deux sélections aux liens ; le même lien peut contenir les deux éléments."] = "IFC/RVT link: select either a local pipe/duct or one from the link. The wall will automatically be selected from the other model. Two Linked Models restricts both selections to links; both elements may be in the same link.",
                ["Mode automatique (scan – mur uniquement)"] = "Automatic Mode (Scan — Walls Only)",
                ["Multi-sélection (canalisations – rectangle)"] = "Multiple Selection (Pipes — Rectangular)",
                ["Le mode automatique convient à la plupart des familles. Ajustez-le selon leur construction."] = "Automatic mode works for most families. Adjust it to match how the family was built.",
                ["Paramètres de dimensions"] = "Dimension Parameters",
                ["Longueur (axe du mur)"] = "Length (Wall Axis)",
                ["Profondeur"] = "Depth",
                ["Largeur"] = "Width",
                ["Hauteur"] = "Height",
                ["Sélectionnez un paramètre existant ou saisissez son nom."] = "Select an existing parameter or enter its name.",
                ["Placement vertical"] = "Vertical Placement",
                ["Référence verticale"] = "Vertical Reference",
                ["Décalage vertical (mm)"] = "Vertical Offset (mm)",
                ["Arrondi des dimensions"] = "Dimension Rounding",
                ["Activer l'arrondi aux 50 mm supérieurs par défaut"] = "Round Up to the Next 50 mm by Default",
                ["Arrondir aux 50 mm supérieurs"] = "Round Up to the Next 50 mm",
                ["Chaque dimension calculée est portée au multiple de 50 mm immédiatement supérieur. Aucun jeu de 50 mm n'est ajouté avant l'arrondi."] = "Each calculated dimension is rounded up to the next multiple of 50 mm. No additional 50 mm clearance is added before rounding.",
                ["Famille et type"] = "Family and Type",
                ["Les Modèles génériques compatibles sont affichés en premier, puis les autres catégories."] = "Compatible Generic Models are shown first, followed by other categories.",
                ["Votre famille de réservation"] = "Your Opening Family",
                ["Facultatif : utilisez cette zone uniquement si la famille n'est pas encore chargée dans le projet."] = "Optional: use this area only if the family is not already loaded in the project.",
                ["Importer une famille RFA"] = "Import an RFA Family",
                ["Charger dans le projet"] = "Load into Project",
                ["Enregistrer cette famille"] = "Save This Family",
                ["Enrichissement avec Dynamo"] = "Dynamo Enhancement",
                ["Option facultative : lance un script Dynamo après la création/mise à jour des réservations pour renseigner ou corriger automatiquement des paramètres. Vous pouvez utiliser votre propre script adapté à vos familles et à votre méthode projet."] = "Optional: run a Dynamo script after creating or updating openings to populate or correct parameters automatically. You can use your own script adapted to your families and project workflow.",
                ["Dynamo n'est pas nécessaire pour créer les réservations. Dans mon cas, je l'utilise pour ajouter automatiquement les hauteurs NGF dans la famille de réservation : c'est la raison pour laquelle cette option existe. Laissez-la désactivée si vous n'en avez pas besoin."] = "Dynamo is not required to create openings. This option exists because I use it to add elevation values automatically to opening families. Leave it disabled if you do not need it.",
                ["Chemin du script Dynamo (.dyn)"] = "Dynamo Script Path (.dyn)",
                ["Exécuter Dynamo automatiquement"] = "Run Dynamo Automatically",
                ["Exécuter le script Dynamo par défaut"] = "Run the Default Dynamo Script",
                ["Enregistrer les réglages"] = "Save Settings",
                ["Exécution"] = "Run",
                ["Lancer la réservation"] = "Create Openings",
                ["Réglages"] = "Settings",
                ["Familles"] = "Families",
                ["Variante"] = "Variant",
                ["Avec hôte"] = "Hosted",
                ["Sans hôte"] = "Unhosted",
                ["Centre"] = "Center",
                ["Bas"] = "Bottom",
                ["Haut"] = "Top",
                ["Affiche l’aperçu, le nom, la description et les mots-clés proposés pour chaque famille. Vous pouvez tout corriger avant l’enregistrement."] = "Shows the preview, name, description, and suggested keywords for each family. You can edit everything before saving.",
                ["Ajouter/retirer des Favoris"] = "Add/Remove Favorites",
                ["Aperçu 3D de la famille"] = "3D Family Preview",
                ["Après une recherche sans résultat, appuie sur Entrée pour mémoriser le terme. BIMaestro pourra ensuite proposer de l’associer à la famille finalement ouverte."] = "After a search with no results, press Enter to remember the term. BIMaestro can then suggest associating it with the family you eventually open.",
                ["Arrière-plan de l'arborescence"] = "Browser Background",
                ["Arrière-plan des familles"] = "Family Background",
                ["Arrière-plan des panneaux"] = "Panel Background",
                ["Arrière-plan général"] = "Main Background",
                ["Astuces de recherche"] = "Search Tips",
                ["Collection :"] = "Collection:",
                ["Charger la collection"] = "Load Collection",
                ["Comportement"] = "Behavior",
                ["Couleur du bas"] = "Bottom Color",
                ["Couleur du haut"] = "Top Color",
                ["Couleurs secondaires"] = "Secondary Colors",
                ["Date de MAJ"] = "Updated On",
                ["Dernière mise à jour"] = "Last Updated",
                ["doc:oui : familles avec documentation"] = "doc:oui: families with documentation",
                ["Dossier"] = "Folder",
                ["Dossier familles"] = "Family Folder",
                ["Dossier miroir"] = "Mirror Folder",
                ["dossier:skid : chercher uniquement dans les dossiers contenant SKID"] = "dossier:skid: search only folders containing SKID",
                ["Dossiers"] = "Folders",
                ["Éléments affichés : "] = "Items shown: ",
                ["Éléments dans la collection : "] = "Items in collection: ",
                ["En mode Tout, tu peux chercher dans toute la bibliothèque. Écris simplement un nom, ou ajoute un mot-clé pour préciser ce que tu veux trouver."] = "In All mode, you can search the entire library. Enter a name or add a keyword to narrow your search.",
                ["Enrichir toute la bibliothèque"] = "Enrich Entire Library",
                ["Exemples utiles"] = "Useful Examples",
                ["Export d'aperçus 3D"] = "3D Preview Export",
                ["Favoris"] = "Favorites",
                ["image:non : familles sans image d'aperçu"] = "image:non: families without a preview image",
                ["Lancer l'export 3D"] = "Start 3D Export",
                ["MAJ"] = "Updated",
                ["maj : familles modifiées récemment"] = "maj: recently modified families",
                ["Mode sombre"] = "Dark Mode",
                ["Modifier les chemins…"] = "Edit Paths…",
                ["Mots-clés intelligents"] = "Smart Keywords",
                ["new : familles ajoutées récemment"] = "new: recently added families",
                ["Nouv."] = "New",
                ["Numéro OmniClass"] = "OmniClass Number",
                ["Ouvrir l’assistant…"] = "Open Assistant…",
                ["Ouvrir le sous-dossier"] = "Open Subfolder",
                ["Paramètres"] = "Settings",
                ["Paramètres d'apparence"] = "Appearance Settings",
                ["Partager"] = "Share",
                ["Partager la collection…"] = "Share Collection…",
                ["Rechercher dans le dossier…"] = "Search This Folder…",
                ["Rechercher dans toute la bibliothèque indexée. Exemples : new, maj, rvt:2024, dossier:skid, doc:oui."] = "Search the entire indexed library. Examples: new, maj, rvt:2024, dossier:skid, doc:oui.",
                ["Rechercher uniquement dans le dossier ouvert"] = "Search Only the Open Folder",
                ["Ren."] = "Rename",
                ["Retirer de la collection courante"] = "Remove from Current Collection",
                ["rvt:2024 : familles enregistrées en Revit 2024"] = "rvt:2024: families saved with Revit 2024",
                ["Sélection multiple"] = "Multiple Selection",
                ["Sélectionne le dossier de familles à traiter puis le dossier miroir pour les PNG exportés."] = "Select the family folder to process, then the mirror folder for exported PNG files.",
                ["Suppr."] = "Delete",
                ["Taille des vignettes"] = "Thumbnail Size",
                ["Taille du fichier"] = "File Size",
                ["Télécharger la collection…"] = "Download Collection…",
                ["Toujours au-dessus"] = "Always on Top",
                ["Tu peux combiner : pompe rvt:2024 doc:oui"] = "You can combine filters: pump rvt:2024 doc:oui",
                ["Une petite faute peut être corrigée automatiquement si aucun résultat exact n’existe."] = "A small typo can be corrected automatically when there is no exact result.",
                ["Version Revit"] = "Revit Version",
                ["Vue détaillée"] = "Detailed View",
                ["Mots-clés intelligents de la bibliothèque"] = "Smart Library Keywords",
                ["Cochez les familles à analyser. La description et les mots-clés participent tous les deux à la recherche. Les propositions de l’IA restent modifiables et ne sont enregistrées qu’avec le bouton Enregistrer les modifications."] = "Select the families to analyze. Both descriptions and keywords are used in search. AI suggestions remain editable and are saved only when you click Save Changes.",
                ["Tout cocher"] = "Select All",
                ["Sans mots-clés"] = "Without Keywords",
                ["Proposer avec l’IA pour les familles cochées"] = "Generate with AI for Selected Families",
                ["Arrêter"] = "Stop",
                ["Filtrer"] = "Filter",
                ["Traiter"] = "Process",
                ["Aperçu"] = "Preview",
                ["Aucun aperçu"] = "No Preview",
                ["Description recherchable proposée"] = "Suggested Searchable Description",
                ["Mots-clés proposés"] = "Suggested Keywords",
                ["Enregistrer les modifications"] = "Save Changes",
                ["Chargement..."] = "Loading...",
                ["Résultats de l'analyse"] = "Analysis Results",
                ["Double-cliquez une ligne pour sélectionner les éléments dans Revit."] = "Double-click a row to select the elements in Revit.",
                ["Nom"] = "Name",
                ["Type"] = "Type",
                ["Taille (Mo)"] = "Size (MB)",
                ["Instances"] = "Instances",
                ["Analyse en cours"] = "Analysis in Progress",
                ["Sélection des paramètres"] = "Parameter Selection",
                ["Sélectionnez les paramètres à supprimer, puis validez pour lancer le nettoyage."] = "Select the parameters to delete, then confirm to start cleanup.",
                ["Paramètres disponibles"] = "Available Parameters",
                ["Nom du paramètre"] = "Parameter Name",
                ["0 / 0 anomalies"] = "0 / 0 issues",
                ["À corriger"] = "To Fix",
                ["À ignorer"] = "Ignore",
                ["À revoir"] = "To Review",
                ["À vérifier"] = "Check",
                ["Actif"] = "Active",
                ["Actives"] = "Active",
                ["Afficher toutes les erreurs"] = "Show All Issues",
                ["Ajouter commentaire"] = "Add Comment",
                ["Astuce : double-clic sur un groupe = voir le détail. Clic droit = filtres, statut, commentaire, miniature."] = "Tip: double-click a group to view details. Right-click for filters, status, comments, and thumbnails.",
                ["Aucun filtre actif"] = "No Active Filter",
                ["Aucune anomalie à afficher avec ces filtres."] = "No issues match these filters.",
                ["BETA - Lecture visuelle des anomalies, collisions, traversées et raccords ouverts."] = "BETA - Visual review of issues, clashes, penetrations, and open connectors.",
                ["Clash 3D"] = "3D Clash",
                ["Collisions liens / tuyaux"] = "Link / Pipe Clashes",
                ["Critiques"] = "Critical",
                ["Détail"] = "Details",
                ["Filtres avancés"] = "Advanced Filters",
                ["Focus + isoler"] = "Focus + Isolate",
                ["Focus 3D"] = "3D Focus",
                ["Focus auto sélection"] = "Auto-Focus Selection",
                ["Id"] = "ID",
                ["Lien"] = "Link",
                ["Marquer OK / Annuler OK"] = "Mark OK / Undo OK",
                ["Message"] = "Message",
                ["Miniatures"] = "Thumbnails",
                ["OK"] = "OK",
                ["Raccords ouverts"] = "Open Connectors",
                ["Rapport"] = "Report",
                ["Réinitialiser filtres"] = "Reset Filters",
                ["Retour groupes"] = "Back to Groups",
                ["Total"] = "Total",
                ["Toutes"] = "All",
                ["Traversées"] = "Penetrations",
                ["Voir ce groupe"] = "View This Group",
                ["Voir ce type d'erreur"] = "View This Issue Type",
                ["Voir cet élément"] = "View This Element",
                ["Voir les erreurs liées"] = "View Related Issues",
                ["Vue"] = "View",
                ["Vue visuelle"] = "Visual View",
                ["Tous"] = "All",
                ["Critique"] = "Critical",
                ["Info"] = "Info",
                ["Groupes intelligents"] = "Smart Groups",
                ["Anomalies"] = "Issues",
                ["Murs superposés"] = "Stacked Walls",
                ["Mur noyé dans sol"] = "Wall Embedded in Floor",
                ["Murs flottants"] = "Floating Walls",
                ["Collisions tuyaux/liens"] = "Pipe / Link Clashes",
                ["Traversée sans réservation"] = "Penetration Without Opening",
                ["Anomalie 3D"] = "3D Issue",
                ["Aperçu en cours"] = "Preview in Progress",
                ["Aperçu non généré"] = "Preview Not Generated",
                ["Afficher toutes"] = "Show All",
                ["Bleu = Répétition"] = "Blue = Repetition",
                ["Légende :"] = "Legend:",
                ["Masquer répétitions"] = "Hide Repetitions",
                ["Orange = Correction mineure"] = "Orange = Minor Correction",
                ["Résultat du Scan de Textes et Corrections (IA)"] = "Text Scan and AI Correction Results",
                ["Rouge = Erreur"] = "Red = Error",
                ["   Créateur : "] = "   Creator: ",
                ["   Revit : "] = "   Revit: ",
                ["Filtrez rapidement par utilisateur, version Revit et type de fichier pour ouvrir le bon dossier de travail."] = "Quickly filter by user, Revit version, and file type to open the correct working folder.",
                ["Modifier chemin"] = "Change Path",
                ["Ouvrir dossier"] = "Open Folder",
                ["Recherche maquette :"] = "Search Models:",
                ["Suivi des maquettes collaboratives"] = "Collaborative Model Tracking",
                ["Type fichier :"] = "File Type:",
                ["Utilisateur :"] = "User:",
                ["Utilisateur : "] = "User: ",
                ["Version Revit :"] = "Revit Version:",
                ["Maquette inconnue"] = "Unknown Model",
                ["Projet sans nom"] = "Unnamed Project",
                ["Chemin non disponible"] = "Path unavailable",
                ["Inconnue"] = "Unknown",
                ["0 / 0 évènements affichés"] = "0 / 0 events displayed",
                ["Aperçu suppression rapide avec boîte estimative"] = "Quick deletion preview with estimated box",
                ["BETA - Lecture visuelle des suppressions, déplacements, créations et clusters de la maquette."] = "BETA - Visual review of deletions, moves, creations, and model clusters.",
                ["Capture le maillage réel des suppressions futures"] = "Capture the actual mesh of future deletions",
                ["Charger jour"] = "Load Day",
                ["Créations"] = "Creations",
                ["Date"] = "Date",
                ["Déplacements"] = "Moves",
                ["Détaillé"] = "Detailed",
                ["Détails évènement"] = "Event Details",
                ["Evènements"] = "Events",
                ["Focus"] = "Focus",
                ["Focus sélection"] = "Focus Selection",
                ["Id / Groupe"] = "ID / Group",
                ["Jour"] = "Day",
                ["Maillage suppression"] = "Deletion Mesh",
                ["Nettoyer previews"] = "Clean Previews",
                ["Ouvrir l'aide en ligne"] = "Open Online Help",
                ["Portée"] = "Scope",
                ["Position / Delta"] = "Position / Delta",
                ["Restaurer"] = "Restore",
                ["Simple"] = "Simple",
                ["Suppressions"] = "Deletions",
                ["Transaction"] = "Transaction",
                ["Utilisateurs"] = "Users",
                ["Visualiser"] = "Visualize",
                ["Visualiser sélection"] = "Visualize Selection",
                ["Voir ce type"] = "View This Type",
                ["Voir cet utilisateur"] = "View This User",
                ["Voir cette action"] = "View This Action",
                ["Voir cette famille"] = "View This Family",
                ["Affichage"] = "Display",
                ["Comparer"] = "Compare",
                ["Ctrl+F. Échap pour effacer. Accent-insensible."] = "Ctrl+F. Escape to clear. Accent-insensitive.",
                ["Exporter PNG"] = "Export PNG",
                ["Familles (.rfa)"] = "Families (.rfa)",
                ["Heures sur la période"] = "Hours During the Period",
                ["Légende Revit :"] = "Revit Legend:",
                ["Maquettes (.rvt)"] = "Models (.rvt)",
                ["Moyenne / jour"] = "Average / Day",
                ["Ouvrir Excel"] = "Open Excel",
                ["Ouvrir l'emplacement"] = "Open Location",
                ["Période"] = "Period",
                ["Projets sélectionnés"] = "Selected Projects",
                ["Recherche rapide"] = "Quick Search",
                ["Tapez pour trouver un projet/famille et ouvrir son dossier sans impacter les graphiques."] = "Type to find a project/family and open its folder without affecting the charts.",
                ["Top N"] = "Top N",
                ["YTD"] = "YTD",
                ["Temps passé"] = "Time Spent",
                ["Autres"] = "Others",
                ["Commencez à taper pour afficher des raccourcis"] = "Start typing to display shortcuts",
                ["Version inconnue"] = "Unknown Version",
                ["  courir / descendre en vol   •   "] = "  run / descend while flying   •   ",
                ["  fluides MEP   •   "] = "  MEP fluids   •   ",
                ["  ouvrir / fermer une porte   •   "] = "  open / close a door   •   ",
                ["  réapparaître"] = "  respawn",
                ["  s’accroupir   •   "] = "  crouch   •   ",
                ["  sauter   •   "] = "  jump   •   ",
                ["  se déplacer   •   "] = "  move   •   ",
                ["  voler   •   "] = "  fly   •   ",
                ["0 actif"] = "0 active",
                ["0 élément"] = "0 elements",
                ["ACTIONS RAPIDES"] = "QUICK ACTIONS",
                ["Afficher la branche complète"] = "Show Full Branch",
                ["Analyse des canalisations"] = "Pipe Analysis",
                ["ARRIVÉES"] = "INLETS",
                ["ARRIVÉES ET RETOURS"] = "INLETS AND RETURNS",
                ["Aucune anomalie ne correspond aux filtres."] = "No issue matches the filters.",
                ["Aucune source active. Sélectionne la canalisation d'arrivée principale et indique son sens."] = "No active source. Select the main inlet pipe and specify its direction.",
                ["Choisir le sens"] = "Choose Direction",
                ["Choisir une branche aval :"] = "Choose a Downstream Branch:",
                ["Choisis un scénario ou saisis un nouveau nom"] = "Choose a scenario or enter a new name",
                ["Clic gauche : sélectionner / regarder  •  Clic droit : action vanne"] = "Left click: select / look  •  Right click: valve action",
                ["Commandes"] = "Controls",
                ["Comprendre et suivre le réseau"] = "Understand and Trace the Network",
                ["Diagnostics"] = "Diagnostics",
                ["Effacer"] = "Clear",
                ["ÉLÉMENTS INSPECTÉS"] = "INSPECTED ELEMENTS",
                ["ESPACE"] = "SPACE",
                ["FLUIDES MEP"] = "MEP FLUIDS",
                ["Fluides"] = "Fluids",
                ["Flux"] = "Flow",
                ["Flux lumineux : alimenté  •  Gris : isolé  •  Ambre : indéterminé\nVannes : anneau vert ouverte  •  croix rouge fermée  •  ambre incertaine  •  blanc sélectionnée"] = "Bright flow: supplied  •  Gray: isolated  •  Amber: undetermined\nValves: green ring open  •  red cross closed  •  amber uncertain  •  white selected",
                ["Historique (0)"] = "History (0)",
                ["Informations Revit"] = "Revit Information",
                ["Initialisation du moteur 3D…"] = "Initializing the 3D Engine…",
                ["Le déplacement restera verrouillé jusqu’au chargement complet."] = "Movement will remain locked until loading is complete.",
                ["Les flux sont désactivés."] = "Flows are disabled.",
                ["Les repères colorés sont affichés directement sur la canalisation. Survole un choix pour prévisualiser la flèche."] = "Colored markers are displayed directly on the pipe. Hover over a choice to preview the arrow.",
                ["MAQUETTE BIM"] = "BIM MODEL",
                ["MARCHE"] = "WALK",
                ["Outils avancés"] = "Advanced Tools",
                ["Pointe un objet puis effectue un clic gauche."] = "Point at an object, then left-click.",
                ["Pourquoi ce sens ?"] = "Why This Direction?",
                ["PRÉPARATION DE LA MAQUETTE BIM"] = "PREPARING THE BIM MODEL",
                ["Quitter  ×"] = "Exit  ×",
                ["Quitter le suivi"] = "Stop Tracing",
                ["Réglages et analyses  +"] = "Settings and Analysis  +",
                ["Réinitialiser :"] = "Reset:",
                ["Réinitialiser ce système"] = "Reset This System",
                ["Rejoindre"] = "Go To",
                ["Rétablir"] = "Redo",
                ["Retirer ce retour"] = "Remove This Return",
                ["Retirer cette arrivée"] = "Remove This Inlet",
                ["RETOURS"] = "RETURNS",
                ["SCÉNARIOS"] = "SCENARIOS",
                ["Sources / sens"] = "Sources / Directions",
                ["SUIVRE LE RÉSEAU"] = "TRACE THE NETWORK",
                ["Suivre vers l’aval"] = "Trace Downstream",
                ["Suivre vers la source"] = "Trace to Source",
                ["SYSTÈMES AFFICHÉS"] = "DISPLAYED SYSTEMS",
                ["Tout afficher, y compris les extrémités probablement légitimes"] = "Show All, Including Probably Legitimate Endpoints",
                ["ZQSD / WASD"] = "WASD / ZQSD",
                ["LÉGENDE"] = "LEGEND",
                ["＋ Ajouter"] = "＋ Add",
                ["▾  Plans d’étage"] = "▾  Floor Plans",
                ["▾  Vues (tout)"] = "▾  Views (All)",
                ["↻ Actualiser"] = "↻ Refresh",
                ["↻ Noms fictifs"] = "↻ Sample Names",
                ["★ Vue active"] = "★ Active View",
                ["01  Apparence"] = "01  Appearance",
                ["02  Mode de coloration"] = "02  Coloring Mode",
                ["Accent et recherche"] = "Accent and Search",
                ["Activez la personnalisation pour l’utiliser."] = "Enable Customization to Use It.",
                ["Afficher le repère"] = "Show Marker",
                ["Ajoute un fond pastel et un repère coloré aux vues visibles. Les dossiers et les feuilles gardent leur apparence normale."] = "Adds a Pastel Background and Colored Marker to Visible Views. Folders and Sheets Keep Their Normal Appearance.",
                ["Ajouter une règle de catégorie"] = "Add Category Rule",
                ["Aperçu désactivé"] = "Preview Disabled",
                ["APERÇU EN DIRECT"] = "LIVE PREVIEW",
                ["Appliquer la couleur au"] = "Apply Color To",
                ["Associez librement un nom de rangement comme APD, PC ou PRO à une couleur. La règle s’applique au dossier et à toutes les vues placées dessous."] = "Assign a Folder Name Such as APD, PC, or PRO to a Color. The Rule Applies to the Folder and All Views Below It.",
                ["Autres vues"] = "Other Views",
                ["Choisissez le décor, puis ajustez les couleurs dans l’aperçu."] = "Choose the Style, Then Adjust the Colors in the Preview.",
                ["Choisissez une seule logique de travail. En mode combiné, les catégories personnelles sont prioritaires sur les types de vues."] = "Choose One Workflow. In Combined Mode, Custom Categories Take Priority over View Types.",
                ["Coloration des lignes"] = "Row Coloring",
                ["Colorer aussi les parents"] = "Also Color Parent Items",
                ["Composez son apparence et contrôlez immédiatement le résultat dans l’aperçu."] = "Compose Its Appearance and Check the Result Immediately in the Preview.",
                ["Couleur d’arrivée"] = "End Color",
                ["Couleur de départ"] = "Start Color",
                ["Couleur du parent"] = "Parent Color",
                ["Couleur du texte"] = "Text Color",
                ["Couleurs selon le type de vue"] = "Colors by View Type",
                ["Couleurs selon mes catégories"] = "Colors by My Categories",
                ["Coupes"] = "Sections",
                ["Désactive toute personnalisation et restaure l’apparence native de l’arborescence."] = "Disables All Customization and Restores the Native Browser Appearance.",
                ["Détection automatique dans l’organisation actuelle des vues Revit."] = "Automatic Detection in the Current Revit View Organization.",
                ["Disponible lorsque le mode dégradé est activé"] = "Available When Gradient Mode Is Enabled",
                ["Élévations"] = "Elevations",
                ["Encore + · Tout Revit"] = "More + · All Revit",
                ["Enregistrer le profil"] = "Save Profile",
                ["Exemple : PC, PRO, APD ou Exécution"] = "Example: PC, PRO, APD, or Construction",
                ["Exemple : Phases projet ou Standard agence"] = "Example: Project Phases or Office Standard",
                ["Fond principal"] = "Main Background",
                ["Générer un nouvel exemple de vues"] = "Generate a New View Sample",
                ["Le thème remplit les lignes ci-dessous ; vous pouvez ensuite personnaliser chaque panneau."] = "The Theme Fills the Rows Below; You Can Then Customize Each Panel.",
                ["Les changements sont appliqués après Enregistrer. Revit 2023 conserve son arborescence native."] = "Changes Are Applied after Saving. Revit 2023 Keeps Its Native Browser.",
                ["Marque d’une étoile le parent visible le plus proche de la vue active."] = "Marks the Closest Visible Parent of the Active View with a Star.",
                ["Navigation depuis une feuille"] = "Navigation from a Sheet",
                ["Nom exact de la catégorie"] = "Exact Category Name",
                ["Nomenclatures"] = "Schedules",
                ["Nouveau profil"] = "New Profile",
                ["Personnalisation active"] = "Customization Enabled",
                ["Plans"] = "Plans",
                ["Priorité : catégorie personnelle → type de vue → fond général"] = "Priority: Custom Category → View Type → General Background",
                ["Profils enregistrés"] = "Saved Profiles",
                ["Recherche automatique"] = "Automatic Search",
                ["Recherche la vue d’un viewport ou d’une nomenclature sélectionnée sur une feuille."] = "Finds the View of a Selected Viewport or Schedule on a Sheet.",
                ["Repère de la vue active"] = "Active View Marker",
                ["Restaurer Revit"] = "Restore Revit",
                ["Ruban BIMaestro"] = "BIMaestro Ribbon",
                ["Sélection et navigation"] = "Selection and Navigation",
                ["Style du fond"] = "Background Style",
                ["Supprimer cette règle"] = "Delete This Rule",
                ["Supprimer le profil sélectionné"] = "Delete Selected Profile",
                ["Testez une couleur unie ou un dégradé horizontal, vertical ou diagonal. L’aperçu permet de vérifier immédiatement le fond et la lisibilité du texte."] = "Try a Solid Color or a Horizontal, Vertical, or Diagonal Gradient. The Preview Lets You Check the Background and Text Readability Immediately.",
                ["Thème prédéfini"] = "Preset Theme",
                ["Uni, horizontal, vertical ou diagonal"] = "Solid, Horizontal, Vertical, or Diagonal",
                ["Valeurs par défaut"] = "Default Values",
                ["Vue 3D · Coordination"] = "3D View · Coordination",
                ["Vues 3D"] = "3D Views",
                ["Mode"] = "Mode",
                ["Animé"] = "Animated",
                ["Sombre"] = "Dark",
                ["Contraste élevé"] = "High Contrast",
                ["Arc-en-ciel"] = "Rainbow",
                ["Océan"] = "Ocean",
                ["Coucher de soleil"] = "Sunset",
                ["Noël"] = "Christmas",
                ["France continue"] = "Continuous France",
                ["Pokéball douce"] = "Soft Poké Ball",
                ["Pokémon pixel"] = "Pixel Pokémon",
                ["Arc-en-ciel animé"] = "Animated Rainbow",
                ["Pastel animé"] = "Animated Pastel",
                ["Bulles pastel"] = "Pastel Bubbles",
                ["Vagues pastel"] = "Pastel Waves",
                ["Étoiles pastel"] = "Pastel Stars",
                ["Nuages doux"] = "Soft Clouds",
                ["Horizontal"] = "Horizontal",
                ["Vertical"] = "Vertical",
                ["Diagonal"] = "Diagonal",
                ["Noël festif"] = "Festive Christmas",
                ["Choisissez le format de numérotation des éléments."] = "Choose the Element Numbering Format.",
                ["Cliquez pour appliquer le renommage aux éléments sélectionnés."] = "Click to Rename the Selected Elements.",
                ["Cliquez pour réinitialiser le paramètre sélectionné des éléments (vide le contenu)."] = "Click to Reset the Selected Parameter on the Elements (Clears Its Content).",
                ["Cochez cette case pour trier les éléments par niveau avant d'appliquer le renommage."] = "Check This Box to Sort Elements by Level Before Renaming.",
                ["Entrez le texte qui sera ajouté après le numéro lors du renommage des éléments."] = "Enter the Text to Add after the Number When Renaming Elements.",
                ["Entrez le texte qui sera ajouté avant le numéro lors du renommage des éléments."] = "Enter the Text to Add before the Number When Renaming Elements.",
                ["Format de numérotation :"] = "Numbering Format:",
                ["Numéro de départ :"] = "Starting Number:",
                ["Organisateur d'éléments"] = "Element Organizer",
                ["Paramètre à modifier :"] = "Parameter to Modify:",
                ["Préfixe :"] = "Prefix:",
                ["Renommer"] = "Rename",
                ["Sélectionnez le paramètre texte que vous souhaitez modifier pour les éléments sélectionnés."] = "Select the Text Parameter to Modify for the Selected Elements.",
                ["Spécifiez le point de départ pour la numérotation."] = "Specify the Starting Point for Numbering.",
                ["Suffixe :"] = "Suffix:",
                ["Trier par niveau"] = "Sort by Level",
                ["Activer Encore + sur Revit"] = "Enable More + on Revit",
                ["Arrivée"] = "End",
                ["Choisissez un onglet et ses couleurs. Par défaut, seul le bandeau de titre est modifié ; activez « Panneau complet » pour étendre la couleur à toute sa surface."] = "Choose a Tab and Its Colors. By Default, Only the Title Bar Is Modified; Enable “Full Panel” to Extend the Color across Its Entire Surface.",
                ["Décoché : bandeau de titre uniquement. Coché : panneau complet."] = "Unchecked: Title Bar Only. Checked: Full Panel.",
                ["Décoché : seul le bandeau de titre est coloré. Coché : toute la surface du panneau est colorée."] = "Unchecked: Only the Title Bar Is Colored. Checked: the Entire Panel Surface Is Colored.",
                ["Départ"] = "Start",
                ["Efface toutes les personnalisations de l’onglet sélectionné et restaure l’apparence Revit."] = "Clears All Customizations from the Selected Tab and Restores the Revit Appearance.",
                ["Encore + · Tout le ruban Revit"] = "More + · Entire Revit Ribbon",
                ["Interrupteur général : désactive temporairement toutes les couleurs natives sans effacer les panneaux cochés."] = "Master Switch: Temporarily Disables All Native Colors without Clearing the Selected Panels.",
                ["Onglet Revit"] = "Revit Tab",
                ["Panneau complet"] = "Full Panel",
                ["Remettre cet onglet à zéro"] = "Reset This Tab",
                ["Vous pouvez continuer à travailler dans Revit : chaque modification est appliquée et enregistrée automatiquement."] = "You Can Continue Working in Revit: Every Change Is Applied and Saved Automatically.",
                ["Ajouter un script"] = "Add Script",
                ["Aperçu du texte sur le ruban"] = "Ribbon Text Preview",
                ["Bouton à configurer :"] = "Button to Configure:",
                ["Chemin complet du fichier .dyn sélectionné"] = "Full Path of the Selected .dyn File",
                ["Choisissez l'emplacement, renommez l'étiquette et sélectionnez le ou les scripts .dyn à lancer."] = "Choose the Location, Rename the Label, and Select the .dyn Script(s) to Run.",
                ["Ils seront exécutés dans l'ordre de la liste."] = "They Will Run in the Listed Order.",
                ["Parcourir..."] = "Browse...",
                ["Personnaliser un bouton Dynamo"] = "Customize a Dynamo Button",
                ["Scripts Dynamo à lancer :"] = "Dynamo Scripts to Run:",
                ["Texte affiché sur le ruban :"] = "Text Displayed on the Ribbon:",
                ["Utilisez Entrée pour forcer un retour à la ligne."] = "Press Enter to Force a Line Break.",
                ["À savoir"] = "Good to Know",
                ["Applique ou réinitialise rapidement la demi-teinte, la transparence ou le masquage des éléments sélectionnés dans les vues choisies."] = "Quickly Applies or Resets Halftone, Transparency, or Hiding for Selected Elements in the Chosen Views.",
                ["Appliquer la demi-teinte"] = "Apply Halftone",
                ["Masquer dans les vues"] = "Hide in Views",
                ["Options générales"] = "General Options",
                ["Si tu coches une feuille, BIMaestro applique les réglages aux vues placées sur cette feuille. La feuille elle-même n’est pas modifiée."] = "If You Select a Sheet, BIMaestro Applies the Settings to Views Placed on That Sheet. The Sheet Itself Is Not Modified.",
                ["Surcharges graphiques par vues"] = "Graphic Overrides by View",
                ["Transparence"] = "Transparency",
                ["Valeur appliquée aux éléments sélectionnés."] = "Value Applied to the Selected Elements.",
                ["Vue active"] = "Active View",
                ["Cette note ne sera plus affichée pour ce bouton dans cette version."] = "This Note Will No Longer Be Shown for This Button in This Version.",
                ["Nouveautés du bouton"] = "What's New for This Button",
                ["Ne plus afficher aujourd'hui"] = "Do Not Show Again Today",
                ["Aperçu intégré indisponible"] = "Embedded Preview Unavailable",
                ["Chargement…"] = "Loading…",
                ["Copier le lien"] = "Copy Link",
                ["Mettre à jour"] = "Update",
                ["Notes de mise à jour"] = "Release Notes",
                ["Ouverture dans votre navigateur par défaut…"] = "Opening in Your Default Browser…",
                ["E-mail : "] = "Email: ",
                ["LinkedIn : "] = "LinkedIn: ",
                ["Rechercher (famille) :"] = "Search (Family):",
                ["Sélection de la bride par défaut"] = "Default Flange Selection",
                ["Sélectionnez le type de bride à utiliser pour la session en cours."] = "Select the Flange Type to Use for the Current Session.",
                ["Tape pour filtrer les familles (ex : 'bri', 'collerette', 'pn16')"] = "Type to Filter Families (e.g. 'flange', 'collar', 'pn16')",
                ["Cliquez sur un réseau pour le sélectionner immédiatement dans Revit."] = "Click a Network to Select It Immediately in Revit.",
                ["Longueur totale (m)"] = "Total Length (m)",
                ["Réseaux détectés"] = "Detected Networks",
                ["Correction / reformulation IA"] = "AI Correction / Rephrasing",
                ["Générez 3 reformulations puis sélectionnez celle à appliquer."] = "Generate 3 Rephrasings, Then Select the One to Apply.",
                ["Instruction personnalisée :"] = "Custom Instruction:",
                ["Personnalisé"] = "Custom",
                ["Reformuler (3 propositions)"] = "Rephrase (3 Suggestions)",
                ["Style de reformulation :"] = "Rephrasing Style:",
                ["Texte corrigé / reformulé :"] = "Corrected / Rephrased Text:",
                ["Texte original :"] = "Original Text:",
                ["Veuillez sélectionner un profil :"] = "Select a Profile:",
                ["(ou clique dans la fenêtre)"] = "(or Click in the Window)",
                ["Appuie sur Espace pour commencer"] = "Press Space to Start",
                ["Évite les tuyaux !"] = "Avoid the Pipes!",
                ["Record : 0"] = "High Score: 0",
                ["Aucun score pour ce mode."] = "No Score for This Mode.",
                ["Meilleurs scores en Arcade"] = "Arcade High Scores",
                ["Meilleurs scores en Classic"] = "Classic High Scores",
                ["Meilleurs scores en Flappy Bird"] = "Flappy Bird High Scores",
                ["Meilleurs scores en Hardcore"] = "Hardcore High Scores",
                ["Mode : Arcade"] = "Mode: Arcade",
                ["Mode : Classic"] = "Mode: Classic",
                ["Mode : Flappy Bird"] = "Mode: Flappy Bird",
                ["Mode : Hardcore"] = "Mode: Hardcore",
                ["Scores enregistrés par mode"] = "Scores Saved by Mode",
                ["Bonus arcade intégré au plugin — score, modes, skins et classement."] = "Arcade Bonus Built into the Plugin — Score, Modes, Skins, and Leaderboard.",
                ["Espace = démarrer / restart • Flèches ou ZQSD = bouger • P = pause"] = "Space = Start / Restart • Arrows or WASD = Move • P = Pause",
                ["Map: 28×28"] = "Map: 28×28",
                ["Mode: Classic"] = "Mode: Classic",
                ["Score: 0"] = "Score: 0",
                ["Top Mode: 0 • Global: 0"] = "Mode Best: 0 • Global: 0",
                ["Voir le classement global"] = "View Global Leaderboard",
                ["Étendre à toute la maquette"] = "Extend to the Entire Model",
                ["Quand coché : collecte/sélection dans tout le projet, pas uniquement la vue active."] = "When Checked: Collects/Selects throughout the Project, Not Only the Active View.",
                ["Sélection des Familles et Sous-Familles"] = "Family and Subfamily Selection",
                ["Fenêtre"] = "Window",
                ["Hébergement"] = "Hosting",
                ["Couleur hexadécimale"] = "Hex Color",
                ["Personnaliser…"] = "Customize…",
                ["Terminer"] = "Finish",
                ["Envoyer"] = "Send",
                ["Transparent"] = "Transparent",
                ["Classement jeux"] = "Game Leaderboard",
                ["Joueur"] = "Player",
                ["Pluie"] = "Rain",
                ["Classement global"] = "Global Leaderboard",
                ["Objet"] = "Object",
                ["Source"] = "Source",
                ["Longueur"] = "Length",
                ["Longueur (axe du mur)"] = "Length (wall axis)",
                ["Mur - Rectangulaire"] = "Wall - Rectangular",
                ["Mur - Circulaire"] = "Wall - Circular",
                ["Sol - Rectangulaire"] = "Floor - Rectangular",
                ["Sol - Circulaire"] = "Floor - Circular"
            };

        internal static bool TryGetEnglish(string french, out string english)
        {
            return English.TryGetValue(french, out english);
        }
    }
}
