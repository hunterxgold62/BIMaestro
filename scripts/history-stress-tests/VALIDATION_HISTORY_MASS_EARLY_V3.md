# Antibes — suppression massive early v3

Statut : **RestoreCompletedAfterInterruptionAnnounced**. La restauration a retourné un résultat après l’annonce de fermeture forcée par l’utilisateur. La vérification initiale est disponible ; aucune sauvegarde du modèle restauré, fermeture propre ou réouverture dans un nouveau document n’est validée. **Aucun PASS global.**

Run : `tmp/history-stress/antibes-mass-early-v3`. Produit chargé : SHA256 `C9CED231881FBCDE747CCD3E3CAEA34907B4A76B3868CF176899ED45A0DA34A0`. Capture **Détaillée** pendant tout l’essai. Ce résultat ne compare pas Simple et Détaillé et ne mesure pas la fluidité de navigation.

## Journal complet, interface par défaut tronquée

La transaction a reçu 3 027 racines, pour 3 086 candidats sélectionnés. `Document.Delete` a retourné 4 201 IDs ; `DocumentChanged` en a finalement annoncé 4 293. Le journal brut du batch `6d4722a1ff4e4c4aabca5983bfa46730` contient exactement ces **4 293 IDs**, sans manquant, événement supplémentaire ou doublon d’ID. Tous indiquent un snapshot absent, avec une recette issue de la copie d’ouverture.

Le chargement complet et les requêtes réellement construites couvrent 4 293 événements. Le chargement récent par défaut en conserve **2 000**, omettant **1 394 des 3 064 candidats effectivement supprimés**. Le banc appelle le vrai chargeur par période et la vraie conversion UI `ToRestoreRequest`, puis le moteur de restauration ; il ne teste pas la sélection/rendu de la liste ou des clusters dans la fenêtre réelle. Une restauration des seuls 2 000 événements récents ne validerait donc pas ce batch.

## Défaut de périmètre conservé dans les dénominateurs

Le banc v3 assimilait certaines catégories de modèle à des objets physiques sélectionnables. Les sous-groupes ci-dessous décrivent le brut ; ils ne remplacent jamais le périmètre initial pour fabriquer un PASS.

| Périmètre déclaré | Sélectionnés | Supprimés | Présents avec une origine unique | Vérification stricte passée | Présents, contrôle échoué | Manquants |
|---|---:|---:|---:|---:|---:|---:|
| Objets internes : 147 ressources de matériaux, 17 réglages soleil, 619 axes, 215 légendes, 19 caméras | 1 017 | 1 017 | 890 | 638 | 252 | 127 |
| Isolants exclus par politique produit | 524 | 524 | 0 | 0 | 0 | 524 |
| Autres candidats modèle déclarés | 1 545 | **1 523** | **1 330** | **750** | **580** | **193** |
| Total v3 | 3 086 | **3 064** | **2 220** | **1 388** | **832** | **844** |

Les 22 sélectionnés survivants sont deux familles « Travailleur chantier » et vingt indicateurs de sens de fluide. Les 193 manquants du troisième groupe sont 176 lignes de modèle, 7 `ConduitRun`, 6 barreaux, 3 pièces et 1 zone CVC. Ce troisième groupe reste celui du banc v3 ; son nom ne garantit pas que chacun de ses membres constitue une racine physique admissible.

## Fidélité : séparer isolants, géométrie et graphe

Parmi les 1 330 autres candidats retrouvés, aucun écart de classe, de type, de dimension, de niveau/hôte/groupe ou de type de système n’est signalé. **Aucun `TypeMatches=false`**, y compris sous le même nom de type. Les 339 axes comparables parmi les objets présents mais en échec ont des extrémités et un milieu identiques, avec un delta mesuré de **0**.

Les 580 échecs présents de ce groupe comprennent les contrôles suivants, qui peuvent se recouper :

| Contrôle échoué | Nombre | Diagnostic |
|---|---:|---|
| Connexions | 535 | 520 ont uniquement perdu des références vers des isolants volontairement non restaurés ; 15 restent différents après retrait diagnostique de ces seuls voisins |
| Position/transformée | 8 | Tous les déplacements mesurés dépassent 1 cm |
| Bbox mondiale | 28 | Tous les écarts mesurés dépassent 1 cm |
| Jonctions géométriques | 28 | 14 murs, 6 sols, 4 toits, 2 poteaux, 1 ossature, 1 topographie |
| Aucune géométrie vérifiable | 2 | Courbes principales ; le test ne peut pas conclure à leur fidélité |

Les **520 cas liés uniquement aux isolants** n’ont aucun autre contrôle échoué. Il reste donc, à titre diagnostique, **60 objets présents avec d’autres problèmes** parmi les 580, en plus des 193 manquants. Ce calcul ne modifie ni les booléens bruts ni le verdict global. Le banc compare les références des connecteurs ; une référence vers un isolant n’établit pas à elle seule qu’il s’agissait d’une connexion physique plutôt que logique.

Les 15 graphes encore différents hors isolants concernent 2 canalisations, 7 raccords, 5 appareils sanitaires et 1 équipement CVC. Les origines des voisins sont résolues à partir des objets vérifiés ; les origines extérieures à cet ensemble restent explicitement non résolues.

Trois défauts matériels précis, indépendants de la tolérance stricte `1e-5 ft` :

- Canalisation **819865**, UID `d9f10a2d-8816-43db-acad-c0e7edc4b016-000c8299`, « CML_Acier Té » : axe, bbox et type identiques, mais **4 ports deviennent 2**. Les deux ports `Curve` ont disparu, avec les piquages acier **923374** et **923378** comme voisins attendus. La canalisation **945923** perd également ses trois ports `Curve` : **5 → 2 ports** ; ses piquages attendus sont 978412, 982660 et 982848.
- Mur **556631**, UID `b53e8810-08f6-436c-89aa-508f4d2974d7-00087e57`, « CML_Mur béton 350mm » : axe et type identiques, mais écart maximal d’un coin de bbox de **175 mm**. Le changement de bbox prouve un écart mesuré ; le banc ne compare pas l’intégralité des solides.
- Plante **520877**, UID `ed00bb15-8d5c-445b-9e0c-fb4a015aaced-0007f2ad`, « ARBOL TIPO 01 » : point, transformée et bbox déplacés de **457,966 mm**. Le barreau 557048 présente un écart encore supérieur, **23,710 m** ; cette observation nécessite l’analyse de son propriétaire et de sa création native avant attribution de cause.

L’[analyse ciblée des causes](RESTORE_FINDINGS.md) distingue les ports de piquage perdus confirmés, la forte inférence d’un changement d’ordre de jonction des murs et l’étape encore inconnue responsable des translations de plantes.

## Identités et objets supplémentaires

Aucun doublon d’origine par source et aucun mapping inverse non injectif n’ont été détectés dans les 3 064 lignes : aucun UID actuel n’est utilisé pour satisfaire plusieurs sources distinctes. Cela ne prouve pas l’absence d’objets supplémentaires.

La liste brute contient **500 objets de modèle supplémentaires sans origine d’une cible** : 329 membres d’esquisse, 84 éléments de légende, 35 ressources de matériaux, 25 systèmes MEP, 7 matériaux, 7 segments de canalisation, 7 `ConduitRun` et 6 barreaux. La source gelée v3 utilise `ToArray()` sans `Take(500)` : **500 est le nombre observé complet**, et non une limite du banc. Ces catégories demandent une analyse de leurs propriétaires ; ce ne sont pas automatiquement 500 copies physiques injustifiées. Le contrôle « aucun objet supplémentaire » reste non validé.

Les noms de familles/types suffixés 2/3/4 dans les captures intermédiaires ne suffisent pas à conclure à une substitution des types des objets vérifiés : leurs identités de type finales correspondent. Les définitions supplémentaires ne sont pas comparées exhaustivement.

## Durées et attribution

Heures UTC : ouverture du document 14:14:09,992 ; retour d’ouverture 14:14:38,461 ; suppression 14:14:42–14:15:16,951 ; journal complet observé 14:15:19,943 ; restauration 14:15:59,610–14:37:54,375 ; vérification initiale terminée 14:37:55,089.

- Inventaire tiers : **1,095 s**. Suppression : **34,895 s**. Journal complet observé environ **3 s après le retour de suppression** ; cela inclut la cadence de sondage et ne constitue pas une mesure isolée de latence disque.
- Restauration : **1 314,761 s — 21 min 54,761 s**, 1 402 créations, 377 inclus dans un parent, 0 existant, 2 514 échecs de requête. Les échecs bruts comprennent 524 isolants, 746 enregistrements internes, 204 autres candidats et 1 040 dépendants/non classés. Un échec de requête n’implique pas automatiquement un objet final manquant : certains dépendants sont recréés par leur parent.
- Pendant cet appel : **2 523 callbacks DocumentChanged**, dont 179 sans aucun ID ; 1 581 transactions de restauration et 942 transactions de connexion ; **13 796 IDs ajoutés**. Ce dernier compteur n’est pas un compteur de restaurations réussies.
- Le mode Détaillé recapture ces transactions. Les mesures `element_capture_full` enregistrent **13 929 appels**, **52,338 s cumulées**, maximum **147,100 ms** ; 45,380 s concernent les FamilyInstance. Ces mesures couvrent la construction des snapshots, pas l’intégralité des callbacks, des copies natives, des transactions, du disque ou du rendu.
- 497 rafraîchissements demandés par le banc représentent 11,359 s mesurées. Ces durées peuvent contenir d’autres opérations synchrones ; elles ne s’additionnent pas aux phases imbriquées pour décomposer la durée totale.
- La priorité du processus a été abaissée de Normal à BelowNormal à **14:36:03,093Z**, au bénéfice du Revit de travail. Les durées postérieures sont affectées par ce changement. Les écarts entre Idling ou messages Dispatcher ne sont pas des temps d’image et ne prouvent pas une fluidité interactive.

Le mode Simple éviterait la capture synchrone des détails de maillage et une partie de l’enrichissement, mais ce run unique ne quantifie pas son gain. Il ne permet pas non plus d’extrapoler les 21 min 55 s à un futur banc corrigé portant uniquement sur des objets admissibles.

## Intégrité et preuves

Les SHA256 source et copie sur disque restent `EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA` après la fermeture forcée, selon `disk-integrity-after-interruption.json`. Cette égalité prouve que les RVT d’origine sur disque n’ont pas changé ; elle ne prouve pas la persistance de la restauration.

Preuves conservées dans le run : `deletion.json`, `journal-summary.json`, `load.json`, journal JSONL brut, `restore-result.json`, `verification.json`, `observer.jsonl`, `process-priority-change.json`, source et provenance gelées. Analyse indépendante : **`independent-summary.json`**, **`independent-fidelity.json`**, **`independent-timings.json`**, `independent-native-copy-tail.jsonl`. Les fichiers `reopened-verification.json`, `persistence.json` et `integrity.json` du cycle complet sont absents. Aucun succès de sauvegarde/réouverture n’est revendiqué.

`Summarize.ps1` a également été vérifié sur des fixtures de 2 001 événements : couverture complète, plafond récent 2 000, requêtes réelles incomplètes, événement brut manquant, origines multiples, mapping inverse non injectif, graphe ou géométrie non vérifiés masqués par `Passed=true`, erreurs de sonde, objets supplémentaires et statut interrompu explicite. Aucun de ces contrôles n’a lancé Revit ni modifié le produit.
