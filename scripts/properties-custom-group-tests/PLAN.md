# Groupe personnalisé dans la palette native — laboratoire Revit 2025

Ce dossier contient le protocole, les collecteurs indépendants et le harnais isolé du prototype natif Revit 2025. Le harnais peut lancer une session de laboratoire et manipuler la palette uniquement dans sa maquette jetable et son processus désigné. Les collecteurs n'appellent aucune API native. Aucune modification de production ni conclusion des anciens tests d'ordre ne sert de preuve d'insertion d'un groupe. Les comptes rendus EVIDENCE et PERF précisent les vérifications réellement effectuées et leurs limites.

## Audit du harnais existant au 23 octobre 2026

`properties-order-tests/Build.ps1` compile les sources de production avec `BIMAESTRO_PROPERTIES_TESTS`, donc un succès de ce harnais n'est pas une preuve que le même comportement est activé dans une DLL de production. Le build doit rester isolé par dossier et nom d'assembly pour chaque itération chargée. Utiliser un nouvel AddInId, une nouvelle classe d'application et un nouveau manifeste pour le prototype de groupe ; ne pas remplacer l'enregistrement du laboratoire d'ordre.

`Start.ps1` protège l'identité de son propre enregistrement, mais lance Revit après copie dans AppData : le nouveau prototype doit employer son propre espace de noms et son propre chemin de registration. `Send.ps1` n'a qu'un nom de commande et une date de fichier pour corréler les réponses ; une réponse comportant un identifiant de requête unique et le nonce de session évite une confusion après timeout/rejeu. Un timeout de l'interface ne signifie pas que la mutation n'a pas été exécutée.

`Probe.cs` possède aujourd'hui garde PID/version, fixture à chemin exact, snapshots de tous les paramètres, diagnostics natifs, timer sur le dispatcher, métriques et désactivation temporaire de l'apparence installée. Cette dernière utilise des champs privés par réflexion et laisse la preview de production suspendue pendant la session : préserver et restaurer exactement son ancienne valeur dans le nouveau harnais, ou consacrer le processus exclusivement au laboratoire. Ne jamais écrire dans les préférences utilisateur.

La production actuelle contient `ResetCurrentCell(false)`, `DeferDrawing`, identité `findParamIdRow`, synchronisation différée de la sélection et préservation du filler terminal. Ces garanties concernent le déplacement de lignes existantes. Elles ne prouvent ni le contrat d'insertion/suppression, ni la création d'un véritable en-tête, ni l'appartenance des paramètres au nouveau groupe.

## Séquence expérimentale

1. Créer un document jetable avec deux murs, deux niveaux et des paramètres éditables texte, booléen, double et ElementId. Initialiser seulement la fixture par API, puis sauvegarder. Photographier par API les valeurs et le schéma des paramètres avant toute manipulation native.
2. Identifier les exports exacts d'insertion/suppression et de copie de style sur les binaires installés. Enregistrer leur signature, le module et son SHA256. Ne pas déduire l'ABI d'un nom de méthode seul. Une exportation disponible n'est pas une qualification de son appel.
3. À la première mutation seulement, exiger PID/version/nonce/fixture/doc sélectionné uniques, grille courante et aucune saisie active. Capturer les cellules et identités natives. Un éditeur actif bloque l'opération ; ne pas valider implicitement son buffer.
4. Insérer un seul en-tête et les lignes auxiliaires prévues explicitement par le contrat natif. L'export `addGroupHeader` identifié dans le laboratoire peut produire un en-tête et un spacer, soit deux lignes ; capturer ce nombre dans `insertedRowCount`. Vérifier intégralement les identités des anciennes lignes et les valeurs du document, avant de poursuivre. Une copie de style doit produire une propriété native possédée correctement par la grille, pas réutiliser sans contrat le pointeur mutable d'un ancien en-tête.
5. Transformer cette ligne en en-tête natif. Conserver son marqueur, l'état lecture seule, le contrôle natif, les bordures et les rectangles observés. Un simple libellé contenant `^` ou une recoloration n'est pas une preuve de groupe natif : le repli/dépli et l'absence d'éditeur de valeur doivent être démontrés.
6. Déplacer des paramètres provenant d'au moins deux groupes d'origine sous le nouvel en-tête, en conservant chaque vraie ligne, son identité et son éditeur. Laisser les en-têtes d'origine en place, même vides, pour ce prototype. Ne pas ajouter un faux paramètre au modèle et ne pas modifier `Definition.GetGroupTypeId`, les bindings ou les définitions partagées.
7. Après CHAQUE déplacement, relire les cellules, `findParamIdRow` et le modèle. Le nom du paramètre doit correspondre à son identifiant, sans double occurrence. La capture du contexte API de production ne doit pas réattribuer artificiellement un paramètre au nouveau groupe sur la base de son libellé.
8. Replier le nouveau groupe dans l'interface puis le déplier. Le repli doit masquer exactement ses membres ; les paramètres restés dans les autres groupes conservent leur hauteur visible. Vérifier ensuite la saisie des membres ET des voisins restés dans les groupes d'origine.
9. Valider ou refuser le prototype sur la matrice ci-dessous. Une bibliothèque native non reconnue, une identité ambiguë ou un changement du schéma interrompt les essais. Aucune activation du produit ne découle automatiquement d'un prototype réussi.

## Matrice de tests

| Famille | Vérification exigée |
|---|---|
| Insertion seule | Exactement les lignes prévues ajoutées (en-tête et spacer pour addGroupHeader) ; modèle et schéma identiques ; toutes anciennes identités conservées ; filler toujours terminal. |
| Transfert entre groupes | Identités choisies sous le nouvel en-tête, sans duplication ; les groupes d'origine ne capturent pas leurs clics ; toutes autres lignes conservées. |
| Texte | Valeur unique tapée dans le vrai éditeur puis Entrée ; exactement la valeur attendue et aucun delta collatéral. |
| Booléen | Vrai clic sur la case, valeur API attendue ; l'éditeur voisin et les cases restées au groupe d'origine restent corrects. |
| Double | Valeur avec unité saisie dans Revit ; comparaison en unités internes et tolérance explicite ; dépendances géométriques autorisées par valeurs attendues explicites. |
| ElementId/liste | Choix d'un autre niveau dans la vraie liste, puis valeur API de ce niveau ; souris et clavier distingués. |
| Lecture seule | Les paramètres calculés et cellules grisées gardent leur impossibilité de modification ; aucune saisie ne doit apparaître sur l'en-tête ajouté. |
| Voisins non déplacés | Saisir aussi des paramètres non déplacés juste avant/après les points d'insertion ; aucun autre paramètre ne change. |
| Entrée/Échap/Tab/Maj+Tab | Validation, annulation et navigation sans écriture dans la mauvaise ligne ; buffers du vrai GXEDIT observés par timer si Idling est suspendu. |
| Annuler/Rétablir | Retour exact aux snapshots de valeurs ; le déplacement visuel n'ajoute aucune transaction métier à la pile Undo. |
| Repli/dépli et défilement | Seuls les membres attendus deviennent invisibles ; les éditeurs restent associés après redéploiement et défilement hors écran. |
| Reconstruction | Changement de sélection mur/vue/mur, second mur, multiple, changement de vue et reconstruction native ; aucun doublon d'en-tête. |
| Opération refusée pendant saisie | Buffer actif laissé intact ; aucune ligne insérée/déplacée/supprimée ; aucune validation implicite. |
| Fautes injectées | Échec après insertion et après chaque déplacement ; rollback vérifié, à chaque point, sans perte de valeur ni résidu du nouveau groupe. |
| Restauration normale | Déplacer les vraies lignes à leur place, supprimer seulement la ligne créée, puis comparer structure originale et valeurs métier courantes. |

« Exhaustif » signifie ici couvrir toutes les opérations et tous les types d'éditeurs réellement présents dans la fixture, y compris les voisins. Les boutons spécialisés (image, association globale, boîte de dialogue de type) restent non qualifiés tant qu'une fixture et une preuve dédiées ne les exercent pas. Les identités natives ne dispensent pas d'un essai d'écriture réel.

## Rollback et reconstruction

Conserver un journal des opérations confirmées avec token de ligne stable, position initiale, position finale et identifiant de paramètre. Un index seul devient faux après insertion. Vérifier une opération avant de l'ajouter à ce journal. Restaurer à l'inverse et relire après chaque étape ; supprimer uniquement l'en-tête créé, identifié par un token et par sa position vérifiée.

Un échec de vérification de cellule après mutation peut aussi signifier que la mutation a eu lieu partiellement : ne pas supposer que l'état précédent est intact. Bloquer de nouvelles mutations et faire reconstruire la palette par Revit si l'inverse ne peut plus être prouvé. Le service doit cesser de réinsérer automatiquement pendant cette reconstruction. Ne pas laisser interagir l'utilisateur avec une palette dont la correspondance n'a pas été rétablie.

Un rollback d'affichage conserve les valeurs actuellement validées ; il ne restaure jamais silencieusement les valeurs initiales du modèle. La baseline de structure et la baseline de valeurs sont donc des objets distincts. Aucun `Transaction.RollBack` Revit ne remplace l'inverse des mutations de grille.

## Contrat de preuve et collecte

Voir [CONTRACT.md](CONTRACT.md). `CollectResults.py --run DOSSIER` produit une synthèse sur stdout. `--output CHEMIN` écrit explicitement une preuve compacte. Il ne pilote aucune UI et n'appelle aucune API native. `TestCollector.py` vérifie que les fausses preuves (doublons, schéma changé, écriture collatérale, repli hors groupe ou rollback incomplet) sont rejetées.
