# Prototype Revit 2025 — Antibes — 23 octobre 2026

Le conflit entre l'ordre normal et le groupe expérimental a été corrigé dans le laboratoire : une permutation de groupe vérifiée n'est plus déplacée indépendamment par le service d'ordre. Les anciens rappels de dessin sont détachés au changement de contexte. Le contexte d'apparence accepte maintenant les paramètres communs d'une sélection de même catégorie ; les catégories mélangées restent natives. Les identités, champs en lecture seule et refus pendant saisie sont conservés.

## Données et intégrité

- Source fournie : `tmp/history-stress/antibes-rapid-j14-restore-v11/Antibes-restaure.rvt`, 142 274 560 octets (135,7 Mio).
- SHA256 source avant/après : `A07A7579FE893F38C9DC515AEFB957426C8D7709BFDC17016C06626BE2E37822`.
- Essais sur copies uniquement, Revit 2025.4 / 25.4.41.14.
- Inventaire initial : 17 555 éléments, 26 murs, 19 plans. Deux vues ont été créées dans la copie avec un ViewFamilyType StructuralPlan avant la baseline. Revit les affiche « Plan de structure ». L'inventaire API retourne toutefois CeilingPlan pour leur ViewType ; ne pas inférer une validation du classement des types de vues depuis ce seul champ.
- Baseline r21 : 17 571 éléments, 318 497 valeurs et descripteurs de paramètres. Empreintes de valeurs et de schéma identiques après essais. Aucune écriture de paramètre réalisée par le scénario de transitions.
- Quatre paramètres natifs dans le groupe de murs sur cette maquette : Commentaires, Limite de pièce, Décalage inférieur, Contrainte inférieure. Les paramètres métier supplémentaires de la petite fixture n'ont pas été ajoutés au projet Antibes.

## Résultats enregistrés

| Série | Résultat | Limite |
|---|---|---|
| r19, disposition personnalisée | 30/30 transitions, dont deux murs sélectionnés ; pas d'arrêt automatique | Critère de disposition native et d'identité, sans preuve d'une nouvelle frame colorée à chaque transition |
| r21, palette native | 30/30 transitions | Demandes API, pas clics physiques |
| r21, groupe + ordre + couleurs | 20 transitions satisfont disposition, identités, plan de couleur courant et nouveau dessin coloré ; contrôle expiré à l'étape 20/30 | Pas de qualification complète. Revit était en arrière-plan ; l'absence de nouveau dessin est une explication plausible, non une cause démontrée par les traces disponibles |
| r21, état après expiration | Groupe natif reconnu, membres et identités valides, autoGroup actif, aucun arrêt de sécurité | Un état final ne prouve pas l'absence de flash intermédiaire |
| Saisie native sur cette version | Non réalisée | L'utilisateur a demandé de terminer les mesures automatiques sans reprendre clavier/souris |

Les mesures incluent l'ordonnancement de la boucle de 250 ms. La condition native vérifie la disposition, la condition personnalisée r21 attend aussi un nouveau dessin coloré : ces conditions et nombres d'échantillons diffèrent. Aucun pourcentage de ralentissement global n'en est déduit.

| Mesure | Native r21, 30 échantillons | Personnalisée r21, 20 échantillons admis |
|---|---:|---:|
| Médiane demande API → critère vérifié | 423,5 ms | 903,8 ms |
| P95 | 2 248,7 ms | 1 588,9 ms |
| Maximum | 5 229,2 ms | 1 837,8 ms |

Coûts internes r21 pendant les transitions : 21 reconstructions de groupe, moyenne 57,9 ms, P95 85,4 ms, maximum 93,3 ms ; 96 dessins colorés, moyenne 2,9 ms, maximum 11,2 ms. Ce ne sont pas les durées complètes d'un clic.

Au repos après remise à zéro des compteurs : 378 contrôles de groupe, moyenne 3,7 ms, P95 6,6 ms, maximum 19,3 ms ; 198 actualisations, moyenne 4,3 ms. Aucune reconstruction de groupe dans cette période stable. Période de compteurs distincte de la fenêtre CPU de 30 secondes.

CPU du processus entier sur deux fenêtres successives de 30 s : 30,72 s CPU avec prototype actif, 35,17 s CPU avec son traitement désactivé. Cela représente environ 1,02 puis 1,17 cœur occupé, pas 102 % ou 117 % de tout le processeur. Activité d'autres composants et applications non isolée ; aucune attribution certaine au prototype, ni conclusion d'amélioration. La fenêtre désactivée conserve le document et peut conserver l'en-tête natif : ce n'est pas une nouvelle session de Revit vierge.

## Preuves et qualification

Fichiers ignorés sous `tmp/properties-custom-group-tests/2025-r19` et `2025-r21` : inventaires, résultats et traces des séries, audits avant/après, état final, compteurs CPU et mémoire. `combined-evaluation.json` r21 reste explicitement **pass=false**, car la série personnalisée complète et équilibrée n'a pas été validée. Les 15 tests du collecteur passent ; ils vérifient le collecteur et ne qualifient pas la saisie Revit.

Prototype maintenu isolé. Aucun engagement de « zéro yoyo » pour toute catégorie, toute configuration ou toute version. La nouvelle saisie, les changements de configuration en cours de session et la présentation au premier plan restent à qualifier.

Compilation complète BIMaestro Revit 2025 réussie : 0 erreur, 314 avertissements. Package manuel dans `tmp/properties-custom-group-tests/manual-2025`. Le lanceur ne remplace pas la DLL installée : il utilise temporairement son manifeste 2025 pour une nouvelle session, restaure ce manifeste après chargement et ouvre une nouvelle copie d'Antibes. Le groupe expérimental reste limité au document/PID du laboratoire.
