using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace BIMaestro.ModelLoading
{
    internal static class ModelOpeningTips
    {
        internal sealed class Tip
        {
            internal readonly string Id, Title, Message;
            internal Tip(string id, string title, string message) { Id = id; Title = title; Message = message; }
        }

        // Short, local tips based on the ribbon's existing tools. No telemetry or network lookup.
        private static readonly Tip[] Items =
        {
            new Tip("active-time", "Votre temps est déjà suivi", "Votre suivi du temps tient déjà compte des périodes d’inactivité."),
            new Tip("time-dashboard", "Retrouvez vos sessions", "« Temps par projet » rassemble vos sessions et votre temps actif par maquette."),
            new Tip("cached-preview", "Un aperçu sans nouveau calcul", "L’image affichée ici provient de vos aperçus enregistrés, sans nouveau calcul 3D pendant l’ouverture."),
            new Tip("schedule-excel", "Vos nomenclatures et Excel", "« Gestion Excel » permet d’exporter ou d’importer une nomenclature au format Excel."),
            new Tip("view-overrides", "Une sélection, plusieurs vues", "« Surcharges » applique la demi-teinte, la transparence ou le masquage dans les vues choisies."),
            new Tip("sheet-overrides", "Les surcharges depuis une feuille", "Dans « Surcharges », sélectionner une feuille permet de cibler les vues placées sur cette feuille."),
            new Tip("rename", "Renommer sans tout ressaisir", "L’Organisateur renomme les éléments sélectionnés avec préfixes, suffixes et numérotation."),
            new Tip("viewport-numbers", "Numéroter les vues d’une feuille", "Sur une feuille, l’Organisateur peut numéroter les fenêtres de vue de haut en bas, puis de gauche à droite."),
            new Tip("phases", "Ajustez rapidement les phases", "L’outil « Phases » modifie la phase de création et la phase de démolition des objets sélectionnés."),
            new Tip("cleanup-choice", "Choisir ce que l’on purge", "La purge BIMaestro permet de choisir les vues non placées, familles et nomenclatures à supprimer."),
            new Tip("pipe-lengths", "Des quantités par diamètre", "« Calcul des canalisations » regroupe les longueurs par diamètre et permet d’exporter les résultats dans Excel."),
            new Tip("pipe-filter", "Ciblez le système à quantifier", "Le calcul des canalisations propose un filtre par type de système et peut inclure les gaines."),
            new Tip("weight-analysis", "Repérez les familles volumineuses", "« Analyse de Poids » classe les familles par taille et indique leur nombre d’instances."),
            new Tip("history-filter", "Retrouvez une action enregistrée", "Dans « Qui a fait ça ? », filtrez l’historique par action, utilisateur ou recherche."),
            new Tip("history-selection", "Le dernier changement connu", "L’option « Infos objet » affiche le dernier changement enregistré après la sélection d’un objet."),
            new Tip("clash-scope", "Un contrôle des conflits ciblé", "« Clash 3D » peut analyser la maquette, la vue active ou une sélection, puis présenter les résultats en 3D."),
            new Tip("family-favorites", "Retrouvez vos familles favorites", "Le Navigateur de Familles propose une recherche, des aperçus et des favoris pour retrouver vos contenus."),
            new Tip("family-radial", "Vos familles en rosace", "La rosace des familles peut être associée à un raccourci clavier ou à un raccourci souris."),
            new Tip("units-save", "Gardez vos réglages d’unités", "Les outils « Unités » enregistrent et rechargent les unités du projet ainsi que leur précision."),
            new Tip("properties-look", "Une palette Propriétés à votre goût", "La personnalisation des Propriétés permet de modifier le texte et le fond des groupes ou paramètres."),
            new Tip("text-correction", "Gardez la main sur les corrections", "« Corr. IA » permet de valider, modifier ou ignorer chaque suggestion sur les textes sélectionnés."),
            new Tip("text-audit", "Un audit texte plus ciblé", "Pour accélérer l’Audit IA, sélectionnez seulement les vues ou feuilles dont vous voulez contrôler les textes."),
            new Tip("dynamo-buttons", "Vos scripts Dynamo à portée de main", "Les boutons Dynamo Auto sont configurables pour lancer vos scripts habituels depuis le ruban."),
            new Tip("default-flange", "Choisissez votre bride par défaut", "Dans « Bride auto », l’option « Choix bride » permet de sélectionner la bride utilisée par défaut.")
        };

        private static readonly object Gate = new object();
        private static readonly Random Random = new Random();
        private static readonly Queue<int> Bag = new Queue<int>();
        private static string _lastId, _statePath;
        private static Task _save = Task.CompletedTask;
        internal static int Count => Items.Length;

        // Called once on the opening UI worker, before its dispatcher accepts document events.
        internal static void Initialize(string statePath)
        {
            lock (Gate)
            {
                _statePath = statePath;
                try { _lastId = File.Exists(statePath) ? File.ReadAllText(statePath).Trim() : null; }
                catch { _lastId = null; }
                Bag.Clear();
            }
        }

        internal static Tip Next()
        {
            lock (Gate)
            {
                if (Bag.Count == 0)
                {
                    var order = new int[Items.Length];
                    for (int i = 0; i < order.Length; i++) order[i] = i;
                    for (int i = order.Length - 1; i > 0; i--)
                    {
                        int other = Random.Next(i + 1), swap = order[i]; order[i] = order[other]; order[other] = swap;
                    }
                    // Also avoid a repeat at a shuffle boundary or after restarting Revit.
                    if (order.Length > 1 && Items[order[0]].Id == _lastId)
                    {
                        int other = Random.Next(1, order.Length), swap = order[0]; order[0] = order[other]; order[other] = swap;
                    }
                    foreach (int index in order) Bag.Enqueue(index);
                }
                var tip = Items[Bag.Dequeue()]; _lastId = tip.Id;
                if (!string.IsNullOrEmpty(_statePath))
                {
                    string file = _statePath, id = tip.Id;
                    // Keep disk writes ordered and away from the opening/repainting threads.
                    _save = _save.ContinueWith(_ =>
                    {
                        try { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, id); }
                        catch { }
                    }, TaskScheduler.Default);
                }
                return tip;
            }
        }
    }
}
