# Prototype r7 — qualification gelée pendant le travail de performance

Revit 2025, build 25.4.41.14, PID 80352. Trois paires sont admises par la vérification indépendante ; aucune qualification complète du prototype n'en est déduite.

| Saisie dans le groupe natif | Différence métier unique attendue | Schéma |
|---|---|---|
| Limite de pièce | `1 → 0` | Identique. |
| Contrainte inférieure | Identifiant de niveau `1951 → 2789` | Identique. |
| Paramètre partagé `BIMAESTRO_Code_metier` | `METIER_BASELINE → METIER_BASELINEx` | Identique, stockage String, GUID partagé conservé. |

Chaque capture avant montre 46 lignes, un véritable en-tête natif non éditable et les six membres avec leurs identités correctement associées. Chaque paire compare 18 550 valeurs et descripteurs de paramètres. Les captures après peuvent suivre une désélection ; la preuve du groupe et de l'éditeur repose obligatoirement sur la capture avant.

Le premier essai de `BIMAESTRO_Statut_validation` n'est pas admis : une vérification initiale n'avait aucun delta ; une vérification ultérieure indiquait une valeur changée sur le premier mur alors que le mur sélectionné, utilisé comme cible, exposait encore 0. Le rapport de vérification était `pass=false` et aucune paire finale admissible n'a été produite. Un changement isolé constaté ensuite ne transforme pas cet essai en succès.

`original-baseline.json` est conservé tel quel : il contient 47 lignes de la palette d'une vue, malgré un contexte API de mur. Il ne constitue donc pas une baseline structurelle synchronisée de 44 lignes. Les rapports natifs internes montrent séparément une baseline de mur à 44 lignes puis une insertion à 46 lignes, mais ils ne contiennent pas le schéma et les valeurs API du rapport complet. Aucune baseline complète n'est reconstruite artificiellement à partir de ces morceaux.

Les fichiers `run.json`, `interactions.json` et `summary.json` de r7 consignent les trois paires admises et leurs sources. Le collecteur rejette explicitement la baseline structurelle désynchronisée ; il ne retire pas pour autant la preuve indépendante de saisie des paires dont l'état avant est correct.

Les essais fonctionnels sont gelés ici pendant le travail de performance r8. La création et les deux autres éditeurs observés dans r5/r6 restent des preuves de séries distinctes. Aucun repli/dépli, cycle de vie complet, rollback ou refus pendant saisie n'est déclaré qualifié à ce stade. Voir [PERF-PLAN.md](PERF-PLAN.md) pour les mesures suivantes, sans capture des 18 550 paramètres dans l'intervalle d'interaction.
