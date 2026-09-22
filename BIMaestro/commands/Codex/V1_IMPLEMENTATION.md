# Moteur de familles V1 — périmètre et validation

Cible corrigée : Revit **2023 et suivants**. Cette V1 est un moteur déclaratif généraliste ; elle ne donne pas un accès arbitraire à toute l'API Revit. Les outils exposent leurs capacités avec `revit_capabilities` pour éviter que l'assistant conserve d'anciennes limitations dans son discours.

## Fonctions intégrées

| Domaine | Contrat implémenté |
| --- | --- |
| Paramètres | Longueur, angle, entier, nombre, Oui/Non, texte ; type ou occurrence ; descriptions et groupes ; paramètres partagés lorsque le GUID est explicitement fourni |
| Formules | Calculs typés, comparaisons, `if`, `and`, `or`, `not`, arrondis, racine et trigonométrie ; unités contrôlées, références inconnues et cycles refusés ; formules conservées dans Revit |
| Types | Plusieurs types nommés avec valeurs propres ; essais de variation et retour au type initial |
| Visibilité | Paramètre Oui/Non, formule conditionnelle, sous-catégorie, grossier/moyen/fin et directions de vue par composant ; une pièce masquée doit rester valide |
| Extrusions | Rectangles avec ouvertures rectangulaires ; profils polygonaux simples de 3 à 24 sommets pilotés par des longueurs, sans ouvertures dans ce second mode |
| Répétitions | Réseaux rectilignes de barres imbriquées, quantité calculée par pas ou paramètre entier ; inclinaison réglable de 1 à 89 degrés ; paramètres associés aux occurrences |
| Zéro/un | Solution commune 2023+ par visibilité du réseau et d'une barre isolée ; les géométries cachées restent présentes. Le compte visible n'est pas une quantité physique supprimée |
| MEP | Connecteurs natifs au centre d'une face rectangulaire pleine, section pilotée par paramètres, direction et système ; catégories compatibles contrôlées |
| Représentation 2D | Contours symboliques rectangulaires contraints en XY/XZ/YZ avec détail et visibilité ; pas de génération générale de masques |
| Hébergement | Gabarits libre, face, mur, plafond et plan de travail selon disponibilité locale. Chargement possible, placement sur l'hôte ensuite dans Revit |
| Famille existante | Lecture des paramètres réels, formules et types ; modification de plusieurs valeurs dans une seule transaction annulable |
| Contrôle | Validation sans enregistrer de RFA ; variations des paramètres, types, seuils et booléens ; vérification des dimensions, volumes, connecteurs et contours ; rapport local |

Le champ facultatif `family_options` active le contrat typé, avec `parameters`, `types` et `representations`. Les anciennes descriptions restent acceptées. `profile_uv`, `connectors`, `symbolic_outlines` et `hosting` complètent le descriptif. Les exemples exécutables sont dans `scripts/codex-tests/native-fixtures/`.

Les cotes internes sont mutualisées lorsque leurs expressions sont identiques. Les paramètres `BIM_` restent des calculs techniques ; ils ne doivent pas être présentés comme autant de réglages utiles à l'utilisateur. Les matériaux disposent déjà de paramètres de type et de couleurs de rendu simples.

## Vérification

État au 15 septembre 2026, après correction des diagnostics : les builds complets 2023/2024 réussissent et 114 contrôles automatisés passent, dont les neuf demandes réelles initialement rejetées, le protocole avec le Codex officiel et les rendus WPF. Le banc natif Revit 2023 passe **7 scénarios sur 7**, avec leurs variations, dans le rapport `Revit-2023-20260915-131954.json` : réseaux inclinés et centrés, connecteurs/contours 2D, grille 600 × 800 issue des diagnostics, profil polygonal, ouvertures rectangulaires, répétitions 0/1 et visibilités/types. Le premier rapport ne passait que 1 scénario sur 6 ; il est remplacé par ce nouvel état. La validation native 2025+ et les contrôles en projet restent ouverts.

Corrections associées : utilisation de l'identifiant du plan de référence pour créer le plan de travail ; absence de renommage d'un type vers son propre nom ; sommets des lames inclinées pilotés par formules trigonométriques ; résolution fraîche des membres après régénération ; ancrage des deux premiers membres sur les trois axes pour éviter le décalage des réseaux centrés. Pour les membres masqués, Revit ne renvoie pas les solides : les valeurs, positions et visibilités sont contrôlées, tandis que les volumes et orientations sont mesurés pour les membres visibles. Les rapports distinguent ces contrôles.

`scripts/codex-tests/Run.ps1 -RealCodex` vérifie hors Revit le contrat, les formules, les refus de données invalides, le protocole app-server et le panneau WPF. Le test app-server n'envoie pas de demande à un modèle.

Les scénarios natifs intégrés restent disponibles pour le diagnostic du moteur, sans bouton dans le panneau Famille IA. Chaque scénario crée puis ferme une famille temporaire ; aucun projet n'est modifié et aucun RFA n'est sauvegardé. Le rapport est écrit dans `%LOCALAPPDATA%\BIMaestro\Codex\Validation\`.

Le banc autonome `BuildNativeHarness.ps1` / `StartNativeHarness.ps1` permet aussi d'utiliser une nouvelle instance 2023 ou 2024. Revit peut demander de charger une fois le complément non signé. Le banc refuse de démarrer si cette instance contient déjà un document. L'inscription temporaire est retirée après le démarrage ou le délai d'attente.

Une compilation ou une validation de données n'est pas un essai natif. Les rapports natifs indiquent les scénarios effectivement exécutés ; le chargement, les occurrences dans un projet, les connexions métier et le rendu des vues demandent des vérifications supplémentaires. Une variation testée avec succès ne garantit pas toutes les valeurs futures.

## Builds et limites restantes

- `BIMaestro.csproj` : .NET Framework 4.8, configuration `Release` pour 2023, `Release2024` pour 2024.
- `BIMaestro.Revit2025.csproj` : projet .NET 8 séparé, version configurable avec `RevitVersion`. Sa migration complète reste à valider : la restauration locale manque notamment de `System.IO.FileSystem.Primitives >= 4.3.0`, et les anciennes dépendances WPF doivent être vérifiées. Ne pas distribuer le binaire 2023 comme un binaire 2025+.
- Non exposés : profils courbes paramétriques, balayages/lofts/révolutions paramétriques, réseaux radiaux ou 2D, familles imbriquées interchangeables générales, points adaptatifs, coupes d'hôtes automatiques, actifs d'apparence de matériaux complets et dimensionnement électrique détaillé.
- Les longueurs géométriques doivent rester positives, les coordonnées contraintes conserver leur signe et les profils rester valides pendant les essais. Les cas limites sont refusés avant sauvegarde ; la visibilité n'autorise pas une géométrie dégénérée.

Ces limites restent explicites dans le dialogue. Une demande qui en dépend doit donner lieu à une question ciblée, et non à une simplification silencieuse.
