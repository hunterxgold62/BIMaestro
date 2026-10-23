# Validation Antibes — 22 octobre 2026

## Résultat et version installée

La protection d’ouverture et le parcours de production ont été validés sur une
copie de `Projet_AntibesV2.rvt`, dans des processus Revit 2025 isolés. Les preuves
combinent le parcours complet v6 et les trois cas ciblés v7, relus par un validateur
hors Revit corrigeant un défaut de périmètre du banc. Ce validateur réussit ses
27 assertions. Les résultats bruts restent conservés avec leur statut initial ;
ils ne sont pas réécrits en succès.

SHA256 de la DLL de production testée et installée :
`0541129F75C04CA2C728A43BC2039227E7479163F4A80C94FC3F8618CDDFEFC3`.

Installation vérifiée :
`C:/Users/plemert/AppData/Local/BIMaestro/Bin/Revit2025-History-0541129F75C0/BIMaestro.dll`.
Le manifeste Revit 2025 pointe vers ce paquet de 262 fichiers, 174 624 688 octets.
La version sera chargée au **prochain lancement de Revit 2025**.
L’ancien paquet `Revit2025-Maintenance-547902D7F557` est conservé et le manifeste
précédent est sauvegardé sous
`BIMaestro.addin.before-history-2025-20261022-151415-c6849798.bak`.
La trace exacte se trouve dans `tmp/codex-native-validation/2025-history-antibes-opening-v7/installation.txt`.

## Mesures du parcours complet v6

Run `tmp/codex-native-validation/2025-history-antibes-opening-v6`, PID 76744.
Le RVT de test mesure 278 659 072 octets. Chaque scénario de suppression et
restauration est annulé par un groupe de transactions ; le document se ferme
sans sauvegarde. SHA256 avant/après, également vérifié en v7 :
`EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA`.

| Mesure | Valeur v6 |
| --- | ---: |
| Ouverture Revit de la copie | 9,895 s |
| Appel de programmation de la copie d’ouverture | 0,735 ms |
| Copie d’ouverture disponible, avant tout scan | 326,243 ms |
| Taille de chaque archive d’ouverture | 278 659 072 octets |
| Candidats parcourus | 6 113 |
| Snapshots conservés | 4 197 |
| Candidats exclus par la politique de suivi | 1 916 |
| Échecs de capture dans l’audit | 0 |
| Temps cumulé API du parcours brut | 6,820 s |
| Parcours brut, sérialisation comprise | 7,381 s |
| Recettes directes | 1 042 |
| Références à la copie d’ouverture | 490 |
| Recettes/références disponibles au total | 1 532 |
| Fin du parcours planifié, cadence explicite de 50 ms | 17,882 s |
| Appels / lots de capture | 252 / 231 |
| Temps API actif cumulé dans l’audit planifié | 5,049 s |
| P95 des appels planifiés | 34,708 ms |
| Appel planifié maximal | 54,471 ms |
| Appels dépassant 100 ms | 0 |

Les 4 197 snapshots comprennent des données de suivi et des métadonnées :
1 706 matériaux et 686 types de famille sont capturés sans recette. Les compteurs
ne représentent donc pas un taux de restauration de tous les objets du RVT.
Le détail par classe/catégorie est conservé dans `opening-benchmark.json`.

La mesure planifiée utilise les véritables `ScheduleDeferredPrime` et
`ProcessDeferredPrime`, après le parcours brut : caches Revit et paramètres de
type déjà chauds, table des snapshots de production initialement vide.
C’est un contexte isolé, sans navigation utilisateur simultanée. Ces chiffres
ne mesurent pas la fluidité d’une session interactive ni un démarrage à cache froid.
La protection par copie du RVT est indépendante de la fin du parcours.

## Contrôles produit réussis

- Cinq racines supprimées et restaurées **avant tout scan** : modèle générique,
  plante, mur, ossature en place et porte hébergée. Classe, type et placement
  sont contrôlés à `1e-5` pied ; l’archive reste identique octet pour octet.
- Une recette sérialisée avec `Ready=false` est utilisable après publication du
  fichier final. Le document historique est reconnu comme archive lors de son
  ouverture et de sa fermeture.
- Trois suppressions sans snapshot passent par les vrais `EnqueueDeleted`, JSON
  et `ToRestoreRequest`. Les identités absentes sont résolues séparément ; aucun
  regroupement ne fusionne ces éléments.
- IDs ajoutés/modifiés et types modifiés refusent l’ancienne copie. Une recette
  conservée avant modification du type est aussi refusée lors de son gel.
- Trente recettes représentatives passent par JSON, suppression réelle et
  restauration avec classe/type/placement exacts. Les trente réussissent en v6,
  avec **zéro ouverture d’archive** pour ces restaurations directes.

Pour la famille en place « Ossature 1 », Revit supprime aussi le type 556714 lors
de la suppression de l’instance et recrée ce type sous un nouvel ID. Le contrôle
accepte ce changement seulement si la famille est en place, le type est réellement
supprimé et absent après suppression, les noms famille/type sont identiques et
la géométrie reste identique. Les distances des coins de boîte valent zéro.

Le test d’identité de document a également révélé que `element.Document == doc`
pouvait être faux alors que `element.Document.Equals(doc)` était vrai. La garde
utilise désormais `Equals`, sans supprimer les protections concernant l’état
non modifié et le fichier enregistré.

## Cas ciblés v7 et normalisation explicite

Run `tmp/codex-native-validation/2025-history-antibes-opening-v7`, PID 59500,
avec exactement la même DLL de production. Ce run cible les deux défauts révélés
par les premières mesures, puis une panne volontaire de recette.

La poutre IPN200
`2f0d3229-e77f-4d56-8aca-0a52aee986c1-000f6c7f` est restaurée directement après
retrait de son secours d’ouverture dans le DTO de test : zéro archive ouverte,
type identique, extrémités et milieu identiques. La correction écarte uniquement
les décalages de placement END0/END1 redondants pour une famille structurelle
pilotée par un axe absolu, avec vérification stricte de cet axe.

Une seconde recette clonée reçoit un GUID de type inexistant, sans modifier la
source. Son secours d’ouverture reste valide : une seule archive est ouverte,
la poutre est restaurée exactement, et `OpeningFallbackTrigger` conserve la cause
`type`. Cela vérifie le secours indépendamment du succès de la recette directe.

La gaine `ee974953-383f-4838-bbc8-67cddf0cf5e4-000939c6` avait un type de système
`-1`, aucun `MEPSystem`, deux connexions physiques et des connecteurs HVAC
`ExhaustAir`. Ses deux voisins HVAC étaient eux aussi sans système défini.
Un équipement voisin comporte également deux ports hydrauliques : ils sont
explicitement exclus du contrôle HVAC.

Revit attribue `CML_Fumées` (ID 313805, classification `ExhaustAir`) dès la copie
native, avant la reconnexion explicite. Les deux liens physiques sont ensuite
rétablis et les voisins HVAC possèdent le même type final. Le type d’élément,
les coordonnées et les identifiants des voisins restent exacts ; la classification
de chaque port physique HVAC reste identique. **L’affectation au système n’est
pas identique à l’état indéfini d’origine.** `SystemTypeAdjustment` l’enregistre
avec `ClassificationPreserved=false` et la note visible :

> Revit a rattaché cette gaine au système des voisins reconnectés ; aucun système n’était défini dans l’historique.

Aucun système arbitraire n’est choisi par le code. Pour un type historique valide,
le contrôle exige toujours son UID exact. La normalisation de ce cas indéfini
est vérifiée séparément, avec diagnostic explicite.

Une tentative **uniquement diagnostique**, dans un groupe toujours annulé, montre
que `parameter.Set(InvalidElementId)` peut réussir et garder les deux connexions.
Elle modifie toutefois le système logique partagé : son type devient indéfini et
son nom passe de `FUM 1` à `Génie climatique Non défini 1`. Le rollback rétablit
le système, son nom et les deux liens. Aucun setter de ce genre n’a été ajouté
au produit pour masquer la normalisation.

## Résultats bruts et validation corrigée

Les fichiers `result.json` bruts v6 et v7 conservent `passed: false` :

- En v6, le diagnostic de remise à zéro du système annulait une transaction, puis
  le banc tentait de lire un ancien wrapper `Curve`, devenu invalide. Les contrôles
  complets et les trente restaurations directes avaient réussi.
- En v7, ce défaut de banc est corrigé, mais l’agrégateur du prédicat incluait
  encore les ports hydrauliques du voisin mixte dans les types HVAC et exigeait
  un type HVAC historique unique alors que l’état HVAC d’origine était indéfini.

Le produit est resté inchangé entre ces deux runs. Le prédicat du banc est corrigé
et une relecture automatisée des données v7 déjà enregistrées applique les critères
pertinents, sans nouveau lancement Revit et sans modifier les fichiers bruts :

```powershell
./scripts/history-tests/ValidateRecordedSystemEvidence.ps1
```

Résultat séparé :
`tmp/codex-native-validation/2025-history-antibes-opening-v7/recorded-system-validation.json`.
Il contient `validationPassed: true`, les **27 assertions réussies**,
`rawResultPassed: false`, le SHA du produit et les SHA des deux fichiers bruts
vérifiés inchangés.

Les voisins sont réellement issus de `CaptureConnections`. V7 ne sérialise pas
l’ID du port voisin de chaque paire de connexion ; le validateur ne l’invente pas.
Il vérifie une portée plus forte pour les classifications : tous les ports
physiques HVAC des deux vrais voisins, avec les mêmes IDs de ports, classifications
et états connectés, plus le même ensemble d’UID voisins et les deux liens de gaine.
Les deux ports hydrauliques sont exclus. Aucun type HVAC historique valide n’est
écrasé ; le type final HVAC des voisins est unique et correspond au résultat.

## Reproduction et limites

Les sources du banc sont `AntibesBenchmark.cs`, `HistoryNativeTests.cs`,
`Build.ps1` et `HistoryNative2025.csproj`. Le mode complet utilise
`opening-benchmark.txt`, une copie `real-model-source.rvt` et un chemin absolu
`production-dll.txt`. Le mode ciblé ajoute `targeted-regressions-only.txt`.
Le lanceur vérifie le PID autorisé et exige un processus sans document utilisateur.

Le paquet produit figé utilisé pour l’installation est conservé sous
`tmp/codex-native-validation/2025-history-antibes-opening-v7/validated-product`.
Son SHA est identique à celui des deux runs et de la version installée.

La copie protège les octets d’un RVT local déjà enregistré, sans ouvrir ni sauvegarder
un deuxième document Revit en arrière-plan. Les mesures proviennent de cette
machine et de son disque local ; elles ne prédisent pas un partage réseau.
Chaque ouverture ajoute une archive de la taille du RVT. Les fichiers utilisés
par l’historique doivent rester disponibles.

L’ouverture du document historique intervient lors d’une restauration explicite ;
les cinq restaurations natives indépendantes du banc v6 prennent environ
12 à 18 secondes chacune, ouverture de source comprise. Les recettes directes
réussies ouvrent zéro archive. Les documents sans fichier local exploitable et
les modifications antérieures à la protection ne peuvent pas utiliser l’ancienne
version enregistrée comme état exact de secours. Ces essais ne prétendent pas
valider exhaustivement la restauration de chacun des milliers d’objets du modèle.
