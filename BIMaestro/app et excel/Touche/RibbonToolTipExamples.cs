using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;

public partial class AppUI
{
    private static string GetToolTipExampleDescription(string id)
    {
        if (id == "CreateDemoProject" && IsDemoToursPresentation) id = "DemoTours";
        switch (id)
        {
            case "PipeLengthByDiameterV2": return "Regroupe les longueurs, accessoires et volumes par diamètre, puis exporte les résultats vers Excel.";
            case "Qui a fait ça ?": return "Retrouvez les créations, suppressions et déplacements enregistrés dans l’historique de la maquette.";
            case "AnalysePoidsButton": return "Repérez les familles et imports volumineux pour cibler les éléments à optimiser.";
            case "Temps par projet": return "Consultez le temps enregistré pour chacun de vos projets.";
            case "ElementHistoryHoverToggle": return "Activez une information sur le dernier changement connu après sélection d’un objet. L’option est désactivée au démarrage.";
            case "Suivi maquette collaboratif": return "Consultez le registre des maquettes et leurs ouvertures/fermetures, avec un dossier de stockage partagé ou local.";
            case "Clash 3D": return "Repérez les conflits entre réseaux, bâtiment et liens, puis examinez-les en 3D.";
            case "Sélection d'objet": return "Sélectionnez un élément de référence, puis retrouvez les éléments similaires dans le projet.";
            case "HighlightElementsByCategories": return "Partez d’un objet sélectionné, choisissez les familles/types et la portée, puis sélectionnez les éléments correspondants.";
            case "MepBooster": return "Activez la pastille MEP, survolez un angle pour prévisualiser la rotation, puis cliquez pour appliquer une action autorisée.";
            case "MaintenanceWorkbench": return "Préparez une intervention MEP : extraction d’un moteur, manœuvre avec opérateur ou retrait d’un filtre / actionneur. Repérez les obstacles et ajustez les cotes ; la procédure et les efforts restent à confirmer.";
            case "HomeAppearanceTest": return "Choisissez la présentation de l’accueil Revit et retrouvez vos projets, familles et, en option, leur historique.";
            case "PropertiesAppearance": return "Personnalisez les couleurs et l’organisation de la palette Propriétés, avec des profils généraux, par catégorie ou par type et un aperçu réversible.";
            case "PipeSystemColors": return "Colorez les canalisations et raccords selon les couleurs des types de systèmes dans la vue active.";
            case "RevitGameCommand": return "Explorez la vue 3D en maquette interactive : déplacez-vous, inspectez les objets et interagissez avec les portes et les vannes.";
            case "CodexChat": return "Créez ou modifiez des familles Revit avec Codex ou Claude Code, selon les autorisations du panneau. Bêta.";
            case "CommunityFamilies": return "Parcourez les familles partagées par catégorie et publiez vos propres familles depuis la bibliothèque commune.";
            case "GPTBotWindowButton": return "Discutez avec un assistant IA connecté au contexte Revit et aux éléments sélectionnés, selon le profil choisi.";
            case "TextCorrectionButton": return "Corrigez ou reformulez les textes sélectionnés ; validez, modifiez ou ignorez chaque suggestion.";
            case "ScanText": return "Contrôlez les textes des vues et feuilles choisies, repérez les anomalies et consultez les corrections proposées.";
            case "RealisticViewImage": return "Générez un rendu de présentation à partir d’une vue Plan, Coupe ou 3D. Préparez une vue propre et sans annotations.";
            case "FamilyBrowser": return "Parcourez vos dossiers de familles avec aperçus, recherche et favoris, puis chargez le contenu choisi.";
            case "Rosace": return "Ouvrez la rosace depuis un raccourci pour accéder rapidement à vos familles.";
            case "ConvertSharedToFamily": return "Dans l’éditeur de familles, convertissez les paramètres partagés modifiables en paramètres de famille, en conservant leur nom, groupe et mode instance/type.";
            case "PurgeFamilyParameters": return "Nettoyez les paramètres inutilisés de la famille après vérification des dépendances, avec une sauvegarde préalable.";
            case "Familytraduction": return "Traduisez en français les noms des paramètres utilisateur de la famille ; les noms déjà français sont conservés.";
            case "FamilyViewtraduction": return "Traduisez en français les noms des vues de la famille en conservant les noms déjà français et leur unicité.";
            case "Export d'unité": return "Sauvegardez les unités et leur précision dans le fichier de préférences JSON du projet.";
            case "Import d'unité": return "Rechargez les unités et leur précision depuis le fichier de préférences JSON.";
            case "Couleur de projet": return "Personnalisez les couleurs du ruban. Double-cliquez pour activer ou désactiver les panneaux colorés.";
            case "RestartAppearanceGuide": return "Suivez le guide depuis le ruban jusqu’aux réglages des couleurs.";
            case "Couleur de maquette": return "Réinitialisez les couleurs appliquées aux onglets des vues.";
            case "papa Noël": return "Animez les couleurs du ruban comme une guirlande. Double-cliquez pour revenir à la normale ; désactivez d’abord les couleurs des panneaux.";
            case "ViewDeckToggle": return "Activez les miniatures dans les onglets ou revenez aux onglets compacts.";
            case "TabCompanionToggle": return "Activez un compagnon pixel qui suit la souris sur la barre des onglets.";
            case "WindowCompanionToggle": return "Activez le compagnon pixel dans toute la fenêtre principale Revit. Fonction de test.";
            case "BIMaestro_Exemple": return "Ouvrez la page de présentation et d’information sur BIMaestro.";
            case "CreateDemoProject": return "Créez une nouvelle maquette d’essai BIMaestro et découvrez les exercices guidés.";
            case "DemoTours": return "Choisissez un parcours pour essayer les outils BIMaestro étape par étape.";
            case "NOTE_MAJ": return "Consultez la page des mises à jour de BIMaestro.";
            case "Snake": return "Faites une pause avec le mini-jeu Snake.";
            case "FlappyBird": return "Faites une pause avec le mini-jeu Flappy Bird.";
            case "CustomizeRibbon": return "Configurez l’organisation du ruban BIMaestro et les préférences utilisateur.";
            case "ContactCommand": return "Ouvrez le profil LinkedIn de Paul Lemert pour partager un retour, signaler un problème ou proposer une idée.";
            case "RadialMenuButtonsCommand": return "Accédez à une rosace personnalisable de 16 favoris et aux boutons BIMaestro récents.";
            case "SupportCommand": return "Ouvrez la page Ko-fi pour soutenir volontairement le développement de BIMaestro.";
            case "ViewTemplateTransfer": return "Transférez les réglages d’une vue et comparez les graphismes obtenus.";
            case "OpenSheetFromViewButton": return "Passez de la vue active à sa feuille associée, ou ouvrez une vue depuis la feuille.";
            case "Export Nomenclature": return "Exportez les nomenclatures sélectionnées en fichiers Excel ou PDF.";
            case "ViewThumbnailBatch": return "Générez les miniatures et activez les aperçus des vues au survol dans l’arborescence et les onglets.";
            case "ExportDwgBatch": return "Exportez plusieurs vues ou feuilles en DWG avec des noms de fichiers liés au projet et à la vue.";
            case "GetPaintedMaterialsButton": return "Cliquez sur une zone pour identifier sa peinture et consulter la composition complète du mur.";
            case "ReorientViewButton": return "Réorientez la vue 3D active selon la face sélectionnée.";
            case "ResérvationAuto": return "Créez des réservations au passage des réseaux dans les parois.";
            case "Bride auto": return "Ajoutez des brides aux extrémités sélectionnées avec votre type de référence.";
            case "Choix bride": return "Choisissez la bride de référence pour les prochains ajouts.";
            case "Suppression de brides": return "Retirez les brides adjacentes aux accessoires ou équipements sélectionnés et reconnectez les réseaux.";
            case "dynamo réglage": return "Associez vos fichiers Dynamo aux cinq boutons et personnalisez leurs libellés.";
            case "GestionExcelCmd": return "Exportez ou importez une nomenclature au format Excel.";
            case "Surcharges vues": return "Appliquez la demi-teinte, la transparence ou le masquage aux éléments dans les vues choisies, puis réinitialisez leurs surcharges.";
            case "ElementRenamerButton": return "Organisez les noms avec des préfixes, suffixes et numéros ; sur une feuille, numérotez les fenêtres de vue.";
            case "PhaseQuickEditButton": return "Modifiez les phases de création et de démolition des objets sélectionnés.";
            case "Purge du plan": return "Choisissez les vues non placées, familles et nomenclatures inutilisées à supprimer.";
            default: return null;
        }
    }

    private static TextBlock ExampleText(string text, bool bold = false)
    {
        return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12 };
    }

    private static bool AddToolTipExample(StackPanel content, string id)
    {
        if (id == "CreateDemoProject" && IsDemoToursPresentation) id = "DemoTours";
        FrameworkElement example;
        string caption;
        switch (id)
        {
            case "Couleur de projet": example = ExampleVideo("InfoColors"); caption = "Palette et panneaux • démonstration illustrée"; break;
            case "RestartAppearanceGuide": example = ModificationImage("InfoAppearanceGuide"); caption = "Du ruban aux réglages • parcours illustré"; break;
            case "Couleur de maquette": example = ModificationImage("InfoReset"); caption = "Onglets colorés / réinitialisés • exemple"; break;
            case "papa Noël": example = ExampleVideo("InfoChristmas"); caption = "Guirlande de couleurs • animation illustrative"; break;
            case "ViewDeckToggle": example = ExampleVideo("InfoDeck"); caption = "Onglets avec miniatures • exemple illustré"; break;
            case "TabCompanionToggle": example = ExampleVideo("InfoTabCompanion"); caption = "Compagnon des onglets • personnage illustratif"; break;
            case "WindowCompanionToggle": example = ExampleVideo("InfoWindowCompanion"); caption = "Compagnon de la fenêtre • personnage illustratif"; break;
            case "BIMaestro_Exemple": example = ModificationImage("InfoAbout"); caption = "Présentation du plugin • aperçu de principe"; break;
            case "CreateDemoProject": example = ModificationImage("InfoDemo"); caption = "Maquette d’essai • exemple illustré"; break;
            case "DemoTours": example = ModificationImage("InfoTours"); caption = "Choisir → essayer → terminer"; break;
            case "NOTE_MAJ": example = ModificationImage("InfoUpdates"); caption = "Page des nouveautés • aperçu de principe"; break;
            case "Snake": example = ExampleVideo("InfoSnake"); caption = "Snake • démonstration illustrative"; break;
            case "FlappyBird": example = ExampleVideo("InfoFlappy"); caption = "Flappy Bird • démonstration illustrative"; break;
            case "CustomizeRibbon": example = ModificationImage("InfoOptions"); caption = "Ruban et préférences • schéma de principe"; break;
            case "ContactCommand": example = ModificationImage("InfoContact"); caption = "Retour ou idée → profil LinkedIn"; break;
            case "RadialMenuButtonsCommand": example = ExampleVideo("InfoRadial"); caption = "16 favoris et boutons récents • rosace illustrative"; break;
            case "SupportCommand": example = ModificationImage("InfoSupport"); caption = "Soutien volontaire → page Ko-fi"; break;
            case "ElementHistoryHoverToggle": example = ModificationImage("AnalysisObjectInfo"); caption = "Objet sélectionné → dernier changement connu"; break;
            case "Suivi maquette collaboratif": example = ModificationImage("AnalysisTracker"); caption = "Maquette et journal des accès • exemple illustré"; break;
            case "FamilyBrowser": example = ExampleVideo("FamilyBrowser"); caption = "Dossier → aperçu → chargement • galerie illustrative"; break;
            case "Rosace": example = ExampleVideo("FamilyRadial"); caption = "Accès radial aux familles • démonstration illustrée"; break;
            case "ConvertSharedToFamily": example = ModificationImage("FamilyConvert"); caption = "Partagé → famille • propriétés conservées"; break;
            case "PurgeFamilyParameters": example = ModificationImage("FamilyPurge"); caption = "Paramètres inutilisés et sauvegarde • exemple"; break;
            case "Familytraduction": example = ModificationImage("FamilyTranslate"); caption = "Noms de paramètres avant / après • exemple"; break;
            case "FamilyViewtraduction": example = ModificationImage("FamilyTranslateViews"); caption = "Noms de vues avant / après • exemple"; break;
            case "Export d'unité": example = ModificationImage("FamilyUnitsExport"); caption = "Unités et précision → fichier JSON"; break;
            case "Import d'unité": example = ModificationImage("FamilyUnitsImport"); caption = "Fichier JSON → unités et précision"; break;
            case "CodexChat": example = ExampleVideo("AiFamily"); caption = "Demande → famille paramétrique • exemple illustré"; break;
            case "CommunityFamilies": example = ModificationImage("AiCommunity"); caption = "Bibliothèque par catégorie • galerie illustrative"; break;
            case "GPTBotWindowButton": example = ModificationImage("AiChat"); caption = "Objet sélectionné et contexte • conversation illustrative"; break;
            case "TextCorrectionButton": example = ModificationImage("AiCorrection"); caption = "Avant / proposition • correction à valider"; break;
            case "ScanText": example = ModificationImage("AiAudit"); caption = "Anomalies repérées et classées • exemple illustré"; break;
            case "RealisticViewImage": example = ExampleVideo("AiRender"); caption = "Plan / rendu • comparaison de principe illustrée"; break;
            case "MepBooster": example = ExampleVideo("BetaBooster"); caption = "Rotation d’un raccord libre • démonstration illustrée"; break;
            case "MaintenanceWorkbench": example = ExampleVideo("MaintenanceMep"); caption = "Extraction et manœuvre • contrôle géométrique illustratif"; break;
            case "HomeAppearanceTest": example = ExampleVideo("InfoHomeAppearance"); caption = "Cartes ou liste • présentations d’accueil illustrées"; break;
            case "PropertiesAppearance": example = ExampleVideo("InfoPropertiesAppearance"); caption = "Couleurs et groupes • aperçu réversible illustré"; break;
            case "PipeSystemColors": example = ExampleVideo("BetaColors"); caption = "Coloration par système • palette illustrative"; break;
            case "RevitGameCommand": example = ExampleVideo("BetaWalkthrough"); caption = "Visite en vue subjective • maquette illustrative"; break;
            case "OpenSheetFromViewButton": example = ExampleVideo("VisNavigation"); caption = "Navigation vue / feuille • exemple illustré"; break;
            case "Export Nomenclature": example = ModificationImage("VisSchedule"); caption = "Excel / PDF • données illustratives"; break;
            case "ViewThumbnailBatch": example = ExampleVideo("VisThumbnails"); caption = "Miniature au survol • démonstration illustrée"; break;
            case "ExportDwgBatch": example = ModificationImage("VisDwg"); caption = "Export par lot • vues et feuilles vers DWG"; break;
            case "GetPaintedMaterialsButton": example = ModificationImage("VisPaint"); caption = "Peinture et couches du mur • schéma illustratif"; break;
            case "ReorientViewButton": example = ExampleVideo("VisFace"); caption = "Alignement sur une face • animation de principe"; break;
            case "ResérvationAuto": example = ExampleVideo("ModReservation"); caption = "Passage de réseau • réservation illustrée"; break;
            case "Bride auto": example = ExampleVideo("ModFlanges"); caption = "Ajout animé • exemple simplifié"; break;
            case "Choix bride": example = ModificationImage("ModChooseFlange"); caption = "Choix visuel du type de référence"; break;
            case "Suppression de brides": example = ExampleVideo("ModRemoveFlanges"); caption = "Retrait et reconnexion • exemple simplifié"; break;
            case "dynamo 1": case "dynamo 2": case "dynamo 3": case "dynamo 4": case "dynamo 5":
                example = ModificationImage("ModDynamo" + id.Substring(id.Length - 1)); caption = "Entrées → script → résultat • illustration générique"; break;
            case "dynamo réglage": example = ModificationImage("ModDynamoSettings"); caption = "Cinq emplacements pour vos scripts"; break;
            case "GestionExcelCmd": example = ModificationImage("ModExcel"); caption = "Échange visuel Revit / Excel • valeurs illustratives"; break;
            case "Surcharges vues": example = ExampleVideo("ModOverrides"); caption = "Comparaison animée des modes d’affichage"; break;
            case "ElementRenamerButton": example = ModificationImage("ModRename"); caption = "Avant / après • noms illustratifs"; break;
            case "PhaseQuickEditButton": example = ModificationImage("ModPhases"); caption = "Existant / démoli / nouveau • schéma de principe"; break;
            case "Purge du plan": example = ModificationImage("ModPurge"); caption = "Sélection des éléments avant suppression"; break;
            case "PipeLengthByDiameterV2":
                example = new Image { Source = ExampleImage("PipeSequence.png"), Width = 320, Height = 156, Stretch = Stretch.Uniform };
                caption = "Vignettes de démonstration • réseau → diamètres → Excel"; break;
            case "Qui a fait ça ?":
                example = ExampleVideo("AnalysisHistory"); caption = "Déplacement et chronologie • démonstration illustrée"; break;
            case "AnalysePoidsButton":
                example = ExampleVideo("AnalysisWeight"); caption = "Classement animé par taille • valeurs illustratives"; break;
            case "Temps par projet":
                example = ExampleVideo("AnalysisTime"); caption = "Durées et répartition • données de démonstration"; break;
            case "Clash 3D":
                example = ExampleVideo("ClashCut");
                caption = "Coupe animée de démonstration • paroi transparente et intersection rouge"; break;
            case "HighlightElementsByCategories":
                example = ExampleVideo("Highlight");
                caption = "Objet de référence → filtre famille/type → sélection"; break;
            case "ViewTemplateTransfer":
                example = ExampleVideo("ViewWipe");
                caption = "Comparaison par balayage • graphismes avant / après illustrés"; break;
            case "Sélection d'objet":
                example = ExampleVideo("Selection");
                caption = "Mini-vidéo de démonstration • 6 secondes • sans son"; break;
            default: return false;
        }
        example.Margin = new Thickness(0, 12, 0, 0);
        content.Children.Add(example);
        var note = ExampleText(caption);
        note.Foreground = new SolidColorBrush(Color.FromRgb(100, 108, 122));
        note.FontSize = 11; note.Margin = new Thickness(0, 6, 0, 0);
        content.Children.Add(note);
        return true;
    }

    private static StackPanel CreateReservationToolTipCard()
    {
        var card = new StackPanel { Width = 320 };
        card.Children.Add(new Border {
            Background = new SolidColorBrush(Color.FromRgb(237, 243, 249)),
            CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Child = new Image { Source = ExampleImage("ReservationCardHero.png"), Width = 320, Height = 126, Stretch = Stretch.Uniform }
        });
        var title = ExampleText("Réservation auto", true);
        title.FontSize = 16; title.Margin = new Thickness(10, 10, 10, 0);
        card.Children.Add(title);
        var summary = ExampleText("Une ouverture centrée sur le réseau, au passage de la paroi.");
        summary.Margin = new Thickness(10, 5, 10, 0); summary.MaxWidth = 300;
        card.Children.Add(summary);
        var steps = new Grid { Margin = new Thickness(8, 12, 8, 8) };
        for (int i = 0; i < 3; i++) steps.ColumnDefinitions.Add(new ColumnDefinition());
        string[] labels = { "Réseau", "Paroi", "Réservation" };
        for (int i = 0; i < 3; i++)
        {
            var step = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            step.Children.Add(new Image { Source = ExampleImage("ReservationCardStep" + i + ".png"), Width = 44, Height = 44 });
            var label = ExampleText(labels[i], true); label.FontSize = 11;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            step.Children.Add(label);
            Grid.SetColumn(step, i); steps.Children.Add(step);
        }
        card.Children.Add(steps);
        var footer = ExampleText("APERÇU ILLUSTRATIF", true);
        footer.FontSize = 9; footer.Foreground = new SolidColorBrush(Color.FromRgb(110, 127, 145));
        footer.Margin = new Thickness(10, 0, 10, 4); card.Children.Add(footer);
        return card;
    }

    private static Border ExampleCard(string heading, string text, string color)
    {
        var body = new StackPanel();
        body.Children.Add(ExampleText(heading, true));
        var detail = ExampleText(text); detail.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(detail);
        return new Border { Background = (Brush)new BrushConverter().ConvertFromString(color),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(10), Child = body };
    }

    private static Image ModificationImage(string asset) => new Image {
        Source = ExampleImage(asset + ".png"), Width = 320, Height = 180, Stretch = Stretch.Uniform
    };

    private static Border HistoryExample(bool after)
    {
        var panel = new StackPanel();
        panel.Children.Add(ExampleText(after ? "APRÈS" : "AVANT", true));
        var plan = new Canvas { Width = 130, Height = 100, Margin = new Thickness(0, 8, 0, 8) };
        var room = new Rectangle { Width = 126, Height = 96, Stroke = Brushes.SlateGray, StrokeThickness = 2 };
        plan.Children.Add(room);
        var pipe = new Rectangle { Width = 84, Height = 12, Fill = after ? Brushes.DodgerBlue : Brushes.SlateGray };
        Canvas.SetLeft(pipe, 22); Canvas.SetTop(pipe, after ? 58 : 26); plan.Children.Add(pipe);
        if (after)
        {
            var ghost = new Rectangle { Width = 84, Height = 12, Stroke = Brushes.SlateGray,
                StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
            Canvas.SetLeft(ghost, 22); Canvas.SetTop(ghost, 26); plan.Children.Add(ghost);
            var arrow = new TextBlock { Text = "↓", FontSize = 22, Foreground = Brushes.DodgerBlue };
            Canvas.SetLeft(arrow, 56); Canvas.SetTop(arrow, 34); plan.Children.Add(arrow);
        }
        panel.Children.Add(plan);
        panel.Children.Add(ExampleText(after ? "Position modifiée" : "Position initiale"));
        return new Border { Background = Brushes.WhiteSmoke, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8), Child = panel };
    }

    private static string ExampleAsset(string name) => Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Content", "ToolTipExamples", name);

    private static BitmapImage ExampleImage(string name)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(ExampleAsset(name)); image.EndInit(); image.Freeze(); return image;
    }

    private static FrameworkElement ExampleVideo(string asset)
    {
        var area = new Grid { Width = 320, Height = 180, Background = Brushes.WhiteSmoke };
        area.Children.Add(new Image { Source = ExampleImage(asset + "Poster.png"), Stretch = Stretch.Uniform });
        var video = new MediaElement { Width = 320, Height = 180, Stretch = Stretch.Uniform,
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Volume = 0, IsMuted = true, Tag = "ToolTipExampleVideo" };
        video.Loaded += (s, e) => { video.Source = new Uri(ExampleAsset(asset + ".mp4")); video.Play(); };
        video.Unloaded += (s, e) => { video.Close(); };
        video.MediaEnded += (s, e) => { video.Position = TimeSpan.Zero; video.Play(); };
        video.MediaFailed += (s, e) => {
            video.Visibility = Visibility.Collapsed;
            var fallback = ExampleText("Vidéo indisponible — aperçu fixe", true);
            fallback.Background = Brushes.White; fallback.VerticalAlignment = VerticalAlignment.Bottom;
            area.Children.Add(fallback);
        };
        area.Children.Add(video);
        return area;
    }

    private static void StopToolTipExampleVideo(object sender, RoutedEventArgs args)
    {
        var popup = sender as ToolTip;
        if (popup?.Content is DependencyObject root) StopExampleMedia(root);
    }

    private static void StopExampleMedia(DependencyObject root)
    {
        if (root is MediaElement video) { video.Close(); return; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) StopExampleMedia(VisualTreeHelper.GetChild(root, i));
    }
}
