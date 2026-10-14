# Résultats des boutons

Dans Supabase consulter `v_button_results` (bilan par bouton) et `v_button_user_summary` (bilan par licence et poste). `v_button_result_events` permet de voir le détail des opérations : mode, date, version et compteurs. Toutes ces vues sont réservées à l'administration et au rôle serveur.

Le catalogue initial contient 68 identifiants de commandes suivies. Les nouvelles commandes apparaissent automatiquement dès leur première utilisation, même avant l'ajout au catalogue. Le titre affiché suit le dernier libellé reçu ; l'identifiant `ButtonId` reste la clé de regroupement.

## Ce que les chiffres signifient

- `uses` compte les lancements distincts. Ouvrir une palette ne prouve pas une modification. Réussites, annulations et échecs sont séparés selon le résultat renvoyé par la commande.
- `result_operations` compte les bilans mesurés hors tutoriels. Une action différée dans une palette produit un bilan, sans compter un nouveau lancement du bouton.
- Une session manuelle Auto réservation qui crée dix réservations produit un bilan de dix. Dix sessions qui en créent chacune une produisent dix bilans, total dix.
- Organisateur compte uniquement les valeurs finales différentes des valeurs initiales, après validation. Les réinitialisations sont séparées des renommages ; les valeurs temporaires de renumérotation ne sont pas comptées.
- Les compteurs génériques des commandes synchrones distinguent les identifiants Revit ajoutés, modifiés et supprimés pendant leur exécution. Un élément créé puis supprimé dans l'opération n'est pas compté comme création persistante. Un élément modifié plusieurs fois compte une fois par opération. Ces chiffres peuvent inclure des éléments techniques Revit et ne signifient pas systématiquement des objets visibles ou renommés. Si une annulation de transaction/groupe est observée, le bilan générique est omis pour éviter un résultat trompeur.
- Les changements de sélection sont comptés lorsque la sélection finale diffère de la sélection initiale, sans enregistrer les identifiants.
- Les compteurs spécialisés ajoutés couvrent Auto réservation, Organisateur, chargement/rechargement de familles et collections, chargement par la rosace, exports Excel/PDF de nomenclatures, exports DWG, exports d'unités et de gabarits, enregistrement d'images IA, analyses et rapports Clash 3D. Les mutations différées de MEP Booster et la correction du tutoriel Clash ont leur propre observation de transactions.
- Une famille déjà présente et réutilisée ne compte pas comme nouvelle famille chargée. Demander le placement d'une famille ne prouve pas qu'une instance a été placée.
- `files_exported` compte les sorties réussies, pas chaque image ou fichier auxiliaire produit par un format. En DWG, une feuille exportée représente une sortie principale. Les fichiers internes de cache/configuration ne sont pas des exports utilisateur.
- Les quantités du tableau principal excluent les opérations marquées comme tutoriels. `tutorial_result_operations` permet de consulter séparément leur volume ; les quantités détaillées de formation restent dans `v_button_result_events`.
- `all_quantities` contient les compteurs supplémentaires, tels que les éléments inchangés, ignorés, les familles réutilisées ou les unités exportées.

Les boutons de consultation, d'interface et les jeux ne reçoivent pas un faux nombre d'objets produits. Ils gardent leurs statistiques d'usage ; l'absence de bilan mesuré ne prouve pas que l'outil n'est pas utile. La capture commune s'arrête au retour de la commande : elle ne surveille pas les modifications ordinaires ultérieures de l'utilisateur. Les workflows asynchrones ont besoin d'un compteur à leur point de résultat ; les points spécifiques ajoutés sont listés ci-dessus.

## Transport et historique

Les commandes synchrones ajoutent leur résultat à l'événement d'usage existant. Les actions différées envoient un événement de résultat distinct, dans le même buffer de télémétrie envoyé par lots. Chaque lancement et chaque bilan ont un UUID ; les vues éliminent les retransmissions. Les snapshots avant/après restent uniquement en mémoire : aucun nom d'élément, de famille, de projet ou chemin de sortie n'est ajouté aux métriques.

Les chiffres commencent avec le plugin recompilé et diffusé. Les versions précédentes n'enregistrent pas ces quantités et ne peuvent pas fournir cet historique. Le pseudonyme utilisateur correspond à licence + poste, pas à une personne distincte sur un poste partagé.

## Vérifications

- `scripts/operation-results-tests/VerifyCounts.ps1` teste les méthodes de production de comptage et d'identification des bilans.
- `scripts/operation-results-tests/VerifyMeasurement.ps1` teste l'observation commune avec des événements Revit simulés.
- `supabase/sql/verify_button_operation_results.sql` teste les lots, les opérations unitaires, les doublons, les actions différées, les tutoriels, les valeurs invalides et les droits, avec des données annulées à la fin.

Les vues historiques `v_button_result_stats` et `v_button_user_results` restent disponibles ; le tableau général recommandé est `v_button_results`.
