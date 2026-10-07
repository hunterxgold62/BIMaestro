# Parcours Clash 3D

Le parcours « 10 · Clash 3D » est accessible depuis **Parcours guidés** et la découverte rapide. La maquette nouvellement créée contient sa scène ; les anciennes maquettes `BIMaestro_Apprentissage_…` la reçoivent à l'ouverture de ce parcours. Chaque lancement reconstruit uniquement les quatre objets portant les repères `BIMaestro_DEMO_CLASH_…`.

La scène comporte trois tuyaux DN100 et un mur. L'analyse initiale doit contenir exactement deux intersections confirmées : une traversée du mur et un croisement entre tuyaux non raccordés. Le bouton de formation relève le tuyau transversal de 300 mm. La seconde analyse doit conserver uniquement la traversée du mur. Les extrémités libres sont volontaires ; le contrôle des connecteurs ouverts reste désactivé pour cet exercice.

Les étapes expliquent le périmètre, les contrôles, le seuil volumique en mm³, l'inspection autonome, le cadrage dans Revit, la correction puis la vérification et l'export. Le guide attend les résultats des opérations ; un clic sur Analyser, une annulation ou un export vide ne valide pas l'étape correspondante. Le détail et le cadrage utilisent le conflit entre tuyaux présélectionné. Après le cadrage, une petite carte laisse observer Revit avant de revenir au guide.

Le statut **Traité** reste une décision humaine, distincte d'une correction géométrique. La traversée du mur ne constitue pas une conclusion automatique sur la présence ou la conformité d'une réservation. Aucun lien n'est inventé dans la scène : le guide explique leur sélection pour les projets ordinaires.

La colonne du guide laisse les commandes accessibles. Précédent permet de revoir une opération accomplie sans imposer un deuxième déplacement. Le bouton **TUTO** de Clash 3D propose aussi un parcours explicatif dans un projet ordinaire, sans bouton de correction. Les réglages de l'exercice ne sont pas enregistrés dans les préférences personnelles. Quitter retire la colonne et les commandes de formation ; fermer Clash 3D restaure la vue par son événement externe habituel.

## Banc du parcours

`TutorialValidation.cs` référence le plugin complet, compilé sous une identité d'assemblage distincte pour empêcher Revit de résoudre sa copie installée à la place. Les scènes, l'interface, les guides, les événements externes et l'écriture des exports sont ceux du plugin. Les fenêtres et les rapports sont enregistrés dans le dossier du passage. Les préférences et décisions du banc sont redirigées vers ce dossier.

```powershell
& ./scripts/clash3d-tests/BuildTutorial.ps1 -RevitVersion 2024 -RunName clash-tutorial-check
& ./scripts/clash3d-tests/StartTutorial.ps1 -RevitVersion 2024 -RunName clash-tutorial-check
```

Le lanceur utilise son propre manifeste temporaire et refuse de démarrer si un autre banc natif est encore inscrit. Le banc refuse toute instance contenant déjà des documents. Lire `result.json`, puis vérifier `finished.txt`, `exited-cleanly.txt` et la sortie du processus indiqué par `launched-pid.txt`. Les captures `tutorial-*.png` servent à vérifier la présentation aux tailles normale et minimale.

La sélection native du chemin dans le dialogue Enregistrer est extérieure au banc ; celui-ci appelle la même méthode d'écriture que ce dialogue et vérifie les écritures réussies, les erreurs de chemin et les filtres vides. Le banc moteur existant reste séparé du parcours et isole son orchestration avec des stubs.

## Validation du 7 octobre 2026

Le passage `2024-clash-tutorial-verified` réussit **17 contrôles, sans échec**, dans Revit 2024. Il produit les deux intersections attendues, vérifie la correction et la reconstruction répétée de la scène, parcourt les vrais événements externes, confirme les écritures HTML et CSV et restaure la vue à la fermeture. `finished.txt` contient `0` ; `exited-cleanly.txt` confirme la sortie par `OnShutdown`.

Les captures aux tailles 1180 × 760 et 1100 × 560 ont été examinées. Les compilations complètes du plugin pour Revit 2023 et 2024 réussissent. Le parcours n'a pas été exécuté nativement dans Revit 2023.

Les passages précédents ne sont pas des validations : `clash-tutorial-01` a rencontré un abonnement interdit pendant Idling et un autre banc chargé simultanément ; `clash-tutorial-isolated` utilisait une activation immédiate de vue après ouverture ; `clash-tutorial-ready` a révélé que le validateur devait reconnaître la catégorie spécifique des intersections réseau/paroi. Le banc isolé attend désormais la vue et protège son dossier avec un PID propriétaire.
