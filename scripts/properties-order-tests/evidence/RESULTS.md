# Qualification native de l'ordre des propriétés — 21 octobre 2026

Les preuves qualifient le réordonnancement des **murs sélectionnés individuellement (`cat:-2000011`), dans Revit 2023 à 2027**, pour les scénarios décrits ci-dessous. Le rapport JSON relève séparément les versions actuellement activées par le code de production et celles dont les preuves sont complètes. Les autres catégories ne sont pas qualifiées par ces essais.

Les rapports bruts des documents jetables restent sous `tmp/properties-order-tests`. Le fichier [results.json](results.json) conserve une synthèse recalculée, les différences de paramètres et les SHA256 des sources, sans recopier les snapshots volumineux.

| Version | Série réelle | Paramètres comparés par scénario | Texte | Booléen | Double | Liste de niveaux | Annuler / Rétablir / Échap | Restauration |
|---|---|---:|---|---|---|---|---|---|
| 2023 | 2023-r8 | 18 164 | PASS | PASS | PASS | PASS | PASS | PASS |
| 2024 | 2024-r8 | 18 251 | PASS | PASS | PASS | PASS | PASS | PASS |
| 2025 | 2025-r7 | 18 526 | PASS | PASS | PASS | PASS | PASS | PASS |
| 2026 | 2026-r8 | 18 659 | PASS | PASS | PASS | PASS | PASS | PASS |
| 2027 | 2027-r8 | 18 620 | PASS | PASS | PASS | PASS | PASS | PASS |

Chaque test d'écriture compare tous les paramètres de toutes les instances et de tous les types du document jetable. Dans les vingt scénarios, **une seule valeur change**, celle du paramètre attendu. Les valeurs observées sont `ORDER_BASELINEx`, `0`, un décalage inférieur de 9 mm (vérifié en unités internes) et l'identifiant du deuxième niveau propre à chaque fixture : 2704, 2746, 2785, 2798 et 2783 respectivement.

Avant chaque saisie, le collecteur exige `hasApplied=true`, le paramètre cible en première position du premier groupe, et une correspondance native valide. Il vérifie aussi l'identité du processus, la version, le build, le document et la chronologie. La désélection volontaire après validation peut restaurer l'ordre natif avant la lecture API ; elle ne remplace pas la preuve préalable.

Annuler retrouve exactement les valeurs du snapshot de référence ; Rétablir retrouve celles de la saisie validée. Échap et la restauration finale ne provoquent aucune différence de paramètre. La restauration finale présente `hasApplied=false`. Le retour au premier mur sélectionné réapplique l'ordre sans différence métier ; la sélection multiple conserve les valeurs et n'applique pas d'ordre personnalisé.

La capture stabilisée du second mur démontre l'ordre actif pour la série 2024. Les captures des séries 2025 à 2027 présentent `hasApplied=false` et la série 2023 ne conserve pas de capture dédiée du second mur : **l'application automatique de l'ordre au second mur n'est pas revendiquée pour ces séries**. Les observations de repli/dépli ne disposent pas d'une paire dédiée complète ; aucune qualification indépendante de ce scénario n'est déduite d'un simple retour ultérieur aux mêmes valeurs.

Ces essais portent sur une petite fixture de murs. Ils ne qualifient pas toutes les familles, catégories, paramètres partagés homonymes, dialogues de type, sélections multiples éditables, grandes maquettes, mises à jour futures des bibliothèques natives ou toutes les combinaisons de navigation clavier. La présence d'une DLL compilée et le démarrage du harnais restent distincts d'une qualification native. Les bibliothèques internes vérifiées par le produit peuvent imposer une nouvelle validation après mise à jour de Revit.

Le collecteur possède six tests autonomes, tous réussis : ils rejettent notamment une écriture sans ordre actif, un changement collatéral, une identité native incorrecte, une valeur inattendue et des preuves provenant de processus différents. Commande : `python scripts/properties-order-tests/TestCollectResults.py`. La consolidation finale utilise `python scripts/properties-order-tests/CollectResults.py --write`.

Historique Revit 2023 : le journal `journal.3172.txt` indiquait Revit 2023.1.9 et `LicenseUpd(1) Adlsdk Error:(3) vendor:SERVICE`. Le lancement ultérieur de `2023-r8` a réussi et les quatre éditeurs, Annuler, Rétablir, Échap, restauration, retour de sélection et sélection multiple ont été vérifiés indépendamment. Aucun compte, jeton ou mécanisme d'authentification n'a été modifié par le collecteur.

Les cinq builds de production corrigés ont été installés le 21 octobre 2026 à 14 h 54, avec vérification des empreintes et sauvegarde des manifestes. Le reçu est conservé sous tmp/properties-order-tests/installed-production.json. Les sessions Revit déjà ouvertes conservent leur ancienne DLL : un redémarrage de l’application charge la correction. Les modules temporaires de test ont été retirés des inscriptions de démarrage.
