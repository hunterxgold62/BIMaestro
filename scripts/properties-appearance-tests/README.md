# Apparence des propriétés

Bouton **BIMaestro → Couleur et information → Apparence propriétés**.

Sélectionner une vue ou un seul élément, ouvrir le bouton, puis choisir la portée (générale, catégorie ou nom de famille/type), le groupe ou le paramètre et les couleurs. Les variantes clair/sombre sont indépendantes ; la variante active suit le thème Revit. Actualiser la sélection permet de garder la fenêtre ouverte en changeant de contexte. Une sélection multiple reste native.

**Tester en direct dans Revit** est temporaire ; fermer l'annule. **Enregistrer mon affichage** conserve les règles dans `%APPDATA%/BIMaestro/PropertiesAppearance.json`. Décocher l'activation et enregistrer restaure le dessin natif sans effacer les règles. Vider un profil ne retire pas les profils plus généraux. Un profil type utilise catégorie + famille/type (ou ViewType pour une vue), et s'applique donc aux mêmes noms dans les autres projets ; il ne cible pas une instance unique.

Le dessin réversible suit les identités et rectangles des vraies cellules natives. Aucun paramètre du modèle n'est écrit. Les champs en lecture seule, paramètres homonymes ambigus et éditeurs actifs gardent leur rendu Revit. La couleur du texte peut être corrigée pour maintenir un contraste de 4,5. Les réglages prioritaires sont ceux du type, puis de la catégorie, puis généraux ; dans une même portée, un paramètre prime sur son groupe. Une variante vide hérite du profil plus général.

## Validation du 20 octobre 2026

- Compilations complètes Revit 2023, 2024, 2025 et 2026 : réussies (avertissements existants).
- Revit 2025.5 : ouverture depuis le bouton réel du ruban ; aperçu de Graphismes (16 lignes reconnues) ; texte vert et fond vert pâle appliqués aux cellules ; champ grisé Valeur de l'échelle conservé ; défilement ; enregistrement, fermeture et réouverture du profil ; désactivation et retour au rendu natif.
- Les couleurs et leur persistance sont vérifiées visuellement en mode clair sur 2025. Les autres versions et le rendu natif sombre ne sont pas encore qualifiés visuellement pour ce nouveau bouton.
- Le module natif vérifie l'empreinte MFC, les exports Objective Grid et le RTTI avant de lire la grille. Un binaire non reconnu conserve les préférences sans appliquer le dessin. Ce garde-fou implique qu'une mise à jour Revit/MFC peut désactiver le rendu jusqu'à validation du nouveau binaire.

Les captures dans `evidence/` sont de vraies captures Revit. Le petit complément de validation temporaire n'est pas livré avec le plugin.

## Tests des préférences

`dotnet run --project scripts/properties-appearance-tests/Verify.csproj`

Ces tests lient le modèle de préférences de production et utilisent un fichier temporaire isolé. Ils couvrent la priorité des portées, l'héritage des variantes, le contraste, la fusion de sauvegardes entre sessions, le retrait d'un profil et la conservation d'un fichier corrompu.

## Éditeur visuel, ordre et réinitialisation

L’éditeur compare le rendu d’origine à votre aperçu. Un clic sur le titre d’un groupe replie ou déplie ses paramètres ; une recherche révèle temporairement les correspondances. Les poignées déplacent les groupes entiers ou les paramètres dans leur groupe d’origine dans l’aperçu. Les ordres sont enregistrés, mais **leur application à la palette native est suspendue** : les essais MoveRows conservent les cellules visuelles, mais un clic peut déclencher une reconstruction des lignes par Revit. La validation de l’édition native après déplacement n’est donc pas acquise. Les déplacements entre groupes restent désactivés.

**Réinitialiser tout…** demande une confirmation dans la fenêtre, puis retire tous les profils, ordres et variantes de couleur et désactive la personnalisation. Les tests utilisent un fichier isolé et ne réinitialisent pas les préférences réelles de l’utilisateur.

`dotnet run --project scripts/properties-appearance-tests/editor/EditorFlow.csproj`

Ces 20 vérifications WPF lient le code de production de l’éditeur, du sélecteur de couleur, de l’ordre et des préférences. Seul le contexte Revit et son service d’application sont simulés. Avec les 10 vérifications des préférences, les 30 vérifications passent.

## Mesures et limites

Deux observations Revit 2025 avec palette visible ont permis d’identifier le coût de la recherche répétée du module natif. Le cache du contrôle et de la vérification du module ramène le rafraîchissement moyen de 571,159 ms à 2,159 ms (95e percentile 2,869 ms, maximum 4,404 ms dans la seconde observation). Le dessin des couleurs prend environ 3,6 ms sur les deux peintures mesurées après optimisation.

Les fichiers JSON bruts sont dans `evidence/`. Le premier porte le nom historique `bench-colors-scroll.json`, mais sa série de défilements automatisés a été interrompue : ce n’est pas un banc de défilement contrôlé. Une action de défilement a été exécutée lors de la seconde observation. Ces observations ne constituent ni une mesure exhaustive de latence des clics, ni une qualification de grandes familles ou des quatre versions. La charge CPU globale de Revit était déjà d’environ un cœur sans personnalisation ; elle ne permet pas d’attribuer cette charge au bouton. L’instrumentation est inactive par défaut et ne lance aucun minuteur de mesure dans le plugin livré.
