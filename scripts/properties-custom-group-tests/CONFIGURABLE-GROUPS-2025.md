# Groupes personnalisés intégrés — 26 octobre 2026

Dans **Couleur → Propriétés**, le champ en haut et **＋ Créer un groupe** ajoutent un bandeau à l'aperçu. Un paramètre reconnu et modifiable peut être glissé sur ce bandeau, ou sélectionné puis affecté avec **Placer ce paramètre dans… → Déplacer le paramètre**. Le groupe Revit d'origine est toujours une destination de retour possible. Un bandeau sélectionné expose à droite son nom, **Renommer** et **Supprimer le groupe**. Les couleurs utilisent les mêmes commandes que les groupes natifs. **Enregistrer mes réglages** conserve l'ensemble dans la portée choisie.

Le groupe est une présentation de la palette : aucune définition de paramètre, liaison, GUID ou valeur du modèle n'est modifiée. La suppression du bandeau remet ses paramètres dans leurs groupes natifs. L'aperçu gauche conserve l'origine et ses numéros ; les nouveaux bandeaux portent un repère « + ».

## Implémentation

- Définitions persistantes avec identifiant stable, nom et liste des clés de paramètres. Héritage par portée ; une liste vide explicite retire un héritage plus général. Une portée plus précise ne peut pas masquer silencieusement une modification.
- Références vers des sauvegardes immuables dans `PropertiesAppearance.json.groups/`. La référence est conservée dans la carte d'ordre connue des anciens BIMaestro : une réécriture de préférences par un ancien binaire ne supprime pas les définitions. Le JSON principal reste au format 1. Les premières définitions expérimentales intégrées au JSON sont migrables. Déplacer manuellement les préférences nécessite aussi ce dossier de sauvegardes.
- Une seule permutation gère désormais les groupes natifs et personnalisés. Les bandeaux sont insérés avec les fonctions internes qualifiées, puis les blocs des vrais paramètres sont déplacés. Le renommage/la modification de composition restaurent d'abord les lignes originales, retirent uniquement les lignes ajoutées en queue, puis reconstruisent les bandeaux souhaités.
- Identités natives vérifiées avant/après mutations ; contrôles de lignes, refus pendant saisie et remise en état prudente. Les champs grisés et absents ne sont pas transférés. La liste d'origine reste disponible même lorsque la palette native est déjà personnalisée.
- Activation native limitée aux bibliothèques Revit 2025 qualifiées par SHA256, sur murs/vues déjà admis par le service. Une version/bibliothèque non qualifiée garde le profil sans insérer les bandeaux et conserve la possibilité d'appliquer les couleurs et ordres ordinaires. Qualification négative mise en cache pour éviter des lectures répétées de bibliothèques.

## Vérifications

48 assertions de l'éditeur WPF : création, glisser-déposer, nom, retour au groupe d'origine, suppression, persistance, héritage, portée plus précise, champs grisés, conservation du brouillon et réécriture compatible avec les anciennes préférences. Rendu vérifié dans `output/appearance-tabs/proprietes-groupes.png`.

Dans les fixtures Revit 2025 :

- Un mur : création de deux groupes, renommage, transfert, suppression, effacement complet ; 29 identités visibles conservées.
- Deux murs sélectionnés : mêmes opérations, mêmes 29 identités conservées.
- Plan de structure : mêmes opérations, plus un groupe vide et un paramètre explicitement grisé dans une définition ; 36 identités visibles conservées. Le champ grisé reste à son emplacement natif.
- À chaque étape admise : 18 550 valeurs et descripteurs de paramètres identiques à la baseline ; limites physiques des groupes vérifiées indépendamment des métadonnées de permutation. Après effacement, toutes les lignes originales sont retrouvées dans le même ordre.

Preuves : `tmp/properties-custom-group-tests/2025-groups-ui-r1/wall`, `.../multiple`, `2025-groups-ui-r2` ; collecteur `VerifyConfigurableGroups.py`.

Le premier scénario de plan visait `VIEW_SCALE`, qui est le champ grisé « Valeur de l'échelle 1: ». Le moteur l'a laissé natif conformément au refus de déplacer les champs grisés ; l'attente du collecteur initial était incorrecte. Le scénario corrigé vise le champ modifiable `VIEW_SCALE_PULLDOWN_METRIC` et vérifie séparément le refus du champ grisé. L'ancien export est conservé dans `2025-groups-ui-r1/view-locked-scale`.

L'essai de saisie physique n'est pas qualifié : le champ a été ouvert mais la sélection/focus a changé pendant le pilotage. La vérification finale `report-edit-unchanged.json` confirme l'absence d'écriture dans le modèle. Aucun résultat positif de saisie n'est déduit des seuls contrôles d'identité.

Compilation complète Revit 2025 : 0 erreur, 314 avertissements existants/multiplateforme. Package : `tmp/properties-custom-group-tests/groups-ui-2025-final`.

## Charger la compilation

Fermer Revit 2025 puis lancer `Lancer-groupes-personnalises-2025.cmd`. Le lanceur vérifie les fichiers, charge temporairement la nouvelle DLL via le manifeste 2025 et confirme son chemin dans les modules du processus. Le manifeste original est restauré ensuite ; la DLL installée n'est pas écrasée. Aucun observateur de laboratoire n'est nécessaire dans cette session. Ouvrir ensuite le projet de son choix et **Couleur → Propriétés**.

Le lanceur a été validé sans lancement ; le chargement complet de cette nouvelle compilation reste à confirmer lors de son exécution. Les autres versions Revit ne sont pas déployées par ce lanceur.
