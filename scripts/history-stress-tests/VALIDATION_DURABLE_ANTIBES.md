# Historique durable Antibes — campagne du 23 octobre 2026

## Critère de réussite

La copie d’ouverture est une passerelle locale temporaire. Les éléments retenus doivent pouvoir être restaurés depuis les données JSON/GZ après fermeture du document et arrêt du processus de capture, dans un nouveau processus Revit et sans cette passerelle. La présence d’objets recréés ne vaut pas validation de leur géométrie, de leurs paramètres ou de leurs relations.

L’original réseau n’est jamais ouvert ni modifié par ce banc. Les essais partent du témoin `tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt`, SHA256 `EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA`, puis travaillent dans leurs propres répertoires. Les Revit de l’utilisateur restent ouverts et ne sont pas pilotés.

## Architecture en cours de validation

- Journal de suppression et travaux de conversion persistés ; reprise du trou entre écriture du journal et création du travail.
- Passerelle d’ouverture dans le cache local, identifiée par session et propriétaire du processus.
- Extraction des racines nécessaires et dépendances dans une capsule ; pas de sauvegarde de la maquette entière comme donnée historique.
- Capsule encodée dans un document JSON/GZ immuable, adressé par SHA256 et intégralement vérifié avant publication et matérialisation.
- Recettes de restauration autonomes publiées puis relues avant acquittement du travail. Une erreur conserve le travail et empêche la suppression automatique de la passerelle.
- Matérialisation RVT temporaire uniquement lors de l’accès Revit à une capsule ; libération après fermeture réelle du document archive.
- Conversion par petits passages API ; compression et écritures exécutées hors du thread Revit. Les appels API individuels restent indivisibles.

Ce format contient des données natives Revit compressées pour les objets dont la recette géométrique seule ne suffit pas. Il ne s’agit pas de conserver une copie complète du projet sous une autre extension.

## Preuves acquises

| Contrôle | Résultat |
|---|---|
| Stockage immuable, corruption, concurrence, chemins, reprise, intégrité | 127 assertions IO réussies (`scripts/history-tests/TestDurableStore.ps1`) |
| Lecture stricte des recettes et résolution des conversions | 31 assertions réussies (`TestDurableBridgeReads.ps1`) |
| Wrappers Document Revit équivalents, fermeture et invalidation | 32 assertions réussies (`TestDocumentWrapperLookup.ps1`) |
| Garde de sémantique réseau et provenance des types versionnés | 48 assertions réussies et compilation avec la véritable API Revit 2025 |
| Révisions des définitions, changements non enregistrés, fermeture et nouvelle session | 14 assertions réussies (`TestDefinitionRevisions.ps1`) |
| Disponibilité de restauration sans IO bloquante dans l’interface, validation bornée à deux lectures, course publication/lecture | 20 assertions réussies (`TestRestorationReadiness.ps1`) |
| Contexte des phases et remappage des références gelées | 16 assertions réussies (`TestFrozenDefinitionContracts.ps1`, doubles API uniquement) |

Ces contrôles ne constituent pas une preuve de restauration native après fermeture.

## Essais réels et défauts conservés

| Run | Observation | Verdict |
|---|---|---|
| `antibes-durable-capture-r1` | Suppression physique immédiate : 2 025 objets ; 3 600 événements persistés. Invalidation d’un wrapper natif après fermeture d’archive. | Échec, corrigé ensuite ; aucune restauration durable validée. |
| `antibes-durable-capture-r2` | Régression de recherche entre wrappers équivalents : recettes absentes. Le statut inactif n’a pas été accepté comme succès. | Échec, corrigé ensuite ; preuve brute conservée. |
| `antibes-durable-capture-r3` | 3 600 recettes présentes ; un type électrique standard annonce son renommage mais le refuse. | Échec, préparation des types stock corrigée ensuite. |
| `antibes-durable-capture-r4` | 3 600 recettes présentes ; ambiguïté de copie d’un séparateur de surface. 1 119 acquittements d’objets internes/exclus, aucune capsule native publiée. | Échec ; les acquittements ne comptent pas comme restauration physique. |
| `antibes-durable-capture-r5` | 12 capsules JSON/GZ, 108 recettes dans 22 catégories, 275 acquittements sans recette. Séparations de surface, membres d’esquisse et groupes signalés séparément. | Publication réelle validée, aucune restauration après fermeture dans ce run. |

Les runs r1 à r5 ont été fermés sans sauvegarde, puis leurs seuls processus isolés ont été arrêtés avec vérification du PID et de l’intégrité. Le témoin et les copies initiales ont conservé le SHA256 ci-dessus. Le relevé stable final r5 est `final-publication-inventory.json` : 383 sidecars, dont 108 recettes et 275 acquittements ; 95 115 679 octets de capsules compressées. La taille et le temps de traitement restent à optimiser.

## Périmètre partiel autorisé

L’utilisateur autorise désormais à laisser provisoirement les objets bloquants de côté pour démontrer le fonctionnement. Le banc doit donc conserver la liste des exclusions et leurs causes, sélectionner seulement des conversions réellement relues, puis tester ce sous-ensemble sans accès à la passerelle. Une validation partielle ne doit jamais devenir un succès de restauration totale ni une preuve de nettoyage automatique des travaux échoués.

La dernière consigne privilégie un usage métier courant : attendre une minute après l’ouverture, modifier quelques éléments pendant la préparation automatique, continuer à travailler, puis supprimer une sélection limitée contenant ces éléments. Ce scénario remplace la suppression immédiate ou de la moitié du projet comme premier critère de validation. L’état de référence est mesuré après les modifications et avant la suppression ; les suppressions par cascade restent dans le contrôle. Aucun amorçage manuel ni conversion forcée ne doit masquer un défaut du produit. La restauration doit être indisponible tant que ses données ne sont pas validées.

## Limites encore ouvertes

La restitution complète, sa persistance après sauvegarde/réouverture et le délai perçu ne sont pas encore validés. Les recettes directes disposent maintenant d’un chemin d’export de leurs définitions versionnées, avec conservation de la recette d’instance au moment de la suppression ; sa compilation Revit 2025 passe, mais le test réel reste requis. Une dépendance modifiée après la capture, une liaison de paramètre projet non prouvée ou une source fermée avant cette préparation doivent encore produire un échec explicite. Le détail technique est consigné dans `DURABLE_PENDING_GAPS.md`.
