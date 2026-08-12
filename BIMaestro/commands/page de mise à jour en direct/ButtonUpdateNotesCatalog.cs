using System.Collections.Generic;

namespace Page
{
    internal static class ButtonUpdateNotesCatalog
    {
        private const string Release1063 = "1.0.6.3";

        public static IReadOnlyList<ButtonUpdateNote> Notes { get; } = new List<ButtonUpdateNote>
        {
            Create1063Note(
                "RevitGameCommand",
                "BIMaestro.VideoGames.RevitGameCommand",
                "Maquette MEP jouable",
                "Playable MEP Model",
                "Une nouvelle expérience transforme la vue 3D active en maquette interactive.",
                "A new experience turns the active 3D view into an interactive model.",
                new[]
                {
                    "Déplacement à la première personne avec marche, course, saut, accroupissement, gravité et vol libre.",
                    "Collisions stabilisées sur les murs, sols, escaliers, rampes et équipements, avec recherche d'un point de départ sûr.",
                    "Ouverture des portes proches et interaction contextuelle avec les vannes.",
                    "Affichage des informations Revit d'un objet par clic et conservation des couleurs et de la boîte de coupe de la vue.",
                    "Simulation MEP avec sens de circulation, scénarios nommés, vannes fermées et isolement correct des réseaux aller/retour."
                },
                new[]
                {
                    "First-person navigation with walking, running, jumping, crouching, gravity, and free-flight mode.",
                    "Stabilized collision handling for walls, floors, stairs, ramps, and equipment, with safe spawn detection.",
                    "Open nearby doors and interact contextually with valves.",
                    "Click objects to display their Revit information while preserving the view colors and section box.",
                    "MEP simulation with flow direction, named scenarios, closed valves, and correct supply/return network isolation."
                }),
            Create1063Note(
                "ViewThumbnailBatch",
                "BIMaestro.ViewHover.ViewThumbnailBatchCommand",
                "Miniatures et aperçus de vues",
                "View Thumbnails and Previews",
                "Les vues du projet disposent maintenant d'aperçus plus rapides à consulter.",
                "Project views now have faster, easier-to-review previews.",
                new[]
                {
                    "Nouveau bouton Miniature pour générer en lot les aperçus manquants ou obsolètes.",
                    "Traitement en arrière-plan une vue à la fois, sans remplacer durablement la vue active.",
                    "Fenêtre de progression avec pause, reprise et arrêt.",
                    "Aperçu au survol dans l'arborescence du projet, plus rapide et mieux positionné."
                },
                new[]
                {
                    "New Thumbnail button to batch-generate missing or outdated previews.",
                    "Background processing one view at a time without permanently replacing the active view.",
                    "Progress window with pause, resume, and stop controls.",
                    "Faster, better-positioned hover previews in the Project Browser."
                }),
            Create1063Note(
                "FamilyBrowser",
                "Famille.FamilyBrowserCommand",
                "Navigateur de familles",
                "Family Browser",
                "La recherche, la navigation et les aperçus ont été largement retravaillés.",
                "Search, navigation, and previews have been extensively reworked.",
                new[]
                {
                    "Recherche plus intelligente sur les noms et informations des familles, même en cas de saisie approximative.",
                    "Indexation et rafraîchissement optimisés pour garder l'interface réactive sur les gros catalogues.",
                    "États de recherche, filtres, regroupements et navigation dans les dossiers clarifiés.",
                    "Aperçus 2D/3D, chargement, rechargement et génération d'images rendus plus robustes.",
                    "Ouverture de la fenêtre et restauration de son état fiabilisées."
                },
                new[]
                {
                    "Smarter search across family names and information, even with approximate input.",
                    "Optimized indexing and refresh behavior to keep large catalogs responsive.",
                    "Clearer search states, filters, grouping, and folder navigation.",
                    "More robust 2D/3D previews, loading, reloading, and image generation.",
                    "More reliable window startup and state restoration."
                }),
            Create1063Note(
                "ResérvationAuto",
                "Modification.ReservationAutoV3Command",
                "Réservation automatique V3",
                "Automatic Openings V3",
                "La troisième génération devient la commande principale de réservation automatique.",
                "The third generation is now the main automatic-opening command.",
                new[]
                {
                    "Prise en charge des murs et planchers provenant de fichiers Revit liés.",
                    "Sélection multiple dans les liens et placement automatique sur plusieurs traversées.",
                    "Calcul des points, orientations, épaisseurs et dimensions amélioré pour les géométries liées.",
                    "Fenêtre de réglage et retours utilisateur retravaillés.",
                    "Les anciennes commandes V1 et V2 ont été retirées du projet au profit de la V3."
                },
                new[]
                {
                    "Support for walls and floors from linked Revit models.",
                    "Multi-selection in links and automatic placement across several penetrations.",
                    "Improved point, orientation, thickness, and dimension calculations for linked geometry.",
                    "Reworked settings window and user feedback.",
                    "Legacy V1 and V2 commands were removed in favor of V3."
                }),
            Create1063Note(
                "GestionExcelCmd",
                "ScheduleIO.ScheduleExcelIOCommand",
                "Gestion Excel",
                "Excel Management",
                "L'échange bidirectionnel entre Revit et Excel gagne en compatibilité et en fiabilité.",
                "Two-way exchange between Revit and Excel is now more compatible and reliable.",
                new[]
                {
                    "Export des nomenclatures vers Excel plus stable.",
                    "Réimport des valeurs modifiées plus fiable.",
                    "Messages, contrôles de fichiers et erreurs d'import/export clarifiés.",
                    "Interface disponible en français et en anglais."
                },
                new[]
                {
                    "More stable schedule export to Excel.",
                    "More reliable re-import of edited values.",
                    "Clearer file checks and import/export error messages.",
                    "Interface available in French and English."
                }),
            Create1063Note(
                "PipeLengthByDiameterV2",
                "Analyse.PipeLengthByDiameterCommandV2",
                "Calcul des canalisations",
                "Pipe Network Calculation",
                "Le calcul et l'export des réseaux MEP ont été consolidés.",
                "MEP network calculation and export have been consolidated.",
                new[]
                {
                    "Tableau Excel plus fiable et plus simple à exploiter.",
                    "Analyse des longueurs, diamètres, accessoires et volumes rendue plus robuste.",
                    "Gestion des systèmes, filtres et résultats améliorée pour les réseaux complexes.",
                    "Présentation des résultats clarifiée."
                },
                new[]
                {
                    "More reliable and easier-to-use Excel report.",
                    "More robust analysis of lengths, diameters, fittings, and volumes.",
                    "Improved system handling, filters, and results for complex networks.",
                    "Clearer result presentation."
                }),
            Create1063Note(
                "Qui a fait ça ?",
                "Analyse.MainCommand",
                "Qui a fait ça ?",
                "Who Did That?",
                "Le suivi de l'historique devient plus léger, plus complet et plus rapide à consulter.",
                "History tracking is now lighter, more complete, and faster to review.",
                new[]
                {
                    "Historique disponible plus rapidement après l'ouverture du document.",
                    "Moins de lignes répétitives pour une lecture plus claire.",
                    "Davantage de suppressions et de modifications d'éléments sont reconnues.",
                    "Recherche du dernier changement d'un objet accélérée.",
                    "Libellés, filtres et affichage de l'historique harmonisés avec la nouvelle langue de l'interface."
                },
                new[]
                {
                    "History becomes available more quickly after opening a document.",
                    "Fewer repeated entries for clearer reading.",
                    "More element deletions and changes are recognized.",
                    "Faster lookup of an object's latest change.",
                    "History labels, filters, and display now follow the selected interface language."
                }),
            Create1063Note(
                "ElementHistoryHoverToggle",
                "Analyse.ElementHistoryHoverToggleCommand",
                "Informations d'historique au survol",
                "History Information on Hover",
                "Un nouveau mode affiche le dernier changement connu après la sélection d'un objet.",
                "A new mode displays the latest known change after selecting an object.",
                new[]
                {
                    "Activation depuis le menu du bouton Qui a fait ça ?.",
                    "Résumé immédiat de la dernière action, de l'utilisateur et de la date disponibles.",
                    "Affichage rapide, même lorsque l'historique contient beaucoup d'actions.",
                    "Le mode reste volontairement désactivé à chaque nouveau démarrage de Revit."
                },
                new[]
                {
                    "Enable it from the Who Did That? split-button menu.",
                    "Immediate summary of the latest available action, user, and date.",
                    "Fast display even when the history contains many actions.",
                    "The mode intentionally starts disabled whenever Revit is launched."
                }),
            Create1063Note(
                "CustomizeRibbon",
                "BIMaestro.RibbonLayout.RibbonLayoutCommand",
                "Personnalisation du ruban",
                "Ribbon Customization",
                "La fenêtre Options permet de mieux organiser et prévisualiser le ruban BIMaestro.",
                "The Options window now provides better BIMaestro ribbon organization and previewing.",
                new[]
                {
                    "Organisation des panneaux et boutons avec une arborescence plus claire.",
                    "Prévisualisation améliorée de la disposition avant application.",
                    "Prise en charge du nouveau panneau Beta et des nouveaux boutons.",
                    "Libellés et états de configuration localisés en français et en anglais."
                },
                new[]
                {
                    "Organize panels and buttons through a clearer tree view.",
                    "Improved layout preview before applying changes.",
                    "Support for the new Beta panel and new commands.",
                    "Configuration labels and states localized in French and English."
                }),
            Create1063Note(
                "Couleur de projet",
                "Couleur.ToggleCombinedColoringCommand",
                "Couleurs du ruban",
                "Ribbon Colors",
                "La coloration automatique du ruban devient entièrement personnalisable.",
                "Automatic ribbon coloring is now fully customizable.",
                new[]
                {
                    "Choix des couleurs par panneau avec aperçu immédiat.",
                    "Nouveaux préréglages, sélecteur de couleurs et réglages de contraste.",
                    "Clic simple pour ouvrir la personnalisation et double-clic pour activer ou désactiver les couleurs.",
                    "Restauration des préférences et application au démarrage rendues plus fiables."
                },
                new[]
                {
                    "Choose colors per panel with an immediate preview.",
                    "New presets, color picker, and contrast settings.",
                    "Single-click to open customization and double-click to enable or disable colors.",
                    "More reliable preference restoration and startup application."
                }),
            Create1063Note(
                "Clash 3D",
                "Analyse.SmartClashCommand",
                "Clash 3D",
                "3D Clash Analysis",
                "La lecture et le traitement des anomalies 3D ont été enrichis.",
                "Reviewing and handling 3D issues has been expanded.",
                new[]
                {
                    "Cartes d'anomalies enrichies avec miniatures, gravité, statut et type plus lisibles.",
                    "Actions rapides pour afficher le détail, focaliser et isoler les éléments concernés.",
                    "Navigation entre groupes et génération des miniatures améliorées.",
                    "Interface et recommandations disponibles en français et en anglais."
                },
                new[]
                {
                    "Richer issue cards with thumbnails and clearer severity, status, and type information.",
                    "Quick actions to open details, focus, and isolate affected elements.",
                    "Improved group navigation and thumbnail generation.",
                    "Interface and recommendations available in French and English."
                }),
            Create1063Note(
                "ElementRenamerButton",
                "Modification.RenameElementsCommand",
                "Organisateur",
                "Organizer",
                "La numérotation des éléments et fenêtres de vue est plus prévisible.",
                "Element and viewport numbering is now more predictable.",
                new[]
                {
                    "Sur une feuille, les fenêtres de vue peuvent être ordonnées de haut en bas puis de gauche à droite.",
                    "Ordre de numérotation plus stable et plus prévisible.",
                    "Fenêtre et messages de renommage clarifiés et localisés."
                },
                new[]
                {
                    "On a sheet, viewports can be ordered from top to bottom and then left to right.",
                    "More stable and predictable numbering order.",
                    "Clearer, localized renaming window and messages."
                }),
            Create1063Note(
                "Purge du plan",
                "Modification.CombinedCleanupCommand",
                "Purge du projet",
                "Project Cleanup",
                "Le nettoyage protège mieux le fichier d'origine et explique plus clairement chaque étape.",
                "Cleanup now protects the original file more effectively and explains each step more clearly.",
                new[]
                {
                    "Proposition de créer automatiquement une copie dédiée avant la purge.",
                    "Avertissement spécifique pour les modèles en travail partagé.",
                    "Sélection et confirmation séparées pour les vues, familles et nomenclatures inutilisées.",
                    "Messages de résultat et de sécurité entièrement localisés."
                },
                new[]
                {
                    "Option to automatically create a dedicated copy before cleanup.",
                    "Specific warning for workshared models.",
                    "Separate selection and confirmation for unused views, families, and schedules.",
                    "Fully localized result and safety messages."
                }),
            Create1063Note(
                "Sélection d'objet",
                "Visualisation.SelectSimilarCommand",
                "Sélection similaire",
                "Select Similar",
                "La sélection par catégorie, famille ou type est plus guidée et plus sûre.",
                "Selection by category, family, or type is now more guided and safer.",
                new[]
                {
                    "Choix explicite du critère avant de compléter la sélection.",
                    "Possibilité de colorer les résultats dans la vue.",
                    "Mémorisation et nettoyage de la dernière série de couleurs appliquée.",
                    "Gestion plus sûre des sélections externes et interface bilingue."
                },
                new[]
                {
                    "Explicit criterion selection before completing the selection.",
                    "Option to color the results in the view.",
                    "Remember and clear the last applied color set.",
                    "Safer external selection handling and a bilingual interface."
                }),
            Create1063Note(
                "Temps par projet",
                "BIMaestro.Dashboard.ShowTimeDashboard",
                "Temps par projet",
                "Time by Project",
                "Le suivi du temps et ses exports ont été modernisés.",
                "Time tracking and its exports have been modernized.",
                new[]
                {
                    "Export Excel rendu plus stable.",
                    "Classement, graphiques et lecture des périodes améliorés.",
                    "Meilleure gestion des versions Revit et des projets renommés ou déplacés.",
                    "Dashboard et messages disponibles en français et en anglais."
                },
                new[]
                {
                    "More stable Excel export.",
                    "Improved sorting, charts, and period review.",
                    "Better handling of Revit versions and renamed or moved projects.",
                    "Dashboard and messages available in French and English."
                }),
            Create1063Note(
                "Suivi maquette collaboratif",
                "Analyse.CollaborativeModelTrackerCommand",
                "Suivi maquette collaboratif",
                "Collaborative Model Tracking",
                "Le registre partagé est plus autonome et plus robuste.",
                "The shared register is now more independent and robust.",
                new[]
                {
                    "Registre Excel actualisé plus fiablement.",
                    "Choix d'un dossier commun personnalisé et repli local mieux expliqués.",
                    "Tableau Excel recréé proprement avec filtres, en-têtes figés et colonnes ajustées.",
                    "Fenêtre, statuts et erreurs localisés."
                },
                new[]
                {
                    "More reliable Excel register updates.",
                    "Clearer custom shared-folder selection and local fallback behavior.",
                    "Cleanly rebuilt Excel table with filters, frozen headers, and adjusted columns.",
                    "Localized window, statuses, and errors."
                }),
            Create1063Note(
                "Surcharges vues",
                "Modification.OverrideColorCommand",
                "Surcharges de vues",
                "View Overrides",
                "Les surcharges graphiques peuvent être appliquées plus facilement sur plusieurs vues.",
                "Graphic overrides can now be applied more easily across multiple views.",
                new[]
                {
                    "Application de la demi-teinte, transparence ou visibilité sur les vues choisies.",
                    "Lorsqu'une feuille est sélectionnée, traitement des vues placées sur cette feuille.",
                    "Sélecteur de couleur et retours utilisateur harmonisés avec le thème et la langue."
                },
                new[]
                {
                    "Apply halftone, transparency, or visibility settings to selected views.",
                    "When a sheet is selected, process the views placed on that sheet.",
                    "Color picker and user feedback aligned with the selected theme and language."
                }),
            Create1063Note(
                "GPTBotWindowButton",
                "IA.GPTBotWindowCommand",
                "Assistant IA",
                "AI Assistant",
                "Le chatbot est plus clair, plus stable et mieux intégré à l'interface.",
                "The chatbot is clearer, more stable, and better integrated into the interface.",
                new[]
                {
                    "Profils et messages du chatbot disponibles en français et en anglais.",
                    "État de l'accès aux fonctions IA présenté plus clairement.",
                    "Erreurs réseau et indisponibilités mieux signalées à l'utilisateur.",
                    "Analyse de la sélection Revit et changement de profil rendus plus robustes."
                },
                new[]
                {
                    "Chatbot profiles and messages available in French and English.",
                    "Clearer display of AI feature availability.",
                    "Clearer reporting of network errors and service outages.",
                    "More robust Revit selection analysis and profile switching."
                }),
            Create1063Note(
                "RealisticViewImage",
                "IA.RealisticViewImageCommand",
                "Rendu plan IA",
                "AI View Rendering",
                "La génération d'images utilise le nouveau flux de rendu et des contrôles plus clairs.",
                "Image generation now uses the new rendering flow and clearer controls.",
                new[]
                {
                    "Création d'une variante réaliste à partir de la vue active améliorée.",
                    "Préparation de l'image source optimisée avant l'envoi.",
                    "Progression et erreurs présentées plus clairement dans les deux langues."
                },
                new[]
                {
                    "Improved creation of a realistic variation from the active view.",
                    "Optimized source-image preparation before upload.",
                    "Clearer progress and error messages in both languages."
                }),
            Create1063Note(
                "GuideCommand",
                "Page.GuideCommand",
                "Interface BIMaestro",
                "BIMaestro Interface",
                "La version 1.0.6.3 introduit une interface bilingue et une expérience plus cohérente.",
                "Version 1.0.6.3 introduces a bilingual interface and a more consistent experience.",
                new[]
                {
                    "Choix du français ou de l'anglais appliqué au ruban, aux fenêtres et aux principaux messages.",
                    "Thème, contrastes, titres et boutons harmonisés dans les outils principaux.",
                    "Accès aux pages d'aide simplifié.",
                    "Fenêtre de bienvenue, contact, mise à jour et assistance retravaillés."
                },
                new[]
                {
                    "French or English can be applied to the ribbon, windows, and main messages.",
                    "Consistent theme, contrast, titles, and buttons across the main tools.",
                    "Simplified access to help pages.",
                    "Reworked welcome, contact, update, and support experience."
                }),
            Create1063Note(
                "SupportCommand",
                "Page.SupportCommand",
                "Soutenir BIMaestro",
                "Support BIMaestro",
                "Un nouveau bouton permet de soutenir volontairement le développement du plugin.",
                "A new button makes it possible to voluntarily support the plugin's development.",
                new[]
                {
                    "Accès direct à la page Ko-fi depuis le ruban BIMaestro.",
                    "Le soutien reste entièrement facultatif."
                },
                new[]
                {
                    "Direct access to the Ko-fi page from the BIMaestro ribbon.",
                    "Support remains entirely optional."
                }),
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

        private static ButtonUpdateNote Create1063Note(
            string buttonId,
            string commandClass,
            string title,
            string englishTitle,
            string summary,
            string englishSummary,
            IEnumerable<string> changes,
            IEnumerable<string> englishChanges)
        {
            return new ButtonUpdateNote
            {
                Version = Release1063,
                ButtonId = buttonId,
                CommandClass = commandClass,
                Title = title,
                EnglishTitle = englishTitle,
                Summary = summary,
                EnglishSummary = englishSummary,
                Changes = new List<string>(changes),
                EnglishChanges = new List<string>(englishChanges)
            };
        }
    }
}
