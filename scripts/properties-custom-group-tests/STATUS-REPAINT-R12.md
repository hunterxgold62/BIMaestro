# Prototype r12 — transition visuelle

Une barrière de présentation native, limitée à 750 ms, est installée uniquement dans le laboratoire Revit 2025. Sur SelectionChanged, elle conserve le dernier affichage jusqu'au passage API qui vérifie et réapplique le groupe. WM_PAINT est différé sans WM_SETREDRAW (la grille reste détectable). Toute entrée clavier/souris ou prise de focus libère immédiatement l'affichage avant traitement natif. Aucun accès DB depuis la procédure de fenêtre ; aucune validation d'éditeur.

Le test complet de transition est EN ATTENTE : une boîte de sécurité d'un autre complément (BIMaestro Antibes mass stress observer) est apparue pendant l'essai UI. Aucun clic sur cette boîte n'a été automatisé. Aucun résultat visuel anti-rebond concluant n'est revendiqué.

La fenêtre WPF « Apparence des propriétés · Prototype numéroté 2025-r12 » a été observée dans Revit : badges 1 et 1.1 à 1.8 présents dans les deux colonnes. La fenêtre a été fermée sans enregistrer pour les essais de sélection. Le bouton du plugin installé reste sur l'ancienne DLL ; les repères sont vérifiés dans la fenêtre du laboratoire.

Compilation r12 réussie, tests du collecteur 15/15. Ces tests ne remplacent pas les essais visuels de la barrière ni un nouvel essai de saisie avec celle-ci active. Reprendre les essais de sélection/désélection et de saisie après traitement manuel de la boîte de sécurité, puis réouvrir la fenêtre numérotée via Send.ps1 -Run 2025-r12 -Command editor.
