# ViewDeck — miniatures dans les onglets natifs

La galerie séparée a été remplacée par un `HeaderTemplate` sur chaque onglet Revit existant. Il n'y a plus de ligne ajoutée à la grille, plus de seconde collection de vues et plus de gestionnaire de navigation personnalisé.

Les tests STA vérifient : nombre d'onglets inchangé, identité Header/Content, image sous le titre, restitution des propriétés/styles/bindings, 30 ON/OFF, préservation des modifications externes et distinction des projets avec des noms de vues identiques. Un exemple WPF est rendu dans `bin/Debug/native-tabs-preview.png` ; ce n'est pas une capture de Revit.

## Essai dans Revit 2024

1. Charger la compilation `Release2024` par la méthode habituelle et redémarrer Revit.
2. Ouvrir un plan, une coupe et une feuille. Activer « Onglets : ON » sous « Couleurs ».
3. Vérifier qu'il reste une seule série d'onglets : chaque nom figure au-dessus de son image, dans le même onglet. Aucune galerie n'apparaît au-dessus.
4. Cliquer sur le nom ou l'image, fermer via la croix ajoutée en haut à droite de la miniature et tester le clic droit/déplacement natif. La croix appelle `LayoutDocument.Close()` sur le modèle exact de cet onglet, en respectant `CanClose` ; elle ne supprime jamais une `DB.View`. Tester une vue inactive, deux vues homonymes de projets différents, et la dernière vue ouverte (Revit doit garder la maîtrise de sa fermeture et de ses éventuelles confirmations). OFF restitue la fermeture native compacte.
5. Désactiver : les onglets doivent retrouver leurs dimensions et leur présentation exactes. Répéter ON/OFF, puis WT/TW.
6. Ouvrir deux documents ayant une vue de même nom. Vérifier les miniatures ; en cas d'identité ambiguë, l'aperçu reste en attente jusqu'à l'activation réelle de l'onglet, plutôt que de montrer une autre vue.
7. Ouvrir, renommer et fermer des vues/documents. Vérifier qu'aucun ancien aperçu n'est réattribué à une autre vue.
8. Tester les nombreux onglets et leur débordement natif, le thème sombre, le DPI 100/150 % et les fenêtres en mosaïque. Les fenêtres flottantes séparées ne sont pas ciblées par cette V1.
9. Avec plusieurs miniatures déjà visibles (dont des vues homonymes), basculer OFF puis ON sans activer les vues : les images doivent revenir immédiatement. Renouveler avec un onglet natif reconstruit et le même modèle de vue.
10. Rafraîchir une miniature via le bouton Miniature habituel : l'ancienne image doit rester affichée jusqu'à disponibilité de la nouvelle. Un fichier temporairement verrouillé, absent ou invalide ne doit pas remplacer une image valide par une vignette vide.
11. Survoler le nom ou la miniature pendant environ 500 ms : un aperçu agrandi apparaît sous l'onglet, sans changer la vue. Sortir de l'onglet, cliquer ou quitter Revit (Alt+Tab) : l'aperçu doit se fermer sans intercepter la navigation/fermeture native. OFF rétablit des onglets compacts mais conserve le survol. Vérifier ce survol dès le démarrage en OFF, sans avoir activé ON.
12. Répéter le survol de plusieurs onglets, y compris sans image, avec noms longs et près d'un bord d'écran. Vérifier qu'une image mise à jour se renouvelle dans l'aperçu sans popup supplémentaire.

OFF ne vide ni les PNG enregistrés ni les associations apprises entre modèles d'onglets et vues. Les associations sont conservées avec des clés faibles (libérables à la fermeture des onglets). Les nouvelles images sont décodées avant affichage et les PNG exportés remplacent atomiquement le cache existant après validation. Les tests couvrent ces cycles, le rechargement disque, les erreurs de lecture et le remplacement réussi.

Le survol réutilise uniquement l'image déjà en mémoire et n'appelle pas l'API Revit. Il est attaché directement aux événements souris de l'onglet natif, indépendamment de sa décoration ON/OFF. Le ToolTip natif conserve sa valeur pour identifier/colorer le document ; seul son affichage est remplacé. En OFF, aucune génération en lot supplémentaire n'est lancée par ViewDeck ; le cache et la génération habituelle après activation de vue restent utilisés. Les nouvelles générations sont en 720 px ; les anciens PNG restent conservés à leur résolution d'origine jusqu'au prochain rafraîchissement normal. `hover-preview.png` est un rendu WPF de test, pas une capture de Revit.

## Résumé des changements — V1 en session

À droite du titre du survol, sur une seule ligne : trois pastilles compactes `+` vert (ajouts), `~` orange (modifications/déplacements), `−` rouge (suppressions). Seuls les compteurs positifs sont visibles. Aucune phrase de statut, aucun détail de catégorie, aucun « Vue active » ; un bilan indisponible, en cours ou vide n'affiche rien. Un suivi partiel ajoute uniquement le symbole `≈` aux compteurs connus. L'en-tête garde une hauteur de 24 DIP et l'image dispose de 340 DIP de hauteur (contre 290 auparavant), indépendamment des pastilles. La largeur et les pixels de l'image restent inchangés.

Le compteur porte sur des **éléments distincts**, pas sur un nombre de transactions. Un ajout suivi d'une modification reste un ajout ; un ajout suivi d'une suppression disparaît du bilan. Une suppression puis restauration est une modification. Ce n'est pas un diff géométrique exact : un aller-retour/Annuler peut rester dans l'activité détectée.

Le périmètre est celui des éléments potentiellement visibles dans les vues ouvertes du document actif : union de l'ancienne et de la nouvelle appartenance au collecteur Revit de la vue. Une porte déplacée hors d'une vue ou un mur supprimé restent donc détectables grâce à l'état précédent. Les états intermédiaires entre deux analyses, les effets indirects (types, paramètres globaux, filtres, phases…), les liens et l'occlusion pixel par pixel ne sont pas intégralement analysés. Les changements de types connus rendent le suivi partiel. Feuilles et nomenclatures n'affichent pas de compteurs.

Les instantanés légers de catégories/positions sont amorcés progressivement (200 éléments / 20 ms maximum par passage, hors appel du collecteur). Les catégories restent internes au suivi. Une translation des positions/endpoints (> 1 mm, même déplacement des deux extrémités pour les courbes) est reconnue comme déplacement, inclus dans `~`. Le collecteur est limité à 50 000 éléments par vue, le journal à 1 000 éléments par vue et les événements en attente à 5 000 par document ; les dépassements rendent le suivi partiel (`≈`). Un seul collecteur de vue est relu par rafraîchissement, après des changements, dans un contexte Revit valide. Attention : un premier collecteur de vue peut tout de même déclencher une régénération Revit coûteuse sur un gros projet.

Revenir dans une vue remet uniquement son bilan à zéro. Les modifications effectuées pendant qu'elle est active sont déjà considérées comme vues. OFF/ON ne change pas ce suivi. Pour un onglet jamais activé, le suivi commence à son ouverture, sans inventer de dernier passage. Le suivi repart à l'ouverture d'un onglet ou au redémarrage ; il n'est pas un historique persistant. Les PNG restent indépendants : un compteur récent ne signifie pas que l'image en cache a déjà été remplacée.

Essai conseillé dans une copie du projet :

Les éléments sélectionnés sont mémorisés en priorité (25 / 10 ms maximum) pour identifier une porte déplacée ou un mur supprimé sans attendre la fin de l'amorçage complet.

1. Ouvrir deux plans/coupes/3D ayant des éléments en commun. Laisser quelques secondes à l'amorçage (plus sur les gros projets), visiter la vue B puis revenir dans A.
2. Dans A, déplacer une porte, créer/modifier des canalisations et supprimer un mur. Survoler B sans l'activer : laisser quelques secondes à l'analyse, puis vérifier les trois compteurs correspondant aux éléments potentiellement concernés. Revit peut aussi modifier des éléments dépendants (raccords, hôtes…).
3. Vérifier une vue ne contenant pas ces éléments : aucun compteur global du projet ne doit lui être attribué. Déplacer un objet hors de B et vérifier que l'ancienne appartenance est conservée pour l'impact.
4. Basculer OFF/ON : mêmes compteurs et mêmes images. Activer B : ses pastilles disparaissent, sans texte de remplacement. Revenir dans A, puis survoler B : les anciens changements ne sont plus comptés.
5. Modifier plusieurs fois la même porte, annuler/rétablir une suppression, créer puis supprimer une canalisation : vérifier les règles d'agrégation ci-dessus.
6. Tester fermeture/réouverture, changement de document, noms homonymes, vue non prise en charge et gros lots. Une erreur ou une limite ne doit pas annoncer un faux zéro ni désactiver les miniatures.

Les tests WPF couvrent le routage réel du clic de la croix via `ContentPresenter`, le respect de `CanClose`, les doublons de noms, l'inertie après OFF, l'appartenance avant/après, les catégories supprimées, la déduplication, la remise à zéro indépendante, les limites et la mise à jour des informations du survol. Ils ne remplacent pas la validation API dans Revit.

## Versions

Le projet .NET Framework 4.8 cible Revit 2023 pour Debug/Release et Revit 2024 pour Release2024. La compilation ne certifie pas l'intégration visuelle native. Revit 2025/2026 et les versions antérieures à 2023 ne sont pas validés par ce projet ; 2025 demande notamment un portage .NET 8. Les versions récentes nécessitent leur propre compilation et des essais du modèle d'onglet interne.

Les caches existants sont réutilisés, y compris pour les autres documents ouverts identifiés. Les images manquantes sont générées progressivement pour le document actif uniquement. Les vues non imprimables restent sans aperçu. L'intégration dépend du modèle de présentation interne de Revit : les tests WPF et la compilation ne remplacent pas cet essai dans Revit réel.
