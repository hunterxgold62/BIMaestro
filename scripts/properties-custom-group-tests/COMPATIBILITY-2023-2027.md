# Groupes personnalisés — compatibilité Revit 2023 à 2027

Correction du 26 octobre 2026. Le pont natif refusait explicitement toute version autre que 2025, avec trois empreintes SHA256 propres à 2025. Les profils étaient enregistrés mais les bandeaux n'étaient pas insérés dans les autres versions.

`NativeCustomGroupRows` possède désormais un jeu d'empreintes et une liaison aux exports pour chaque version installée. Les bibliothèques inconnues restent refusées avant insertion.

| Version | Adaptation |
| --- | --- |
| 2023 | Fabrique native avec `ElementId` 32 bits invalide, puis titre appliqué uniquement à la nouvelle cellule d'en-tête par `SetValueRange`. |
| 2024 | Fabrique native avec `ForgeTypeId` vide. Le constructeur natif initialise le temporaire ; la fonction appelée par valeur le détruit. Même affectation ciblée du titre. |
| 2025–2026 | Fabrique native acceptant directement une référence `AString`. |
| 2027 | Export texte renommé avec `NotNull<int*>`. Le désassemblage confirme que le pointeur de curseur reste passé directement dans RDX. |

Les anciennes versions utilisent un `CGXRange` de 24 octets initialisé par son constructeur exporté. Aucun style emprunté n'est modifié, aucune définition ni valeur de paramètre du modèle n'est réécrite par la personnalisation. Les contrôles existants d'édition, de nombre de lignes, d'en-tête natif, d'identité de paramètre et de restauration sont conservés.

## Vérification en véritables instances Revit

Chaque série exécute création de deux groupes, renommage, transfert d'un membre, suppression d'un groupe, puis suppression de tous les groupes. Le collecteur compare les valeurs, les définitions, les identités visibles, les limites physiques des groupes et le retour exact aux lignes originales.

| Version | Mur | Plan de structure | Valeurs conservées par étape |
| --- | --- | --- | --- |
| 2023 | 27 identités, 5 étapes réussies | 35 identités, 5 étapes réussies | 18 164 |
| 2024 | 27 identités, 5 étapes réussies | 35 identités, 5 étapes réussies | 18 251 |
| 2025 | 27 identités, 5 étapes réussies | Non réexécuté dans cette série | 18 526 |
| 2026 | 27 identités, 5 étapes réussies | Non réexécuté dans cette série | 18 659 |
| 2027 | 28 identités, 5 étapes réussies | Non réexécuté dans cette série | 18 620 |

Les 35 étapes ont réussi. Rapports sous `tmp/g2023-r2/wall`, `tmp/g2023-r2`, `tmp/g2024-r2/wall`, `tmp/g2024-r2` et `tmp/g2025-r2` à `tmp/g2027-r2`. Les essais utilisent des documents jetables et des préférences isolées. Les sessions de test ont reçu la commande de fermeture et les cinq manifestes de compatibilité ont été retirés.

Ces essais structurels ne constituent pas une validation des interactions clavier, du repli/dépli ou de toutes les mises à jour possibles des bibliothèques Revit.

## Compilations complètes

Les cinq modules complets sont compilés sans erreur dans `tmp/groups-production/2023` à `2027`. Les avertissements existants restent présents. Pour 2023–2024, utiliser MSBuild 64 bits ; pour 2027, le SDK déjà disponible dans `tmp/dotnet10` permet de cibler .NET 10.

`tmp/groups-production/validation.json` contient les empreintes des DLL et du pont source ainsi que la synthèse des essais natifs. Les manifestes de production n'ont pas été remplacés : les compilations sont prêtes pour une installation distincte.

## Reproduire

- `python scripts/properties-custom-group-tests/InspectCompatibility.py` : vérifier les empreintes exactes et les exports nécessaires sans charger les DLL Autodesk.
- `BuildCompatibility.ps1 -RevitVersion 2023 -OutputTag '-nouvel-essai'` : compiler la sonde isolée. Réutiliser un dossier contenant déjà une sonde est refusé.
- `StartCompatibility.ps1` avec les mêmes arguments : enregistrer temporairement cette sonde et lancer une nouvelle instance de Revit. Le contrôle est limité au PID cible et au chemin exact du document jetable.
- Utiliser `Send.ps1 -Run '../g2023-nouvel-essai'` pour les commandes `fixture`, `select-wall`, `custom-state`, `custom-create`, `custom-rename`, `custom-transfer`, `custom-delete`, `custom-clear`, puis `shutdown-fixture`. Conserver les captures initiale/créée/effacée avec `-Evidence original`, `created`, `cleared`.
- Passer le dossier des rapports à `VerifyConfigurableGroups.py`, puis retirer la sonde avec `CleanupCompatibility.ps1`.

La lecture des rapports JSON volumineux attend désormais la fin de leur écriture, afin de ne pas prendre un fichier partiel pour un échec du moteur.
