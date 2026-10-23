# Prototype de groupe natif — preuves r4/r5 du 23 octobre 2026

**La création native et le transfert de six paramètres sont démontrés dans r5. La saisie dans ce groupe et la fiabilité de son cycle de vie ne sont pas encore qualifiées.** Le groupe a disparu avant la capture prévue pour le premier essai de texte.

La consolidation est indépendante de l'indicateur `pass` du harnais : elle compare les cellules, les identités, les valeurs API et le schéma des rapports bruts, sans piloter Revit. Seuls `run.json` et `summary.json` ont été ajoutés dans `tmp/properties-custom-group-tests/2025-r5` ; les captures originales n'ont pas été modifiées.

| Observation | Résultat indépendant |
|---|---|
| Hôte r5 | Revit 2025, build 25.4.41.14, PID 80088, fixture dédiée. |
| Ligne créée | 44 lignes à l'origine, 46 après l'opération : un en-tête et son spacer. |
| Vrai en-tête natif | `isCellPartOfAGroupHeader` retourne 1 pour la ligne 2 ; ligne non éditable et marqueur de groupe natif observés. |
| Membres | Les six identités prévues sont sous ce seul en-tête ; leurs groupes de définition API restent inchangés. |
| Origines | Deux groupes natifs d'origine : Contraintes et Données d'identification. |
| Identités natives | Les 29 identités visibles de la baseline sont conservées ; chaque ligne correspond au libellé attendu. |
| Cellules originales | Le multiensemble des cellules reste identique, sauf les deux lignes supplémentaires prévues. |
| Valeurs métier | Zéro différence sur 18 550 valeurs de paramètres de toutes les instances et de tous les types de la fixture. |
| Schéma | Zéro différence sur les 18 550 descripteurs : stockage, lecture seule, groupe de définition, partage et GUID. |
| Étapes natives | Les dix rapports `group-step-*` concordent indépendamment sur positions natives et libellés, y compris après chacun des six déplacements. |
| Capture avant saisie | `text-before.json`, 12:34:47 UTC : 44 lignes, groupe absent. Cette capture ne peut pas qualifier une écriture dans le groupe personnalisé. |

Les membres sont Commentaires, Limite de pièce, Décalage inférieur, Contrainte inférieure, `BIMAESTRO_Code_metier` et `BIMAESTRO_Statut_validation`. Ces deux derniers sont de vrais paramètres partagés de la fixture. Leur présence dans le groupe d'affichage n'est pas une création ou une modification de leur définition API.

L'en-tête est construit avec les primitives inspectées `InsertRows`, `addGroupHeader@ElemGrid` et `AString` ; son caractère natif est testé séparément. Les SHA256 des modules installés et de l'assembly de laboratoire sont consignés dans `run.json`. Ces empreintes actuelles et les gardes du code ne sont pas présentées comme un dump autonome des modules chargés lors de la capture.

Dans r4 (PID 12992), l'insertion et le test d'en-tête natif étaient déjà observés sans différence métier ni schéma. La vérification d'identité a ensuite échoué avant les transferts (`mutationAttempted=false` pour la commande `group-members`). Cette série ne prouve donc aucun déplacement de membres ni aucune saisie dans le groupe.

Les captures structurelles complètes `inserted`, `collapsed`, `expanded` et `restored` manquent dans r5. Les étapes natives internes ne sont pas transformées artificiellement en rapports complets contenant un schéma API qui n'y a pas été capturé. Aucun éditeur, repli/dépli ou rollback complet n'est déclaré réussi par cette consolidation.

Pour la série suivante, une écriture après désélection peut être vérifiée sur le modèle complet, mais elle ne sera attribuée au groupe personnalisé que si la capture **avant** l'entrée prouve son en-tête natif et tous les membres correctement mappés. La disparition du groupe avant l'entrée ne peut pas être compensée par une valeur API correcte après coup. La série r6 doit notamment vérifier la reconstruction contrôlée, le refus de mutation pendant saisie et les limites de réapplication.
