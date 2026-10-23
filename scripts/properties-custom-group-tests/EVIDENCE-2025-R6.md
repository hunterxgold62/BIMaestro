# Prototype de groupe natif — état des preuves r6

La série Revit 2025 `2025-r6` (build 25.4.41.14, PID 30196) démontre deux saisies dans le groupe personnalisé : un texte et un double. **Le cycle de vie, le booléen dans le groupe et le repli/dépli restent non qualifiés.** La suite est prévue dans une nouvelle série après correction du précontrôle de synchronisation.

| Scénario | Preuve avant | Vérification après | Conclusion |
|---|---|---|---|
| Commentaires | 12:43:07 UTC, 46 lignes, en-tête natif reconnu, six membres correctement mappés. | 18 550 paramètres comparés ; seul Commentaires passe de `ORDER_BASELINE` à `ORDER_BASELINEx` ; zéro différence de schéma. | PASS pour cette saisie. |
| Décalage inférieur | 12:50:42 UTC, même groupe natif et six identités valides. | 18 550 paramètres comparés ; seul le décalage passe de 0 à `0.02952755905511811` en unités internes, soit 9 mm ; zéro différence de schéma. | PASS pour cette saisie. |
| Limite de pièce | `bool-before.json` à 12:48:07 UTC contient 44 lignes et aucun groupe personnalisé. | Une seule valeur passe de 1 à 0, sans différence de schéma. | Écriture métier correcte ; **ne qualifie pas l'éditeur dans le groupe personnalisé**. |
| Repli | `collapsed.json` contient seulement cinq lignes du contexte navigateur. | Aucun groupe ni ensemble de membres vérifiable dans cette capture. | Capture inadéquate ; non qualifié. |
| Dépli | `expanded.json` retrouve 44 lignes mais aucun en-tête personnalisé. | Le groupe n'est pas présent. | Non qualifié. |

Après les deux saisies valides, l'utilisateur a volontairement changé le focus ou désélectionné l'élément avant la capture API. Le collecteur accepte ce contexte différent **uniquement après** avoir vérifié le groupe et les six correspondances dans la capture précédant l'entrée. Il compare ensuite l'intégralité du modèle et du schéma, avec PID, build, document et chronologie identiques. Une valeur correcte après coup ne réhabilite pas une capture avant sans groupe.

Les fichiers `run.json`, `interactions.json` et `summary.json` du dossier temporaire de r6 conservent les métadonnées, les deux paires admissibles et les SHA256 de leurs sources. Les preuves brutes ne sont pas réécrites. Les captures incorrectes de repli/dépli restent présentes et explicitement rejetées.

Une tentative automatique de réapplication s'est arrêtée à 12:45:26 UTC faute d'identités natives vérifiables ; le rapport indique que la mutation n'avait pas commencé. Le mécanisme de récupération n'est donc pas déclaré fiable par ces deux seules saisies. Les essais de liste, paramètres partagés métier, voisins non déplacés, annulation, restauration complète et refus pendant saisie restent à produire avec des captures admissibles.
