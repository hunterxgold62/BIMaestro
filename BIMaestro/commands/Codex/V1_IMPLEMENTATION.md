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

État au 15 septembre 2026 : les builds complets 2023/2024 réussissent et 103 contrôles automatisés passent, dont le protocole avec le Codex officiel et les rendus WPF. Le lancement isolé de Revit 2023 s'est arrêté sur `TaskDialog_Security_Unsigned_File_Loading` avant l'entrée dans le banc : aucun scénario natif ne doit être déclaré réussi sur cette base. La validation native et la compatibilité 2025+ restent ouvertes.

`scripts/codex-tests/Run.ps1 -RealCodex` vérifie hors Revit le contrat, les formules, les refus de données invalides, le protocole app-server et le panneau WPF. Le test app-server n'envoie pas de demande à un modèle.

Dans le panneau, **Installation Codex → Tester le moteur de familles** exécute les scénarios intégrés via le contexte API Revit. Il faut autoriser les créations, mais aucune connexion ChatGPT n'est nécessaire. Chaque scénario crée puis ferme une famille temporaire ; aucun projet n'est modifié et aucun RFA n'est sauvegardé. Le rapport est écrit dans `%LOCALAPPDATA%\BIMaestro\Codex\Validation\`.

Le banc autonome `BuildNativeHarness.ps1` / `StartNativeHarness.ps1` permet aussi d'utiliser une nouvelle instance 2023 ou 2024. Revit peut demander de charger une fois le complément non signé. Le banc refuse de démarrer si cette instance contient déjà un document. L'inscription temporaire est retirée après le démarrage ou le délai d'attente.

Une compilation ou une validation de données n'est pas un essai natif. Les rapports natifs indiquent les scénarios effectivement exécutés ; le chargement, les occurrences dans un projet, les connexions métier et le rendu des vues demandent des vérifications supplémentaires. Une variation testée avec succès ne garantit pas toutes les valeurs futures.

## Builds et limites restantes

- `BIMaestro.csproj` : .NET Framework 4.8, configuration `Release` pour 2023, `Release2024` pour 2024.
- `BIMaestro.Revit2025.csproj` : projet .NET 8 séparé, version configurable avec `RevitVersion`. Sa migration complète reste à valider : la restauration locale manque notamment de `System.IO.FileSystem.Primitives >= 4.3.0`, et les anciennes dépendances WPF doivent être vérifiées. Ne pas distribuer le binaire 2023 comme un binaire 2025+.
- Non exposés : profils courbes paramétriques, balayages/lofts/révolutions paramétriques, réseaux radiaux ou 2D, familles imbriquées interchangeables générales, points adaptatifs, coupes d'hôtes automatiques, actifs d'apparence de matériaux complets et dimensionnement électrique détaillé.
- Les longueurs géométriques doivent rester positives, les coordonnées contraintes conserver leur signe et les profils rester valides pendant les essais. Les cas limites sont refusés avant sauvegarde ; la visibilité n'autorise pas une géométrie dégénérée.

Ces limites restent explicites dans le dialogue. Une demande qui en dépend doit donner lieu à une question ciblée, et non à une simplification silencieuse.
