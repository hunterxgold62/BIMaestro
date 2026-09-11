# Essais du viewer et des réservations

## Installation

Installer la nouvelle DLL BIMaestro et redémarrer Revit. Republier la maquette avec cette version pour transmettre les identifiants persistants, les dimensions des hôtes et le repère interne Revit. Recharger le site avec Ctrl+F5.

## Points à essayer

1. **Navigation** : cliquer sur un mur, vérifier sa coloration bleue, faire une orbite, puis double-cliquer ou utiliser « Cadrer la sélection » / F. La molette avance vers le pointeur, proportionnellement à la distance. Échap libère la sélection.
2. **Réservations** : clic droit sur une face, créer une réservation. Glisser les poignées en mode Déplacer ou Redimensionner, ajuster la profondeur dans le champ. La découpe ne change qu'après Enregistrer ; Annuler conserve la réservation enregistrée auparavant.
3. **Révisions** : enregistrer une note et une réservation, puis republier la même publication. Les hôtes reconnus et de dimensions compatibles permettent le report du point. Les autres restent dans Notes / réservations avec « emplacement à vérifier » ; utiliser « Replacer sur une face » puis Enregistrer.
4. **IFC** : dans Notes / réservations, Exporter IFC. Le fichier contient les volumes de réservation confirmés, en mètres, dans le repère interne du projet Revit source. Il peut être superposé à un modèle utilisant le même repère. Il n'effectue pas de découpe dans une maquette tierce.
5. **Revit** : Exporter vers Revit, puis dans le plugin « Importer les réservations web ». Ouvrir le projet qui a servi à la publication. Les volumes sont créés dans Modèles génériques et portent leur commentaire et leurs dimensions dans Commentaires. Réimporter le même fichier met à jour les volumes identifiés, sans les dupliquer. Les absents du fichier ne sont pas supprimés. Rouvrir Maquette MEP pour voir les objets importés.

Les anciens points sans rattachement restent accessibles mais peuvent nécessiter un repositionnement. Les exports anciens sans repère doivent être republiés avant un échange IFC/Revit.

## Vérifications effectuées

- 36 tests du viewer ; contrôles TypeScript et lint ; compilation de production.
- Tests des workers de découpe et de préparation, y compris deux murs et suppression de réservation.
- Compilation du plugin et tests de régression MEP (442 assertions et tests d'export).
- Lecture et validation IFC avec IfcOpenShell, règles EXPRESS comprises ; contrôle de la géométrie et des coordonnées.
- Sauvegarde et relecture d'un rattachement sur une publication temporaire en ligne ; refus d'écriture avec le lien de consultation ; publication de test supprimée ensuite.
- Vérification visuelle de la barre d'outils et de la liste des annotations dans le navigateur local.

À valider sur le projet réel : confort de navigation, interaction avec les poignées sur des murs complexes et import dans une session Revit. Aucun essai d'import n'a modifié une maquette Revit de production pendant le développement.
