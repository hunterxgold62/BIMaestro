using System.Collections.Generic;

namespace Page
{
    internal static class ButtonUpdateNotesCatalog
    {
        public static IReadOnlyList<ButtonUpdateNote> Notes { get; } = new List<ButtonUpdateNote>
        {
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "MainCommand",
                CommandClass = "Analyse.MainCommand",
                Title = "Qui a fait ça ?",
                Summary = "Refonte majeure de l'outil Qui a fait ça ?.",
                Changes = new List<string>
                {
                    "L'ancien historique séparé dans Suivi maquette collaboratif a été supprimé puis réintégré directement dans commands/qui est le coupable.",
                    "Ajout d'une vraie interface dédiée pour consulter l'historique des actions.",
                    "Visualisation des suppressions et déplacements directement dans la maquette.",
                    "Ajout des clusters pour regrouper les événements liés.",
                    "Ajout de filtres pour retrouver plus facilement les actions importantes.",
                    "Ajout des snapshots pour mieux comprendre l'état ou l'action enregistrée.",
                    "Regroupement intelligent des événements.",
                    "Chargement de l'historique par jour pour améliorer les performances et la lisibilité.",
                    "Nettoyage automatique des anciennes captures inutiles."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "FamilyBrowserCommand",
                CommandClass = "Famille.FamilyBrowserCommand",
                Title = "Navigateur de familles",
                Summary = "Gros travail sur l'interface et la logique générale.",
                Changes = new List<string>
                {
                    "Ajout d'un fil d'Ariane pour mieux naviguer dans les dossiers.",
                    "Tuiles retravaillées pour une présentation plus propre.",
                    "Regroupement des familles par dossier.",
                    "Amélioration de l'indexation.",
                    "États de recherche rendus plus clairs et plus fiables."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "ShowTimeDashboard",
                CommandClass = "BIMaestro.Dashboard.ShowTimeDashboard",
                Title = "Temps par projet",
                Summary = "Refonte importante du système de suivi du temps Revit.",
                Changes = new List<string>
                {
                    "Amélioration du logger Excel.",
                    "Meilleure classification des données enregistrées.",
                    "Suivi par version de Revit.",
                    "Dashboard amélioré pour une lecture plus claire du temps passé par projet."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "PhaseQuickEditCommand",
                CommandClass = "Modification.PhaseQuickEditCommand",
                Title = "Phases rapides",
                Summary = "Nouvelle commande ajoutée directement dans le ruban.",
                Changes = new List<string>
                {
                    "Ajout d'une nouvelle commande Phases rapides.",
                    "Modification plus directe des phases de création et de démolition des objets sélectionnés."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "SmartClashCommand",
                CommandClass = "Analyse.SmartClashCommand",
                Title = "Smart Clash / Analyse 3D",
                Summary = "Nombreuses améliorations côté état et fonctionnement général.",
                Changes = new List<string>
                {
                    "Amélioration de la fenêtre d'analyse.",
                    "Ajout et amélioration des types de conflits.",
                    "Renforcement du handler externe pour une exécution plus stable."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "GuideCommand",
                CommandClass = "Page.GuideCommand",
                Title = "Interface générale",
                Summary = "L'expérience BIMaestro a été polie sur plusieurs fenêtres et points d'entrée.",
                Changes = new List<string>
                {
                    "Fenêtres mieux habillées et plus propres visuellement.",
                    "Messages de bienvenue retravaillés.",
                    "Ajout de liens d'aide en ligne.",
                    "Gestion du prompt de bienvenue selon la version installée."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "ContactCommand",
                CommandClass = "Page.ContactCommand",
                Title = "Contact",
                Summary = "Nouveau bouton pour faciliter les retours et demandes.",
                Changes = new List<string>
                {
                    "Ajout d'un bouton de contact LinkedIn."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "ExportScheduleCommand",
                CommandClass = "Visualisation.ExportScheduleCommand",
                Title = "Export de nomenclature",
                Summary = "Améliorations sur les exports.",
                Changes = new List<string>
                {
                    "Polissage des exports.",
                    "Corrections mineures et amélioration globale de la stabilité."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "ExportSheetsCommand",
                CommandClass = "Visualisation.ExportSheetsCommand",
                Title = "Export DWG",
                Summary = "Améliorations sur les exports.",
                Changes = new List<string>
                {
                    "Polissage des exports.",
                    "Corrections mineures et amélioration globale de la stabilité."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "CommandAnalysePoids",
                CommandClass = "Analyse.CommandAnalysePoids",
                Title = "Analyse de poids",
                Summary = "Polissage de l'analyse de poids.",
                Changes = new List<string>
                {
                    "Améliorations et finitions sur l'analyse de poids.",
                    "Corrections mineures et nettoyage de comportements anciens."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "SelectViewsCommand",
                CommandClass = "ScanTextRevit.SelectViewsCommand",
                Title = "Correction automatique",
                Summary = "Améliorations sur la correction automatique.",
                Changes = new List<string>
                {
                    "Améliorations sur l'analyse et la correction automatique.",
                    "Corrections mineures et amélioration de l'expérience utilisateur."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "TextCorrectionCommand",
                CommandClass = "IA.TextCorrectionCommand",
                Title = "Correction de texte IA",
                Summary = "Améliorations sur la correction automatique.",
                Changes = new List<string>
                {
                    "Améliorations sur les outils de correction.",
                    "Corrections mineures et amélioration de l'expérience utilisateur."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "SnakeCommand",
                CommandClass = "BIMaestro.Bonus.SnakeCommand",
                Title = "Snake",
                Summary = "Ajustements et finitions sur les jeux intégrés.",
                Changes = new List<string>
                {
                    "Finitions et corrections mineures."
                }
            },
            new ButtonUpdateNote
            {
                Version = "1.0.6.2",
                ButtonId = "FlappyBirdCommand",
                CommandClass = "BIMaestro.Bonus.FlappyBirdCommand",
                Title = "Flappy Bird",
                Summary = "Ajustements et finitions sur les jeux intégrés.",
                Changes = new List<string>
                {
                    "Finitions et corrections mineures."
                }
            }
        };
    }
}
