# Historique : récupération à J+14 avec les types présents

23 octobre 2026. Chemins de preuves relatifs à la racine du dépôt.

**Statut : cycle natif v11 réussi sur copie Antibes, avec la DLL produit v10 inchangée.** Les trois objets réellement modifiés puis supprimés ont retrouvé leur dernier état à **J+14 simulé**, puis après sauvegarde, fermeture et réouverture. Le refus après suppression réelle de la famille et de ses types est également vérifié sur une autre copie. Le résumé strict valide ses **148 contrôles**, sans erreur cachée. Ce résultat ne représente ni quatorze jours écoulés ni une garantie universelle de récupération.

## Contrat retenu avec l’utilisateur

Conserver le dernier état de l’instance avant suppression, puis réutiliser la famille et le type encore présents dans la maquette. Une famille ou un type purgé rend la restauration indisponible : ce cas est accepté. Aucune recherche par nom ni réimportation automatique ne doit masquer cette absence. Les définitions éventuellement modifiées depuis la suppression sont celles du modèle courant ; leur version historique n’est plus garantie.

Les liens/imports sont hors périmètre des objets : `RevitLinkInstance`, `RevitLinkType`, `ImportInstance`, `CADLinkType`. Un objet Revit natif n’est pas exclu parce qu’il possède un champ IfcGUID. Une géométrie convertie en objets natifs sans provenance identifiable n’est pas assimilée à un lien sur son seul nom.

Une copie complète d’ouverture commune reste un support temporaire pour la période où l’inventaire n’est pas terminé. Elle ne devient pas une bibliothèque permanente de familles ou une capsule par objet.

## Fonctionnement implémenté

- Les modifications capturent immédiatement la recette directe disponible, avec placement, paramètres d’instance et relations. La transaction suivante peut supprimer l’objet avant tout passage de préparation différée.
- Les recettes directes portent `UsesExistingModelDefinitions=true`. Leur publication conserve du JSON vérifié et retire les reçus `Native`/`OpeningFallback`, y compris dans les circuits enfants. Le chemin nominal ne fait aucun export de définitions RVT/RFA.
- Un objet non encore inventorié peut être lu dans la copie d’ouverture et converti en recette directe. Un état modifié non prouvé ou une catégorie qui exige une copie native reçoit un diagnostic d’indisponibilité explicite. Le journal de suppression reste conservé.
- La restauration vérifie les UID exacts du type, de la famille et des références requises. Une définition absente retourne `Reason=definition_missing` et un détail commençant par `ExistingModelDefinitionMissing:` avant toute création. Un homonyme ne remplace pas le type d’origine.
- La file locale asynchrone publie des lots avec SHA256, fsync et relecture avant retrait de la RAM. Le transport réseau est séparé. La reprise déduplique après crash entre append et acquittement et conserve les fragments tronqués avant réparation.
- Append, lecture stricte et compression partagent une lease de fichier. La compression termine le GZip, force l’écriture et vérifie la séquence complète avant suppression du journal source. Les erreurs IO temporaires sont retentées ; une erreur d’intégrité reste visible.
- Les copies d’ouverture ne sont retirées qu’après fermeture et preuve que les événements ont une conversion durable relue, y compris un refus explicite pour les objets non pris en charge.
- La lecture des anciennes archives reste compatible. Elle ne constitue pas le contrat des nouvelles recettes et ne produit plus de nouvelles bibliothèques dans le chemin nominal.

## Validation ciblée acquise

| Contrôle | Résultat |
|---|---|
| Restauration avec définitions présentes, purge, homonyme, références, circuits, DTO réel | 41 assertions et 3 contrats réussis |
| Publication compacte, ACK explicites, fermeture, lecture après arrêt du processus source | 31 assertions et 2 contrôles du second processus réussis |
| Disponibilité hors interface | 29 assertions réussies |
| Lecture stricte des événements et conversions | 35 assertions réussies |
| Capture immédiate | 20 assertions réussies |
| Exclusion des liens/imports et maintien des objets natifs | 60 assertions dédiées ; 44 assertions de preuve d’exclusion vivante |
| Contrainte supérieure du mur | 23 assertions réussies |
| Spool local / course publication | 53 / 7 assertions réussies |
| Journal, vraie pompe locale et worker | 43 344 contrôles sur 7 625 événements |
| Compression de production à J+14 simulé | 13 assertions sur 2 507 événements |
| Sélection indépendante des requêtes du banc | 33 assertions réussies |
| Résumé strict, contrôles négatifs et rejet des preuves v10 échouées | 362 contrôles réussis lors de la revue indépendante |

Le témoin synthétique du stockage compact représente **758 octets de recette JSON**, **502 octets pour sa conversion compressée**, et **zéro capsule native**. Les mesures des trois objets Revit réels sont données ci-dessous, séparément de ce test synthétique.

Preuves :

- `tmp/history-existing-definition-tests/` : `TestExistingModelDefinitionRestoration.ps1`.
- `tmp/history-compact-direct-tests/c2a2c75742574020a0ae045fa90cf5e7/io/results.json` et lecture à froid.
- `tmp/history-readiness-tests/fe62b516ec97494faae5dfa652f3e5cb/`.
- `tmp/history-durable-bridge-read-tests/f35d61411af14aa5ae95d14cfb13225f/`.
- `tmp/history-external-model-exclusion-tests/799e84b3669e4a8cac2f151ad26517aa/results.json`.
- `tmp/history-live-exclusion-tests/a1493811a80f4f16b0323d07492a0a3c/results.json`.
- `tmp/history-rapid-summary-tests/81da55570da24e6bad692371db2d374a/results.json` ; revue indépendante avec rejet des preuves réelles v10.

## Compilations v10

- Revit 2025 Release : réussite. DLL cible `tmp/history-stress/product-rapid-cycle-v10/BIMaestro.dll`, SHA256 `581F751C1D950EA12D73518646B08FFDF81A2E743D7CF778C6F856FCC822B0BE`.
- Revit 2024 / .NET 4.8 : réussite avec MSBuild amd64, 8 avertissements de champs inutilisés, aucune erreur. SHA256 `7874545464E6FD6168CFD78BF21736B5DEB286C218E50445F534CB6C1EEF8CB2` ; preuves `tmp/history-stress/compat-revit2024-v10-191f88097d744cc497ffbe59af88b3fc/{compatibility-result.json,build.log}`.
- Sources compilées : `tmp/history-rapid-v10-sources-before.json`. Seul l’ajout de l’identifiant d’instance dans le filtre de capture réseau distingue cette version de v9.
- Vérification finale : les 24 sources du manifeste n’ont pas changé depuis la compilation (`tmp/history-rapid-v10-source-integrity-final.json`).
- Aucun déploiement permanent. Le lanceur rétablit l’inscription normale du complément après le démarrage du processus isolé.

## Critère du cycle natif complet

Copie Antibes uniquement, trois témoins : famille `474717`, mur `556835`, canalisation `913245`. Modifier réellement leur géométrie et commentaire, puis supprimer au callback suivant avec préparation différée suspendue. Exiger tous les événements et trois recettes prêtes, fermer le document et le processus, vérifier le retrait de la copie d’ouverture.

Dans un nouveau processus avec la même DLL, interdire la lecture des sources préparées, avancer seulement l’horloge de maintenance à la dernière suppression +14 jours, appeler la vraie compression et vérifier chaque empreinte d’événement. Restaurer depuis le loader et les requêtes réelles, comparer paramètres, type/famille d’origine, géométrie, hôtes, niveaux, joins et connexions. Sauvegarder, fermer et rouvrir le résultat, puis refaire les contrôles.

Après les contrôles positifs, ouvrir une autre copie sous le périmètre historique, y supprimer réellement la famille et ses types, et demander la même restauration. Exiger le refus explicite `definition_missing`, aucun import, aucun objet créé et aucune modification du fichier positif. Mesurer les recettes réelles et l’absence de tout payload RVT/RFA dans l’historique.

**Résultat complet v11 : PASS.** Exécution indépendante, sans reclasser l’essai v10. Le banc conserve les dix événements et leurs dix conversions réelles par l’interface, puis sélectionne les trois objets physiques selon le catalogue initial indépendant. Les sept enregistrements internes ou annotations ont chacun une preuve d’exclusion ou un refus explicite. Un axe régénéré n’est accepté que par sa classe API exacte, sa catégorie native et un nombre borné par les identifiants initiaux distincts ; aucun filtre par nom n’assouplit la fidélité physique.

| Étape réelle | Résultat v11 |
|---|---|
| Modification géométrique et commentaire des trois témoins | 3/3 ; préparation différée suspendue, inventaire initial incomplet |
| Suppression au callback suivant | 1 401,29 ms après les modifications ; aucun export natif en attente |
| Événements et conversions durables | 10/10 ; trois recettes physiques Ready |
| Fermeture et retrait automatique du support d’ouverture | Vérifiés avant arrêt du processus de capture |
| Nouveau processus, même SHA256 produit, sources interdites en lecture | Vérifiés ; ancien support d’ouverture absent |
| Vraie compression et loader à J+14 simulé | Empreintes, identifiants et dates des événements conservés |
| Restauration et comparaison indépendante | 3/3 ; paramètres capturés, placement, géométrie, références, types/famille d’origine et connexions vérifiés |
| SaveAs, fermeture, réouverture d’un Document distinct | 3/3 de nouveau vérifiés |
| Famille et types réellement supprimés dans une copie séparée | `definition_missing` avec UID d’origine ; zéro création ou import ; copie positive inchangée |
| Sources préparées et checkpoint supprimé | Empreintes inchangées |

Les transactions de modification, y compris les callbacks de production, ont duré respectivement **68,08 ms**, **81,16 ms** et **21,39 ms**. Le callback du banc pour la suppression complète a duré **330,40 ms**. Ces mesures incluent du travail Revit et du banc ; elles ne démontrent pas une absence de latence sur une grande suppression. Aucun export RVT/RFA ni attente réseau synchrone n’est ajouté au chemin nominal de capture.

Les recettes exactes des témoins occupent **1 983 + 1 954 + 1 911 = 5 848 octets JSON**, soit environ **5,8 ko**, avec **zéro payload RVT/RFA** dans l’historique. À la fermeture de capture, le journal pèse 56 365 octets et le répertoire historique complet 210 533 octets. Après le cycle J+14 et la restauration, ils pèsent respectivement 43 150 et 262 209 octets ; le journal mensuel compressé pèse 6 339 octets. Le total complet inclut les diagnostics, les conversions et le suivi collaboratif. Il exclut les copies de maquette du banc et la copie d’ouverture temporaire, déjà retirée automatiquement.

Preuves natives :

- `tmp/history-stress/antibes-rapid-j14-capture-v11/` : `rapid-edits.json`, `rapid-deletion-gap.json`, `rapid-readiness.json`, `durable-session-closed.json`, `rapid-storage-capture.json`.
- `tmp/history-stress/antibes-rapid-j14-restore-v11/rapid-cycle-summary.json` : résultat global strict, 148 contrôles réussis.
- Dans ce même dossier : `rapid-j14-simulation.json`, `rapid-source-denial.json`, `rapid-request-selection.json`, `rapid-existing-definition-inputs.json`, `verification.json`, `reopened-verification.json`, `persistence.json`, `rapid-purge-refusal.json`, `rapid-storage-final.json` et `finished.json`.
- Les processus isolés 85404 et 66528 ont été arrêtés après preuve de fermeture des documents, par le script de garde ; voir `isolated-process-stopped.json` dans chaque dossier. La DLL normale reste à son SHA256 initial `2BDD94A23A6CA520F423B95FF8D28D1394EB11C1C8F01918F3AD3925E7861261` et le manifeste normal pointe de nouveau vers elle.

## Essais précédents conservés

Les essais v3 à v8 visaient le contrat plus fort de définitions historiques autonomes, abandonné après la précision de l’utilisateur sur la purge. Ils restent des échecs documentés et ne sont pas reclassés : v3 a révélé la réutilisation de familles courantes et la perte de contrainte supérieure du mur ; v5/v6 un GUID virtuel non stocké ; v7 une précondition de collecteur Revit ; v8 un conflit implicite des dépendances du PipeType. Le mur passe toutes les vérifications v8 ; la famille v8 présente aussi deux écarts de métriques calculées readonly. Aucune tolérance du comparateur n’a été assouplie. Voir les dossiers `antibes-rapid-j14-capture-v3/v5/v6/v7/v8` et `antibes-rapid-j14-restore-v3/v8` sous `tmp/history-stress/`.

v9 a révélé un identifiant d’instance manquant sur la canalisation, corrigé en v10. Les trois objets passent toutes les vérifications physiques v10 ; le banc exigeait cependant zéro échec y compris pour sept enregistrements hors périmètre, et comptait l’axe natif régénéré de la canalisation comme objet inattendu. Cet essai reste incomplet. Les corrections de classement du banc ont été revues indépendamment et la campagne v11 a recommencé depuis une copie source intacte, sans changement de DLL produit.

## Limites

- Une famille, un type ou une référence requis purgé est un refus prévu. Les catégories non reconstructibles directement ne sont pas annoncées récupérables.
- Un arrêt avant le premier flush local asynchrone peut perdre les derniers événements encore uniquement en RAM. La reprise vérifiée concerne les lots déjà publiés durablement.
- La copie d’ouverture nécessite un RVT enregistré, accessible et non modifié au lancement de la copie. Elle ne remplace pas les modifications ultérieures capturées en mémoire.
- Les callbacks API Revit restent synchrones. Le retrait des exports de bibliothèques réduit le travail ; le banc de trois témoins ne prouve pas l’absence de gêne pour toutes les catégories ou toutes les tailles de suppression.
- J+14 est une simulation de la maintenance/compression/relecture, sans antidater les événements ni changer l’horloge Windows. Ce n’est pas quatorze jours de stockage réel ni une campagne SMB multi-postes.
- Le comparateur ne prouve pas l’égalité exhaustive des solides ou de toutes les règles de routage. Les noms de modèles restent des alias ; une identité métier persistante entre copies et projets homonymes reste à fiabiliser.
- Les mesures d’archive excluent les fichiers modèles créés par le banc et la copie complète d’ouverture temporaire. Aucune purge destructive d’anciennes données n’a été ajoutée.
