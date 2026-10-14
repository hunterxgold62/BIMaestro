# Visite en réalité virtuelle

Ouvrir le lien de la maquette dans le navigateur du Meta Quest, attendre le chargement puis choisir **Entrer en VR** et accepter l’autorisation du navigateur. Aucun logiciel supplémentaire n’est nécessaire pour la visite web.

Le bouton apparaît seulement si WebXR annonce une session `immersive-vr` compatible. Le site ne démarre jamais une session sans action de l’utilisateur. Sur PC ou smartphone sans casque compatible, le bouton reste absent et les commandes habituelles sont conservées.

## Commandes

| Action | Contrôleurs du casque | Manette Xbox connectée au casque |
| --- | --- | --- |
| Regarder | Mouvement de la tête | Mouvement de la tête |
| Marcher | Stick gauche | Stick gauche |
| Tourner de 30° | Stick droit, puis relâcher | Stick droit, puis relâcher |
| Inspecter un élément | Pointer avec un contrôleur et presser la gâchette | Regarder l’élément puis RT |
| Revenir au départ | A du contrôleur droit ou bouton dans la visite | A |
| Quitter | B du contrôleur droit ou bouton dans la visite | B |

La visite respecte l’échelle de la maquette : les coordonnées Revit en pieds sont converties en mètres pour les poses du casque. La hauteur des yeux est celle du suivi du casque. Les déplacements au stick restent au sol, avec contrôle des murs et des zones chargées. Les mouvements physiques de l’utilisateur dépendent du suivi et des limites de sécurité du casque.

Cette première version permet la visite et l’inspection des objets. Les outils de réservation, d’annotation et d’édition restent accessibles dans l’interface classique. Une édition en cours doit être terminée ou annulée avant l’entrée en VR.

## Vérifications

`npm test` vérifie la détection, les commandes, l’échelle et le pivot de rotation. `node scripts/test-vr-browser.mjs` utilise Edge et une simulation de l’API WebXR : PC, téléphone, refus d’autorisation, rendu stéréo, déplacement, sélection, rotation et restauration de la caméra. La variable `PLAYWRIGHT_MODULE` peut indiquer une installation Playwright existante et `VIEWER_TEST_URL` le serveur local.

Un Meta Quest réel reste nécessaire pour valider le confort, le suivi physique, le point de départ et les performances sur les maquettes volumineuses.
