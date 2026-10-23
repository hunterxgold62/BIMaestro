# Essai manuel 2025

1. Enregistrer les projets nécessaires et fermer Revit.
2. Lancer `tmp/properties-custom-group-tests/manual-2025/Lancer-test-Revit-2025.cmd`.
3. Attendre l'ouverture de la copie d'Antibes et le message PRET du lanceur. Toute demande de confiance du complément doit être traitée manuellement.

Le lanceur charge la compilation complète 2025 et son prototype isolé, active les couleurs, l'ordre et les groupes de test, et ouvre le plan préparé. Le bouton « Apparence des propriétés » de cette compilation rejoint la fenêtre numérotée du laboratoire pour ce document.

Chaque lancement crée un nouveau dossier de session et une nouvelle copie ; aucun essai précédent n'est écrasé. Le fichier Antibes source et la DLL BIMaestro installée ne sont pas remplacés. Le manifeste BIMaestro 2025 est sauvegardé dans la session et restauré après préparation. Les autres versions Revit ne sont pas modifiées.

Le groupe est encore un prototype avec membres prédéfinis et limité à la copie ouverte par ce lanceur ; sa création libre dans n'importe quel projet n'est pas livrée. Les saisies de cette nouvelle version restent à qualifier. Ne pas présenter cette compilation comme une version de production validée.

Validation du package sans démarrage : `scripts/properties-custom-group-tests/StartManual2025.ps1 -ValidateOnly`.
