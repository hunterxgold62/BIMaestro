# Maquette de formation BIMaestro

`BIMaestro_Apprentissage_2024.rvt` est la maquette principale, créée avec Revit 2024 pour les trois premiers parcours guidés. `BIMaestro_Apprentissage_2023.rvt` reste disponible pour l'ancien essai. La nouvelle scène comprend un mur traversé par une canalisation pour **Auto réservation**, un petit espace meublé pour **Qui a fait ça ?**, et trois vues 3D nommées selon les exercices.

Les trois vues 3D sont créées en niveau de détail **Élevé** et en style visuel **Couleurs uniformes**. L'ouverture d'un parcours applique aussi ces réglages aux vues d'une maquette de formation déjà créée ; enregistrer cette maquette pour les conserver dans son fichier.

Dans Revit, ouvrir l'onglet **BIMaestro**, puis **Couleurs > Parcours guidés**. Commencer par **Découverte rapide** pour voir les trois fonctions, puis choisir à la fin si l'on souhaite pratiquer dans la maquette. Pendant un exercice détaillé, Pikachu indique d'abord la commande du ruban, puis les commandes dans sa fenêtre. Le bouton **Créer et ouvrir la maquette de formation** régénère une maquette vierge. Il ne remplace pas un fichier existant : Revit crée alors un fichier horodaté.

La **découverte rapide** ne modifie pas le projet. À la fin, elle propose les trois exercices détaillés ou la possibilité de s'arrêter. Pour passer directement à la pratique, déplier **Aller directement aux exercices détaillés** dans la première fenêtre. Chaque exercice fait manipuler les commandes et vérifie le résultat dans la maquette.

Le guide avance lorsque l'action demandée est effectuée. Si un filtre ou un réglage est déjà actif, le bouton **Suivant** permet de continuer. L'étape de restauration attend qu'un élément soit effectivement recréé ; Auto réservation confirme après l'exécution qu'une nouvelle réservation a été placée.

Le bouton **Recommencer les exercices de la maquette** remet les deux meubles d'exercice à leur position initiale et retire seulement les réservations marquées comme créées pendant le parcours guidé. Il ne remet pas à zéro les couleurs personnelles. Les anciennes entrées d'historique restent disponibles ; le parcours suivant crée de nouvelles suppressions.

Dans **Auto réservation**, le guide laisse le mode automatique désactivé : sélectionner dans la vue 3D la canalisation, puis le mur, pour poser soi-même la réservation.
Si le mauvais élément est choisi, Pikachu explique lequel sélectionner et redemande le clic. Après la création, BIMaestro sélectionne et cadre la réservation pour en examiner la position et les dimensions.

Dans **Couleurs et vues**, le guide passe par **Fond et aperçu** pour changer la couleur du fond, puis par **Dossiers** et **Icônes**. Il fait ajouter une règle pour le dossier `Plans d'étage` et une autre pour `Vues 3D` ; il ne crée aucun type de vue Revit. Chaque règle associe le nom exact du dossier à une image.

Dans **Qui a fait ça ?**, le lancement du parcours supprime deux objets de mobilier marqués pour l'exercice. Ce sont de vraies suppressions enregistrées dans l'historique local ; filtrer sur **Suppressions**, sélectionner une carte et cliquer sur **Restaurer les éléments**. Un troisième objet reste visible pour servir de repère. La maquette seule ne transporte pas le journal local : il faut lancer la préparation sur chaque poste.

La génération d'une autre maquette exige un gabarit Revit contenant des canalisations, la famille `CML_Réservation rectangulaire murale.rfa` dans `Documents\Revit\Réservation` et `CML_Table ronde + chaise.rfa` dans `Documents\Revit` ou `Documents\Revit\A-Famille Revit\Mobilier`.
