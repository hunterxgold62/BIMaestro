# Programmes de familles Revit

Les outils spécialisés ne constituent plus le catalogue complet des opérations
possibles. `revit_run_family_program` compose des opérations natives pour créer
et modifier la famille ouverte, y compris une famille construite manuellement.
Il interprète un graphe JSON ; il n'exécute pas de code C# ou Python arbitraire.

## Parcours de l'assistant

1. Inspecter la famille et relever son `document_key` et les identifiants utiles.
2. Utiliser les outils spécialisés lorsqu'ils couvrent la demande. Sinon lire
   `revit_family_program_contract`, puis `revit_family_api` pour découvrir les
   signatures de la version de Revit en cours avant de déclarer une limitation.
3. Construire `program_json`, un objet avec `steps` et `outputs`. Les références
   `{ "ref": "nom" }` désignent les résultats nommés par `id`.
4. Appeler `revit_run_family_program` avec `description`, `document_key`,
   `program_json` et `validate_only=true`. Le test est entièrement annulé.
5. Appliquer le même programme avec `validate_only=false`, puis inspecter le
   résultat. Ne pas réutiliser les identifiants temporaires du test.

Les références initiales sont `doc`, `manager`, `family` et `factory`. Les
opérations couvrent constructeurs, méthodes, propriétés, enums, listes typées,
boucles, conditions, calculs et assertions. `mm` et `xyz_mm` convertissent les
millimètres ; les autres appels utilisent les unités internes Revit.
Une surcharge ambiguë exige `signature` avec les types exacts fournis par
la découverte. Les méthodes génériques et paramètres `ref`/`out` ne sont pas pris
en charge.

`nested` crée une famille temporaire puis la charge sans fichier intermédiaire.
Avec `source_family`, il édite une famille imbriquée existante et la recharge.
`overwrite_parameter_values=false` conserve les valeurs de types du parent.
Revit peut remplacer l'identifiant de la définition pendant le rechargement :
relire les identifiants après application. Les instances placées et leurs types
sont contrôlés ; leur perte ou la création d'un doublon annule le programme.
Chaque famille imbriquée possède son propre contexte d'objets. Les éléments
du parent ne peuvent pas être transmis comme éléments du document enfant.

## Portée et garanties

Les formes creuses, révolutions et réseaux ne sont plus limités aux primitives
rectangulaires des outils spécialisés. Paramètres, associations et visibilité
restent également accessibles via les outils de configuration existants.
Les possibilités restent celles de l'API native exposée : cela ne garantit pas
que toute commande de l'interface Revit soit programmable ou toute contrainte
modifiable. Une erreur doit être diagnostiquée, pas contournée en reconstruisant
silencieusement la famille.

Les écritures respectent l'autorisation de modification de la passerelle.
Elles sont regroupées en une annulation Revit ; une erreur annule le programme,
y compris ses imports imbriqués. Aucun enregistrement ni rechargement du projet
n'est automatique. Le descriptif historique `construction.json` n'est pas réécrit.
Le graphe refuse l'accès à l'application, aux autres documents, aux transactions,
aux fichiers, au réseau, à l'impression et aux exports.

Limites : 1 500 étapes décrites, 10 000 exécutées, 200 itérations par boucle,
1 000 objets par collection et trois niveaux d'imbrication. Les courbes bornées
doivent mesurer au moins 1 mm et respecter la tolérance native. Le délai et
l'annulation sont coopératifs : un appel synchrone au noyau Revit ne peut pas
être interrompu immédiatement de manière garantie.

## Banc natif

Compiler `BuildNativeHarness.ps1 -RevitVersion 2024 -RunName <nom> -FamilyProgramOnly`,
puis lancer `StartNativeHarness.ps1 -RevitVersion 2024 -RunName <nom>`.
Le banc utilise une instance Revit séparée et des documents temporaires.
`FamilyProgramNativeTests.cs` construit le graphe complet d'un service avec verre
creux et assiette de révolution, le répète en deux réseaux au pas de 900 mm,
fait varier le nombre, retouche le verre imbriqué et vérifie les annulations.
Il écrit les programmes JSON exécutés et le rapport dans
`tmp/codex-native-validation/<version>-<nom>`.

Validation du 19 septembre 2026 : `2024-family-program-v5/result.json` réussit
tous ces essais, notamment le passage de six à huit services par paramètre,
la modification/recharge du verre à 120 mm avec conservation des identifiants
des instances, les boucles/calculs, la découverte de l'API et l'annulation après
une mutation suivie d'une erreur. Ces essais ne certifient pas toutes les
combinaisons d'API ni toutes les versions de Revit.
