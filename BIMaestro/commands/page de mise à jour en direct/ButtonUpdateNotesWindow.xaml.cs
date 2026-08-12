using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BIMaestro.Localization;

namespace Page
{
    public partial class ButtonUpdateNotesWindow : Window
    {
        public ButtonUpdateNotesWindow(IReadOnlyList<ButtonUpdateNote> notes, string previousVersion, string version)
        {
            InitializeComponent();

            var list = (notes ?? new List<ButtonUpdateNote>())
                .Select(LocalizeNote)
                .ToList();
            NotesItems.ItemsSource = list;

            var title = list.Count == 1 && !string.IsNullOrWhiteSpace(list[0].Title)
                ? UiLanguage.T("Nouveautés - ", "What's New - ") + UiLanguage.T(list[0].Title)
                : UiLanguage.T("Nouveautés BIMaestro", "What's New in BIMaestro");

            Title = title;
            HeaderTitleText.Text = title;
            HeaderSubtitleText.Text = string.IsNullOrWhiteSpace(previousVersion)
                ? UiLanguage.T("Version ", "Version ") + version + UiLanguage.T(" - première utilisation après mise à jour", " - First Use after Update")
                : UiLanguage.T("Passage ", "Upgrade ") + previousVersion + " → " + version + UiLanguage.T(" - première utilisation après mise à jour", " - First Use after Update");
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private static ButtonUpdateNote LocalizeNote(ButtonUpdateNote note)
        {
            if (note == null || !UiLanguage.IsEnglish)
                return note;

            return new ButtonUpdateNote
            {
                Version = note.Version,
                ButtonId = note.ButtonId,
                CommandClass = note.CommandClass,
                Title = string.IsNullOrWhiteSpace(note.EnglishTitle)
                    ? TranslateNoteText(note.Title)
                    : note.EnglishTitle,
                Summary = string.IsNullOrWhiteSpace(note.EnglishSummary)
                    ? TranslateNoteText(note.Summary)
                    : note.EnglishSummary,
                Changes = note.EnglishChanges != null && note.EnglishChanges.Count > 0
                    ? note.EnglishChanges.ToList()
                    : (note.Changes ?? new List<string>())
                        .Select(TranslateNoteText)
                        .ToList(),
                EnglishTitle = note.EnglishTitle,
                EnglishSummary = note.EnglishSummary,
                EnglishChanges = note.EnglishChanges?.ToList() ?? new List<string>()
            };
        }

        private static string TranslateNoteText(string text)
        {
            return text switch
            {
                "Qui a fait ça ?" => "Who Did That?",
                "Navigateur de familles" => "Family Browser",
                "Temps par projet" => "Time by Project",
                "Phases rapides" => "Quick Phases",
                "Interface générale" => "General Interface",
                "Export de nomenclature" => "Schedule Export",
                "Analyse de poids" => "Weight Analysis",
                "Correction automatique" => "Automatic Correction",
                "Correction de texte IA" => "AI Text Correction",
                "Refonte majeure de l'outil Qui a fait ça ?." => "Major Redesign of the Who Did That? Tool.",
                "Gros travail sur l'interface et la logique générale." => "Major Improvements to the Interface and Overall Logic.",
                "Refonte importante du système de suivi du temps Revit." => "Major Redesign of the Revit Time-Tracking System.",
                "Nouvelle commande ajoutée directement dans le ruban." => "New Command Added Directly to the Ribbon.",
                "Nombreuses améliorations côté état et fonctionnement général." => "Numerous Improvements to Status Handling and Overall Operation.",
                "L'expérience BIMaestro a été polie sur plusieurs fenêtres et points d'entrée." => "The BIMaestro Experience Was Refined across Several Windows and Entry Points.",
                "Nouveau bouton pour faciliter les retours et demandes." => "New Button to Make Feedback and Requests Easier.",
                "Améliorations sur les exports." => "Export Improvements.",
                "Polissage de l'analyse de poids." => "Weight Analysis Refinements.",
                "Améliorations sur la correction automatique." => "Automatic Correction Improvements.",
                "Ajustements et finitions sur les jeux intégrés." => "Adjustments and Refinements to the Built-in Games.",
                "Ajout d'une vraie interface dédiée pour consulter l'historique des actions." => "Added a Dedicated Interface for Reviewing Action History.",
                "Visualisation des suppressions et déplacements directement dans la maquette." => "Visualize Deletions and Moves Directly in the Model.",
                "Ajout des clusters pour regrouper les événements liés." => "Added Clusters to Group Related Events.",
                "Ajout de filtres pour retrouver plus facilement les actions importantes." => "Added Filters to Find Important Actions More Easily.",
                "Ajout des snapshots pour mieux comprendre l'état ou l'action enregistrée." => "Added Snapshots to Better Understand the Recorded State or Action.",
                "Regroupement intelligent des événements." => "Smart Event Grouping.",
                "Chargement de l'historique par jour pour améliorer les performances et la lisibilité." => "Load History by Day to Improve Performance and Readability.",
                "Nettoyage automatique des anciennes captures inutiles." => "Automatic Cleanup of Old Unused Captures.",
                "Ajout d'un fil d'Ariane pour mieux naviguer dans les dossiers." => "Added Breadcrumbs for Easier Folder Navigation.",
                "Tuiles retravaillées pour une présentation plus propre." => "Reworked Tiles for a Cleaner Presentation.",
                "Regroupement des familles par dossier." => "Group Families by Folder.",
                "Amélioration de l'indexation." => "Improved Indexing.",
                "États de recherche rendus plus clairs et plus fiables." => "Clearer and More Reliable Search States.",
                "Amélioration du logger Excel." => "Improved Excel Logger.",
                "Meilleure classification des données enregistrées." => "Better Classification of Recorded Data.",
                "Suivi par version de Revit." => "Tracking by Revit Version.",
                "Dashboard amélioré pour une lecture plus claire du temps passé par projet." => "Improved Dashboard for a Clearer View of Time Spent by Project.",
                "Ajout d'une nouvelle commande Phases rapides." => "Added the New Quick Phases Command.",
                "Modification plus directe des phases de création et de démolition des objets sélectionnés." => "More Direct Editing of Creation and Demolition Phases for Selected Objects.",
                "Amélioration de la fenêtre d'analyse." => "Improved Analysis Window.",
                "Ajout et amélioration des types de conflits." => "Added and Improved Conflict Types.",
                "Renforcement du handler externe pour une exécution plus stable." => "Strengthened the External Handler for More Stable Execution.",
                "Fenêtres mieux habillées et plus propres visuellement." => "Cleaner, Better-Styled Windows.",
                "Messages de bienvenue retravaillés." => "Reworked Welcome Messages.",
                "Ajout de liens d'aide en ligne." => "Added Online Help Links.",
                "Gestion du prompt de bienvenue selon la version installée." => "Welcome Prompt Handling Based on the Installed Version.",
                "Ajout d'un bouton de contact LinkedIn." => "Added a LinkedIn Contact Button.",
                "Polissage des exports." => "Export Refinements.",
                "Corrections mineures et amélioration globale de la stabilité." => "Minor Fixes and Overall Stability Improvements.",
                "Améliorations et finitions sur l'analyse de poids." => "Weight Analysis Improvements and Refinements.",
                "Corrections mineures et nettoyage de comportements anciens." => "Minor Fixes and Cleanup of Legacy Behaviors.",
                "Améliorations sur l'analyse et la correction automatique." => "Automatic Analysis and Correction Improvements.",
                "Corrections mineures et amélioration de l'expérience utilisateur." => "Minor Fixes and User Experience Improvements.",
                "Améliorations sur les outils de correction." => "Correction Tool Improvements.",
                "Finitions et corrections mineures." => "Refinements and Minor Fixes.",
                _ => text
            };
        }
    }
}
